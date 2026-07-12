using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChumChat.Web.Data;
using ChumChat.Web.Services;

namespace ChumChat.Web.Channels;

// Facebook Messenger Platform — https://developers.facebook.com/docs/messenger-platform
public class MessengerAdapter(
    ChannelSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    ILogger<MessengerAdapter> logger) : IChannelAdapter
{
    private MessengerOptions Opts => settings.Messenger;

    public ChannelType Channel => ChannelType.Messenger;

    public bool IsConfigured => !string.IsNullOrEmpty(Opts.PageAccessToken);

    // Chữ ký Messenger: X-Hub-Signature-256 = "sha256=" + HMACSHA256(appSecret, rawBody)
    public bool VerifySignature(string rawBody, IHeaderDictionary headers, string requestUrl)
    {
        if (string.IsNullOrEmpty(Opts.AppSecret))
            return true;

        var signature = headers["X-Hub-Signature-256"].ToString();
        if (string.IsNullOrEmpty(signature))
            return false;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(Opts.AppSecret));
        var hash = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(rawBody))).ToLowerInvariant();
        return signature.Equals("sha256=" + hash, StringComparison.OrdinalIgnoreCase);
    }

    public IReadOnlyList<InboundMessage> ParseWebhook(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        if (!root.TryGetProperty("object", out var obj) || obj.GetString() != "page")
            return [];

        var result = new List<InboundMessage>();
        foreach (var entry in root.GetProperty("entry").EnumerateArray())
        {
            if (!entry.TryGetProperty("messaging", out var messaging))
                continue;

            foreach (var item in messaging.EnumerateArray())
            {
                // Bỏ qua echo (tin do page tự gửi)
                if (!item.TryGetProperty("message", out var message))
                    continue;
                if (message.TryGetProperty("is_echo", out var echo) && echo.GetBoolean())
                    continue;

                var text = message.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";

                // Ảnh khách gửi: attachments[0].type == "image", URL trong payload.url
                string? attachmentUrl = null;
                if (message.TryGetProperty("attachments", out var atts) && atts.GetArrayLength() > 0 &&
                    atts[0].TryGetProperty("type", out var attType) && attType.GetString() == "image" &&
                    atts[0].TryGetProperty("payload", out var payload) &&
                    payload.TryGetProperty("url", out var u))
                {
                    attachmentUrl = u.GetString();
                }

                if (string.IsNullOrEmpty(text) && attachmentUrl is null)
                    continue;

                var senderId = item.GetProperty("sender").GetProperty("id").GetString() ?? "";
                var mid = message.TryGetProperty("mid", out var midEl) ? midEl.GetString() : null;

                var sentAt = DateTime.UtcNow;
                if (item.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var unixMs))
                    sentAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;

                result.Add(new InboundMessage(
                    senderId,
                    $"FB …{senderId[Math.Max(0, senderId.Length - 4)..]}",
                    text,
                    mid,
                    sentAt,
                    attachmentUrl));
            }
        }

        if (result.Count == 0)
            logger.LogDebug("Messenger: webhook không chứa tin nhắn văn bản nào");
        return result;
    }

    public async Task<string?> SendTextAsync(Conversation conversation, string text, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient();
        var url = $"https://graph.facebook.com/v21.0/me/messages?access_token={Uri.EscapeDataString(Opts.PageAccessToken)}";

        var response = await client.PostAsync(url, ZaloAdapter.JsonContent(new
        {
            recipient = new { id = conversation.ExternalId },
            messaging_type = "RESPONSE",
            message = new { text }
        }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Messenger API error {(int)response.StatusCode}: {body}");
        return ExtractMid(body);
    }

    public async Task<string?> SendImageAsync(Conversation conversation, string imageUrl, byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient();
        var url = $"https://graph.facebook.com/v21.0/me/messages?access_token={Uri.EscapeDataString(Opts.PageAccessToken)}";

        var response = await client.PostAsync(url, ZaloAdapter.JsonContent(new
        {
            recipient = new { id = conversation.ExternalId },
            messaging_type = "RESPONSE",
            message = new
            {
                attachment = new
                {
                    type = "image",
                    payload = new { url = imageUrl, is_reusable = true }
                }
            }
        }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Messenger API error {(int)response.StatusCode}: {body}");
        return ExtractMid(body);
    }

    public async Task<string?> SendFileAsync(Conversation conversation, string fileUrl, byte[] fileBytes, string fileName, string mimeType, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient();
        var url = $"https://graph.facebook.com/v21.0/me/messages?access_token={Uri.EscapeDataString(Opts.PageAccessToken)}";

        var response = await client.PostAsync(url, ZaloAdapter.JsonContent(new
        {
            recipient = new { id = conversation.ExternalId },
            messaging_type = "RESPONSE",
            message = new { attachment = new { type = "file", payload = new { url = fileUrl, is_reusable = true } } }
        }), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Messenger API error {(int)response.StatusCode}: {body}");
        return ExtractMid(body);
    }

    private static string? ExtractMid(string body)
    {
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("message_id", out var mid) ? mid.GetString() : null;
    }

    // Đồng bộ tin cũ qua Graph API: /me/conversations rồi /{thread}/messages
    public async Task<IReadOnlyList<HistoryMessage>> FetchHistoryAsync(int maxConversations, CancellationToken ct = default)
    {
        var result = new List<HistoryMessage>();
        var client = httpClientFactory.CreateClient();
        var token = Uri.EscapeDataString(Opts.PageAccessToken);

        var convBody = await client.GetStringAsync(
            $"https://graph.facebook.com/v21.0/me/conversations?platform=messenger&fields=participants&limit={maxConversations}&access_token={token}", ct);
        using var convDoc = JsonDocument.Parse(convBody);
        if (!convDoc.RootElement.TryGetProperty("data", out var convs))
            throw new InvalidOperationException($"Messenger /me/conversations lỗi: {convBody}");

        foreach (var conv in convs.EnumerateArray())
        {
            var threadId = conv.GetProperty("id").GetString();

            // Người còn lại trong hội thoại (không phải Page) = khách
            string psid = "", customerName = "";
            foreach (var p in conv.GetProperty("participants").GetProperty("data").EnumerateArray())
            {
                var pid = p.GetProperty("id").GetString() ?? "";
                if (pid != Opts.PageId)
                {
                    psid = pid;
                    customerName = p.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                }
            }
            if (string.IsNullOrEmpty(psid))
                continue;

            var msgBody = await client.GetStringAsync(
                $"https://graph.facebook.com/v21.0/{threadId}/messages?fields=message,from,created_time&limit=30&access_token={token}", ct);
            using var msgDoc = JsonDocument.Parse(msgBody);
            if (!msgDoc.RootElement.TryGetProperty("data", out var msgs))
                continue;

            foreach (var msg in msgs.EnumerateArray())
            {
                var text = msg.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(text))
                    continue;
                var fromId = msg.TryGetProperty("from", out var from) && from.TryGetProperty("id", out var fid)
                    ? fid.GetString() ?? "" : "";

                result.Add(new HistoryMessage(
                    psid,
                    customerName,
                    fromId == Opts.PageId ? MessageDirection.Outbound : MessageDirection.Inbound,
                    text,
                    msg.TryGetProperty("id", out var mid) ? mid.GetString() : null,
                    ParseFacebookTime(msg.TryGetProperty("created_time", out var ctEl) ? ctEl.GetString() : null)));
            }
        }
        return result;
    }

    // Graph API lấy tên + avatar của khách theo PSID
    public async Task<CustomerProfile?> FetchProfileAsync(string externalId, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;
        try
        {
            var client = httpClientFactory.CreateClient();
            var body = await client.GetStringAsync(
                $"https://graph.facebook.com/v21.0/{externalId}?fields=name,profile_pic&access_token={Uri.EscapeDataString(Opts.PageAccessToken)}", ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var name = root.TryGetProperty("name", out var n) ? n.GetString() : null;
            var avatar = root.TryGetProperty("profile_pic", out var p) ? p.GetString() : null;
            return new CustomerProfile(name, avatar);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Messenger: không lấy được profile của {Psid}", externalId);
            return null;
        }
    }

    // Gọi /me để xác nhận Page access token còn sống
    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new(false, "Chưa kết nối (chưa có Page access token). Bấm 'Kết nối Facebook'.");
        try
        {
            var client = httpClientFactory.CreateClient();
            var response = await client.GetAsync(
                $"https://graph.facebook.com/v21.0/me?fields=name&access_token={Uri.EscapeDataString(Opts.PageAccessToken)}", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (response.IsSuccessStatusCode && doc.RootElement.TryGetProperty("name", out var n))
                return new(true, $"Token còn sống — Page: {n.GetString()}. Gửi/nhận tin sẽ hoạt động.");
            var msg = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                ? m.GetString() : body;
            return new(false, $"Facebook báo lỗi: {msg}. Token có thể đã hết hạn — bấm 'Kết nối Facebook' để làm mới.");
        }
        catch (Exception ex)
        {
            return new(false, $"Không gọi được Graph API: {ex.Message}");
        }
    }

    // Facebook trả thời gian dạng "2026-07-07T12:34:56+0000" (offset không có dấu hai chấm)
    private static DateTime ParseFacebookTime(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return DateTime.UtcNow;
        var normalized = System.Text.RegularExpressions.Regex.Replace(value, @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.TryParse(normalized, null, System.Globalization.DateTimeStyles.None, out var dto)
            ? dto.UtcDateTime
            : DateTime.UtcNow;
    }
}
