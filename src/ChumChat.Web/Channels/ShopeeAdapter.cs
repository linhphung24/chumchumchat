using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChumChat.Web.Data;
using ChumChat.Web.Services;

namespace ChumChat.Web.Channels;

// Shopee Open Platform v2, sellerchat — https://open.shopee.com
public class ShopeeAdapter(
    ChannelSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    ILogger<ShopeeAdapter> logger) : IChannelAdapter
{
    private ShopeeOptions Opts => settings.Shopee;

    public ChannelType Channel => ChannelType.Shopee;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(Opts.PartnerId) &&
        !string.IsNullOrEmpty(Opts.PartnerKey) &&
        !string.IsNullOrEmpty(Opts.AccessToken);

    // Chữ ký push của Shopee: Authorization = HMACSHA256(partnerKey, url + "|" + rawBody)
    public bool VerifySignature(string rawBody, IHeaderDictionary headers, string requestUrl)
    {
        if (string.IsNullOrEmpty(Opts.PartnerKey))
            return true;

        var signature = headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(signature))
            return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Opts.PartnerKey));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(requestUrl + "|" + rawBody))).ToLowerInvariant();
        return signature.Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<InboundMessage> ParseWebhook(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        // code 10 = webchat message push
        if (!root.TryGetProperty("code", out var code) || code.GetInt32() != 10)
        {
            logger.LogDebug("Shopee: bỏ qua push code {Code}", root.TryGetProperty("code", out var c) ? c.ToString() : "?");
            return [];
        }

        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("content", out var content))
            return [];

        var messageType = content.TryGetProperty("message_type", out var mt) ? mt.GetString() : null;
        if (messageType is not ("text" or "image"))
            return [];

        var fromId = content.TryGetProperty("from_id", out var f) ? f.ToString() : "";
        var fromName = content.TryGetProperty("from_user_name", out var fn) ? fn.GetString() ?? fromId : fromId;
        var msgId = content.TryGetProperty("message_id", out var mi) ? mi.GetString() : null;

        var text = "";
        string? attachmentUrl = null;
        if (content.TryGetProperty("content", out var inner))
        {
            text = inner.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
            attachmentUrl = inner.TryGetProperty("image_url", out var iu) ? iu.GetString()
                : inner.TryGetProperty("url", out var u) ? u.GetString()
                : inner.TryGetProperty("thumb_url", out var tu) ? tu.GetString() : null;
        }
        if (string.IsNullOrEmpty(text) && attachmentUrl is null)
            return [];

        // Shop cũng nhận push cho tin mình tự gửi — bỏ qua để không tự nhân đôi
        if (fromId == Opts.ShopId)
            return [];

        var sentAt = DateTime.UtcNow;
        if (content.TryGetProperty("created_timestamp", out var ts) && ts.TryGetInt64(out var unix))
            sentAt = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        return [new InboundMessage(fromId, fromName, text, msgId, sentAt, attachmentUrl)];
    }

    // Chữ ký request Shopee: HMACSHA256(partnerKey, partnerId + path + timestamp + accessToken + shopId)
    private string BuildSignedUrl(string path, string extraQuery = "")
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var baseString = Opts.PartnerId + path + timestamp + Opts.AccessToken + Opts.ShopId;
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Opts.PartnerKey));
        var sign = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString))).ToLowerInvariant();
        return $"{Opts.ApiBaseUrl}{path}?partner_id={Opts.PartnerId}&timestamp={timestamp}" +
               $"&access_token={Opts.AccessToken}&shop_id={Opts.ShopId}&sign={sign}{extraQuery}";
    }

    public async Task<string?> SendTextAsync(Conversation conversation, string text, CancellationToken ct = default)
    {
        var url = BuildSignedUrl("/api/v2/sellerchat/send_message");

        var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync(url, ZaloAdapter.JsonContent(new
        {
            to_id = long.Parse(conversation.ExternalId),
            message_type = "text",
            content = new { text }
        }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err) &&
            !string.IsNullOrEmpty(err.GetString()))
            throw new InvalidOperationException($"Shopee API error: {body}");
        return ShopeeMessageId(doc);
    }

    private static string? ShopeeMessageId(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("response", out var r) && r.TryGetProperty("message_id", out var m)
            ? m.ToString() : null;

    // Shopee bắt buộc upload ảnh qua API riêng trước, rồi gửi bằng URL Shopee trả về
    public async Task<string?> SendImageAsync(Conversation conversation, string imageUrl, byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(imageBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(fileContent, "file", fileName);

        var uploadResponse = await client.PostAsync(BuildSignedUrl("/api/v2/sellerchat/upload_image"), form, ct);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync(ct);
        using var uploadDoc = JsonDocument.Parse(uploadBody);
        string? shopeeUrl = null;
        if (uploadDoc.RootElement.TryGetProperty("response", out var resp))
        {
            shopeeUrl = resp.TryGetProperty("url", out var u) ? u.GetString()
                : resp.TryGetProperty("image_url", out var iu) ? iu.GetString() : null;
        }
        if (string.IsNullOrEmpty(shopeeUrl))
            throw new InvalidOperationException($"Shopee upload_image lỗi: {uploadBody}");

        var response = await client.PostAsync(BuildSignedUrl("/api/v2/sellerchat/send_message"), ZaloAdapter.JsonContent(new
        {
            to_id = long.Parse(conversation.ExternalId),
            message_type = "image",
            content = new { image_url = shopeeUrl }
        }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err) && !string.IsNullOrEmpty(err.GetString()))
            throw new InvalidOperationException($"Shopee API error: {body}");
        return ShopeeMessageId(doc);
    }

    // Shopee sellerchat không gửi file tùy ý tiện lợi — gửi tạm đường link để khách tải
    public Task<string?> SendFileAsync(Conversation conversation, string fileUrl, byte[] fileBytes, string fileName, string mimeType, CancellationToken ct = default) =>
        SendTextAsync(conversation, $"{fileName}: {fileUrl}", ct);

    // Shopee webhook đã kèm from_user_name; không có API avatar riêng tiện dùng
    public Task<CustomerProfile?> FetchProfileAsync(string externalId, CancellationToken ct = default) =>
        Task.FromResult<CustomerProfile?>(null);

    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(new ConnectionTestResult(IsConfigured,
            IsConfigured ? "Đã điền credentials Shopee." : "Chưa kết nối Shopee."));

    // Đồng bộ tin cũ: get_conversation_list rồi get_message từng hội thoại
    public async Task<IReadOnlyList<HistoryMessage>> FetchHistoryAsync(int maxConversations, CancellationToken ct = default)
    {
        var result = new List<HistoryMessage>();
        var client = httpClientFactory.CreateClient();

        var convBody = await client.GetStringAsync(
            BuildSignedUrl("/api/v2/sellerchat/get_conversation_list",
                $"&direction=latest&type=all&page_size={maxConversations}"), ct);
        using var convDoc = JsonDocument.Parse(convBody);
        if (!convDoc.RootElement.TryGetProperty("response", out var convResp) ||
            !convResp.TryGetProperty("conversations", out var convs))
            throw new InvalidOperationException($"Shopee get_conversation_list lỗi: {convBody}");

        foreach (var conv in convs.EnumerateArray())
        {
            var conversationId = conv.TryGetProperty("conversation_id", out var cid) ? cid.ToString() : null;
            if (string.IsNullOrEmpty(conversationId))
                continue;
            var buyerName = conv.TryGetProperty("to_name", out var tn) ? tn.GetString() ?? "" : "";

            var msgBody = await client.GetStringAsync(
                BuildSignedUrl("/api/v2/sellerchat/get_message",
                    $"&conversation_id={conversationId}&page_size=30"), ct);
            using var msgDoc = JsonDocument.Parse(msgBody);
            if (!msgDoc.RootElement.TryGetProperty("response", out var msgResp) ||
                !msgResp.TryGetProperty("messages", out var msgs))
                continue;

            foreach (var msg in msgs.EnumerateArray())
            {
                if ((msg.TryGetProperty("message_type", out var mt) ? mt.GetString() : "text") != "text")
                    continue;
                var text = msg.TryGetProperty("content", out var c) && c.TryGetProperty("text", out var t)
                    ? t.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(text))
                    continue;

                var fromId = msg.TryGetProperty("from_id", out var f) ? f.ToString() : "";
                var toId = msg.TryGetProperty("to_id", out var to) ? to.ToString() : "";
                var isFromShop = fromId == Opts.ShopId;
                // ExternalId của hội thoại = id người mua (khớp với webhook và send_message)
                var buyerId = isFromShop ? toId : fromId;

                var sentAt = DateTime.UtcNow;
                if (msg.TryGetProperty("created_timestamp", out var ts) && ts.TryGetInt64(out var unix))
                    sentAt = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

                result.Add(new HistoryMessage(
                    buyerId,
                    buyerName,
                    isFromShop ? MessageDirection.Outbound : MessageDirection.Inbound,
                    text,
                    msg.TryGetProperty("message_id", out var mi) ? mi.GetString() : null,
                    sentAt));
            }
        }
        return result;
    }
}
