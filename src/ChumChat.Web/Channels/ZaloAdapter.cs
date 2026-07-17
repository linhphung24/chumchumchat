using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChumChat.Web.Data;
using ChumChat.Web.Services;

namespace ChumChat.Web.Channels;

// Zalo Official Account API v3 — https://developers.zalo.me/docs
public class ZaloAdapter(
    ChannelSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    ILogger<ZaloAdapter> logger) : IChannelAdapter
{
    private ZaloOptions Opts => settings.Zalo;
    public string? LastVerificationError { get; set; }

    public ChannelType Channel => ChannelType.Zalo;

    public bool IsConfigured => !string.IsNullOrEmpty(Opts.AccessToken);

    // Chữ ký Zalo: X-ZEvent-Signature = "mac=" + SHA256(appId + rawBody + timestamp + oaSecretKey)
    public bool VerifySignature(string rawBody, IHeaderDictionary headers, string requestUrl)
    {
        if (string.IsNullOrEmpty(Opts.OaSecretKey))
            return true; // chưa cấu hình secret → bỏ qua xác minh (chỉ dùng khi dev)

        var signature = headers["X-ZEvent-Signature"].ToString();
        if (string.IsNullOrEmpty(signature))
            return false;

        string timestamp;
        try
        {
            using var doc = JsonDocument.Parse(rawBody);
            timestamp = doc.RootElement.TryGetProperty("timestamp", out var ts)
                ? ts.ToString()
                : "";
        }
        catch (JsonException)
        {
            return false;
        }

        var data = Opts.AppId + rawBody + timestamp + Opts.OaSecretKey;
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
        
        var isValid = signature.Equals("mac=" + hash, StringComparison.OrdinalIgnoreCase)
            || signature.Equals(hash, StringComparison.OrdinalIgnoreCase);

        if (!isValid)
        {
            LastVerificationError = $"Expected: {hash}, Actual: {signature}, HashedData: {data}";
            logger.LogWarning("Zalo signature verification failed!\nExpected hash: {ExpectedHash}\nActual signature: {ActualSignature}\nData string: '{Data}'", 
                hash, signature, data);
        }
        else
        {
            LastVerificationError = null;
        }

        return isValid;
    }

    public IReadOnlyList<InboundMessage> ParseWebhook(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        var eventName = root.TryGetProperty("event_name", out var ev) ? ev.GetString() : null;
        if (eventName is not ("user_send_text" or "user_send_image"))
        {
            logger.LogDebug("Zalo: bỏ qua sự kiện {Event}", eventName);
            return [];
        }

        var senderId = root.GetProperty("sender").GetProperty("id").GetString() ?? "";
        var message = root.GetProperty("message");
        var text = message.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var msgId = message.TryGetProperty("msg_id", out var m) ? m.GetString() : null;

        // user_send_image: URL ảnh nằm trong attachments[0].payload.url (hoặc thumbnail)
        string? attachmentUrl = null;
        if (message.TryGetProperty("attachments", out var atts) && atts.GetArrayLength() > 0 &&
            atts[0].TryGetProperty("payload", out var payload))
        {
            attachmentUrl = payload.TryGetProperty("url", out var u) ? u.GetString()
                : payload.TryGetProperty("thumbnail", out var th) ? th.GetString() : null;
        }

        var sentAt = DateTime.UtcNow;
        if (root.TryGetProperty("timestamp", out var ts) && long.TryParse(ts.ToString(), out var unixMs))
            sentAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;

        // Webhook Zalo không kèm tên hiển thị; dùng đuôi id làm tên tạm,
        // có thể gọi API getprofile để lấy tên thật sau.
        return [new InboundMessage(senderId, $"Zalo …{senderId[Math.Max(0, senderId.Length - 4)..]}", text, msgId, sentAt, attachmentUrl)];
    }

    public async Task<string?> SendTextAsync(Conversation conversation, string text, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openapi.zalo.me/v3.0/oa/message/cs");
        request.Headers.Add("access_token", Opts.AccessToken);
        request.Content = JsonContent(new
        {
            recipient = new { user_id = conversation.ExternalId },
            message = new { text }
        });

        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();

        // Zalo trả HTTP 200 kể cả khi lỗi — mã lỗi nằm trong body
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err) && err.GetInt32() != 0)
            throw new InvalidOperationException($"Zalo API error: {body}");
        return ExtractMessageId(doc);
    }

    // Gửi ảnh qua template media với URL công khai
    public async Task<string?> SendImageAsync(Conversation conversation, string imageUrl, byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openapi.zalo.me/v3.0/oa/message/cs");
        request.Headers.Add("access_token", Opts.AccessToken);
        request.Content = JsonContent(new
        {
            recipient = new { user_id = conversation.ExternalId },
            message = new
            {
                attachment = new
                {
                    type = "template",
                    payload = new
                    {
                        template_type = "media",
                        elements = new[] { new { media_type = "image", url = imageUrl } }
                    }
                }
            }
        });

        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err) && err.GetInt32() != 0)
            throw new InvalidOperationException($"Zalo API error: {body}");
        return ExtractMessageId(doc);
    }

    // Gửi file: upload lên Zalo lấy token, rồi gửi tin attachment type=file
    public async Task<string?> SendFileAsync(Conversation conversation, string fileUrl, byte[] fileBytes, string fileName, string mimeType, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient();

        using var form = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent(fileBytes);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(
            string.IsNullOrEmpty(mimeType) ? "application/octet-stream" : mimeType);
        form.Add(fileContent, "file", fileName);

        using var uploadReq = new HttpRequestMessage(HttpMethod.Post, "https://openapi.zalo.me/v2.0/oa/upload/file") { Content = form };
        uploadReq.Headers.Add("access_token", Opts.AccessToken);
        var uploadResp = await client.SendAsync(uploadReq, ct);
        var uploadBody = await uploadResp.Content.ReadAsStringAsync(ct);
        using var uploadDoc = JsonDocument.Parse(uploadBody);
        var token = uploadDoc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("token", out var tk)
            ? tk.GetString() : null;
        if (string.IsNullOrEmpty(token))
            throw new InvalidOperationException($"Zalo upload file lỗi: {uploadBody}");

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://openapi.zalo.me/v3.0/oa/message/cs");
        request.Headers.Add("access_token", Opts.AccessToken);
        request.Content = JsonContent(new
        {
            recipient = new { user_id = conversation.ExternalId },
            message = new { attachment = new { type = "file", payload = new { token } } }
        });
        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("error", out var err) && err.GetInt32() != 0)
            throw new InvalidOperationException($"Zalo API error: {body}");
        return ExtractMessageId(doc);
    }

    // message_id nằm trong data.message_id của response
    private static string? ExtractMessageId(JsonDocument doc) =>
        doc.RootElement.TryGetProperty("data", out var data) && data.TryGetProperty("message_id", out var mid)
            ? mid.ToString() : null;

    // Đồng bộ tin cũ: listrecentchat lấy hội thoại gần nhất, rồi /oa/conversation lấy tin từng người
    public async Task<IReadOnlyList<HistoryMessage>> FetchHistoryAsync(int maxConversations, CancellationToken ct = default)
    {
        var result = new List<HistoryMessage>();
        var userIds = new HashSet<string>();

        // Zalo giới hạn count tối đa 10 mỗi trang
        for (var offset = 0; userIds.Count < maxConversations; offset += 10)
        {
            // API hội thoại của Zalo vẫn ở v2.0 (v3.0 chỉ có nhóm API gửi tin)
            var body = await GetAsync($"https://openapi.zalo.me/v2.0/oa/listrecentchat?data=" +
                Uri.EscapeDataString($"{{\"offset\":{offset},\"count\":10}}"), ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                if (offset == 0)
                    throw new InvalidOperationException($"Zalo listrecentchat lỗi: {body}");
                break;
            }
            var count = 0;
            foreach (var item in data.EnumerateArray())
            {
                count++;
                // src = 1: tin từ khách (from_id là khách); src = 0: tin OA gửi (to_id là khách)
                var src = item.TryGetProperty("src", out var s) && s.TryGetInt32(out var srcVal) ? srcVal : 1;
                var partner = src == 1
                    ? item.TryGetProperty("from_id", out var f) ? f.ToString() : null
                    : item.TryGetProperty("to_id", out var t) ? t.ToString() : null;
                if (!string.IsNullOrEmpty(partner))
                    userIds.Add(partner);
            }
            if (count < 10)
                break;
        }

        foreach (var userId in userIds.Take(maxConversations))
        {
            // Tối đa 30 tin gần nhất mỗi hội thoại (3 trang x 10)
            for (var offset = 0; offset < 30; offset += 10)
            {
                var body = await GetAsync($"https://openapi.zalo.me/v2.0/oa/conversation?data=" +
                    Uri.EscapeDataString($"{{\"user_id\":{userId},\"offset\":{offset},\"count\":10}}"), ct);
                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
                    break;

                var count = 0;
                foreach (var msg in data.EnumerateArray())
                {
                    count++;
                    var type = msg.TryGetProperty("type", out var ty) ? ty.GetString() : "text";
                    var text = msg.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                    if (type is not (null or "text") || string.IsNullOrEmpty(text))
                        continue;

                    var src = msg.TryGetProperty("src", out var s) && s.TryGetInt32(out var srcVal) ? srcVal : 1;
                    var name = src == 1 && msg.TryGetProperty("from_display_name", out var dn)
                        ? dn.GetString() ?? ""
                        : "";
                    var sentAt = DateTime.UtcNow;
                    if (msg.TryGetProperty("time", out var tm) && tm.TryGetInt64(out var unixMs))
                        sentAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;

                    result.Add(new HistoryMessage(
                        userId,
                        name,
                        src == 1 ? MessageDirection.Inbound : MessageDirection.Outbound,
                        text,
                        msg.TryGetProperty("message_id", out var mi) ? mi.ToString() : null,
                        sentAt));
                }
                if (count < 10)
                    break;
            }
        }
        return result;
    }

    // Zalo OA getprofile: trả display_name + avatar (avatars.240 là ảnh 240px)
    public async Task<CustomerProfile?> FetchProfileAsync(string externalId, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;
        try
        {
            var body = await GetAsync("https://openapi.zalo.me/v3.0/oa/user/detail?data=" +
                Uri.EscapeDataString($"{{\"user_id\":\"{externalId}\"}}"), ct);
            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("data", out var data))
                return null;
            var name = data.TryGetProperty("display_name", out var n) ? n.GetString() : null;
            string? avatar = null;
            if (data.TryGetProperty("avatars", out var avatars))
                avatar = avatars.TryGetProperty("240", out var a240) ? a240.GetString()
                    : avatars.TryGetProperty("120", out var a120) ? a120.GetString() : null;
            if (string.IsNullOrEmpty(avatar) && data.TryGetProperty("avatar", out var av))
                avatar = av.GetString();
            return new CustomerProfile(name, avatar);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Zalo: không lấy được profile của {UserId}", externalId);
            return null;
        }
    }

    // Gọi getoa để xác nhận access token còn sống
    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new(false, "Chưa kết nối (chưa có access token). Bấm 'Kết nối Zalo'.");
        try
        {
            var body = await GetAsync("https://openapi.zalo.me/v2.0/oa/getoa", ct);
            using var doc = JsonDocument.Parse(body);
            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetInt32() : -1;
            if (error == 0)
            {
                var name = doc.RootElement.TryGetProperty("data", out var d) && d.TryGetProperty("name", out var n)
                    ? n.GetString() : null;
                return new(true, $"Token còn sống — OA: {name ?? "?"}. Gửi/nhận tin và lấy avatar sẽ hoạt động.");
            }
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : body;
            return new(false, $"Zalo báo lỗi (error {error}): {msg}. Token có thể đã hết hạn — bấm 'Kết nối Zalo' để làm mới.");
        }
        catch (Exception ex)
        {
            return new(false, $"Không gọi được API Zalo: {ex.Message}");
        }
    }

    private async Task<string> GetAsync(string url, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("access_token", Opts.AccessToken);
        var response = await client.SendAsync(request, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    internal static StringContent JsonContent(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
}
