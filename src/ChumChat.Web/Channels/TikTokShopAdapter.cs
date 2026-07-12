using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChumChat.Web.Data;
using ChumChat.Web.Services;

namespace ChumChat.Web.Channels;

// TikTok Shop Open Platform, Customer Service API — https://partner.tiktokshop.com/docv2
public class TikTokShopAdapter(
    ChannelSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    ILogger<TikTokShopAdapter> logger) : IChannelAdapter
{
    private TikTokShopOptions Opts => settings.TikTokShop;

    public ChannelType Channel => ChannelType.TikTokShop;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(Opts.AppKey) &&
        !string.IsNullOrEmpty(Opts.AppSecret) &&
        !string.IsNullOrEmpty(Opts.AccessToken);

    // Chữ ký webhook TikTok: Authorization = HMACSHA256(appSecret, appKey + rawBody)
    public bool VerifySignature(string rawBody, IHeaderDictionary headers, string requestUrl)
    {
        if (string.IsNullOrEmpty(Opts.AppSecret))
            return true;

        var signature = headers["Authorization"].ToString();
        if (string.IsNullOrEmpty(signature))
            return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Opts.AppSecret));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(Opts.AppKey + rawBody))).ToLowerInvariant();
        return signature.Equals(hash, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<InboundMessage> ParseWebhook(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("data", out var data))
            return [];

        // Webhook NEW_CONVERSATION_MESSAGE: data chứa conversation_id + nội dung tin
        var conversationId = data.TryGetProperty("conversation_id", out var cid) ? cid.GetString() : null;
        if (string.IsNullOrEmpty(conversationId))
        {
            logger.LogDebug("TikTok: webhook không có conversation_id, bỏ qua");
            return [];
        }

        var type = data.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (type is not (null or "TEXT" or "IMAGE"))
            return [];

        // content là chuỗi JSON lồng: TEXT → {"content":"..."}, IMAGE → {"url":"..."}
        var text = "";
        string? attachmentUrl = null;
        if (data.TryGetProperty("content", out var contentEl))
        {
            var raw = contentEl.GetString() ?? "";
            try
            {
                using var contentDoc = JsonDocument.Parse(raw);
                text = contentDoc.RootElement.TryGetProperty("content", out var innerText)
                    ? innerText.GetString() ?? ""
                    : "";
                attachmentUrl = contentDoc.RootElement.TryGetProperty("url", out var u) ? u.GetString() : null;
            }
            catch (JsonException)
            {
                text = raw;
            }
        }
        if (string.IsNullOrEmpty(text) && attachmentUrl is null)
            return [];

        var senderName = "Khách TikTok";
        if (data.TryGetProperty("sender", out var sender))
        {
            // Bỏ qua tin do chính shop gửi (từ app khác hoặc app này)
            if (sender.TryGetProperty("role", out var role) && role.GetString() != "BUYER")
                return [];
            if (sender.TryGetProperty("nickname", out var nick))
                senderName = nick.GetString() ?? senderName;
        }

        var msgId = data.TryGetProperty("message_id", out var mi) ? mi.GetString() : null;

        var sentAt = DateTime.UtcNow;
        if (data.TryGetProperty("create_time", out var ts) && ts.TryGetInt64(out var unix))
            sentAt = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

        return [new InboundMessage(conversationId, senderName, text, msgId, sentAt, attachmentUrl)];
    }

    // Chữ ký TikTok: HMACSHA256(appSecret, appSecret + path + key1value1key2value2... + body + appSecret)
    private string BuildSignedUrl(string path, SortedDictionary<string, string> queryParams, string body = "")
    {
        queryParams["app_key"] = Opts.AppKey;
        queryParams["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();

        var baseString = new StringBuilder(Opts.AppSecret).Append(path);
        foreach (var (key, value) in queryParams)
            baseString.Append(key).Append(value);
        baseString.Append(body).Append(Opts.AppSecret);

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Opts.AppSecret));
        var sign = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString.ToString()))).ToLowerInvariant();

        var query = string.Join("&", queryParams.Select(kv => $"{kv.Key}={Uri.EscapeDataString(kv.Value)}"));
        return $"{Opts.ApiBaseUrl}{path}?{query}&sign={sign}";
    }

    public async Task<string?> SendTextAsync(Conversation conversation, string text, CancellationToken ct = default)
    {
        var path = $"/customer_service/202309/conversations/{conversation.ExternalId}/messages";
        var body = JsonSerializer.Serialize(new
        {
            type = "TEXT",
            content = JsonSerializer.Serialize(new { content = text })
        });
        var url = BuildSignedUrl(path, new SortedDictionary<string, string>
        {
            ["shop_cipher"] = Opts.ShopCipher
        }, body);

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-tts-access-token", Opts.AccessToken);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await client.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            throw new InvalidOperationException($"TikTok API error: {responseBody}");
        return TikTokMessageId(doc);
    }

    private static string? TikTokMessageId(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("message_id", out var m)
            ? m.GetString() : null;

    // TikTok bắt buộc upload ảnh qua API riêng, rồi gửi tin IMAGE với URL trả về
    public async Task<string?> SendImageAsync(Conversation conversation, string imageUrl, byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient();

        // Upload: body multipart không tính vào chữ ký
        var uploadUrl = BuildSignedUrl("/customer_service/202309/images/upload",
            new SortedDictionary<string, string>());
        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(imageBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(fileContent, "data", fileName);

        using var uploadRequest = new HttpRequestMessage(HttpMethod.Post, uploadUrl) { Content = form };
        uploadRequest.Headers.Add("x-tts-access-token", Opts.AccessToken);
        var uploadResponse = await client.SendAsync(uploadRequest, ct);
        var uploadBody = await uploadResponse.Content.ReadAsStringAsync(ct);
        using var uploadDoc = JsonDocument.Parse(uploadBody);
        var tikTokUrl = uploadDoc.RootElement.TryGetProperty("data", out var uploadData) &&
                        uploadData.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(tikTokUrl))
            throw new InvalidOperationException($"TikTok upload ảnh lỗi: {uploadBody}");

        var path = $"/customer_service/202309/conversations/{conversation.ExternalId}/messages";
        var body = JsonSerializer.Serialize(new
        {
            type = "IMAGE",
            content = JsonSerializer.Serialize(new { url = tikTokUrl })
        });
        var url = BuildSignedUrl(path, new SortedDictionary<string, string>
        {
            ["shop_cipher"] = Opts.ShopCipher
        }, body);

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Add("x-tts-access-token", Opts.AccessToken);
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        var response = await client.SendAsync(request, ct);
        var responseBody = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(responseBody);
        if (doc.RootElement.TryGetProperty("code", out var code) && code.GetInt32() != 0)
            throw new InvalidOperationException($"TikTok API error: {responseBody}");
        return TikTokMessageId(doc);
    }

    // TikTok CSKH không gửi file tùy ý — gửi tạm đường link
    public Task<string?> SendFileAsync(Conversation conversation, string fileUrl, byte[] fileBytes, string fileName, string mimeType, CancellationToken ct = default) =>
        SendTextAsync(conversation, $"{fileName}: {fileUrl}", ct);

    // TikTok webhook đã kèm nickname; không lấy avatar riêng
    public Task<CustomerProfile?> FetchProfileAsync(string externalId, CancellationToken ct = default) =>
        Task.FromResult<CustomerProfile?>(null);

    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(new ConnectionTestResult(IsConfigured,
            IsConfigured ? "Đã điền credentials TikTok Shop." : "Chưa kết nối TikTok Shop."));

    // Đồng bộ tin cũ: /conversations rồi /conversations/{id}/messages
    public async Task<IReadOnlyList<HistoryMessage>> FetchHistoryAsync(int maxConversations, CancellationToken ct = default)
    {
        var result = new List<HistoryMessage>();

        var convBody = await SignedGetAsync("/customer_service/202309/conversations",
            new SortedDictionary<string, string>
            {
                ["shop_cipher"] = Opts.ShopCipher,
                ["page_size"] = maxConversations.ToString()
            }, ct);
        using var convDoc = JsonDocument.Parse(convBody);
        if (!convDoc.RootElement.TryGetProperty("data", out var convData) ||
            !convData.TryGetProperty("conversations", out var convs))
            throw new InvalidOperationException($"TikTok /conversations lỗi: {convBody}");

        foreach (var conv in convs.EnumerateArray())
        {
            var conversationId = conv.TryGetProperty("id", out var cid) ? cid.GetString() : null;
            if (string.IsNullOrEmpty(conversationId))
                continue;

            var buyerName = "";
            if (conv.TryGetProperty("participants", out var participants))
            {
                foreach (var p in participants.EnumerateArray())
                {
                    if (p.TryGetProperty("role", out var role) && role.GetString() == "BUYER")
                        buyerName = p.TryGetProperty("nickname", out var nick) ? nick.GetString() ?? "" : "";
                }
            }

            var msgBody = await SignedGetAsync($"/customer_service/202309/conversations/{conversationId}/messages",
                new SortedDictionary<string, string>
                {
                    ["shop_cipher"] = Opts.ShopCipher,
                    ["page_size"] = "30"
                }, ct);
            using var msgDoc = JsonDocument.Parse(msgBody);
            if (!msgDoc.RootElement.TryGetProperty("data", out var msgData) ||
                !msgData.TryGetProperty("messages", out var msgs))
                continue;

            foreach (var msg in msgs.EnumerateArray())
            {
                if ((msg.TryGetProperty("type", out var ty) ? ty.GetString() : "TEXT") != "TEXT")
                    continue;

                // content là chuỗi JSON lồng: {"content":"..."}
                var text = "";
                if (msg.TryGetProperty("content", out var contentEl))
                {
                    var raw = contentEl.GetString() ?? "";
                    try
                    {
                        using var contentDoc = JsonDocument.Parse(raw);
                        text = contentDoc.RootElement.TryGetProperty("content", out var inner)
                            ? inner.GetString() ?? raw : raw;
                    }
                    catch (JsonException)
                    {
                        text = raw;
                    }
                }
                if (string.IsNullOrEmpty(text))
                    continue;

                var isBuyer = msg.TryGetProperty("sender", out var sender) &&
                              sender.TryGetProperty("role", out var role) &&
                              role.GetString() == "BUYER";

                var sentAt = DateTime.UtcNow;
                if (msg.TryGetProperty("create_time", out var ctEl) && ctEl.TryGetInt64(out var unix))
                    sentAt = DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime;

                result.Add(new HistoryMessage(
                    conversationId,
                    buyerName,
                    isBuyer ? MessageDirection.Inbound : MessageDirection.Outbound,
                    text,
                    msg.TryGetProperty("id", out var mi) ? mi.GetString() : null,
                    sentAt));
            }
        }
        return result;
    }

    private async Task<string> SignedGetAsync(string path, SortedDictionary<string, string> queryParams, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BuildSignedUrl(path, queryParams));
        request.Headers.Add("x-tts-access-token", Opts.AccessToken);
        var response = await client.SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }
}
