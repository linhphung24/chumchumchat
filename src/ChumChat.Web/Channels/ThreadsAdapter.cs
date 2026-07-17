using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChumChat.Web.Data;
using ChumChat.Web.Services;

namespace ChumChat.Web.Channels;

// Threads API (qua Meta Graph API)
public class ThreadsAdapter(
    ChannelSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    ILogger<ThreadsAdapter> logger) : IChannelAdapter
{
    private ThreadsOptions Opts => settings.Threads;

    public ChannelType Channel => ChannelType.Threads;

    public bool IsConfigured => !string.IsNullOrEmpty(Opts.PageAccessToken);

    // Chữ ký Webhook của Threads tương tự Instagram (sha256 = HMACSHA256(appSecret, rawBody))
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

        // Threads webhook có object = "threads"
        if (!root.TryGetProperty("object", out var obj) || obj.GetString() != "threads")
            return [];

        var result = new List<InboundMessage>();
        foreach (var entry in root.GetProperty("entry").EnumerateArray())
        {
            if (entry.TryGetProperty("messaging", out var messaging))
            {
                // Tin nhắn trực tiếp (nếu có hỗ trợ sau này)
                foreach (var item in messaging.EnumerateArray())
                {
                    if (!item.TryGetProperty("message", out var message))
                        continue;
                    if (message.TryGetProperty("is_echo", out var echo) && echo.GetBoolean())
                        continue;

                    var text = message.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
                    var senderId = item.GetProperty("sender").GetProperty("id").GetString() ?? "";
                    var mid = message.TryGetProperty("mid", out var midEl) ? midEl.GetString() : null;

                    var sentAt = DateTime.UtcNow;
                    if (item.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var unixMs))
                        sentAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;

                    result.Add(new InboundMessage(
                        senderId,
                        $"Threads …{senderId[Math.Max(0, senderId.Length - 4)..]}",
                        text,
                        mid,
                        sentAt));
                }
            }
            else if (entry.TryGetProperty("changes", out var changes))
            {
                // Xử lý thông báo bình luận/nhắc tới trên Threads (comments/replies)
                foreach (var change in changes.EnumerateArray())
                {
                    if (!change.TryGetProperty("value", out var value))
                        continue;

                    var text = value.TryGetProperty("text", out var textEl) ? textEl.GetString() ?? "" : "";
                    var senderId = value.TryGetProperty("sender_id", out var sId) ? sId.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(senderId) || senderId == Opts.ThreadsAccountId)
                        continue;

                    var replyId = value.TryGetProperty("media_id", out var mId) ? mId.GetString() : null; // ID của post/reply để trả lời

                    var sentAt = DateTime.UtcNow;
                    if (value.TryGetProperty("timestamp", out var ts) && ts.TryGetInt64(out var unixMs))
                        sentAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;

                    result.Add(new InboundMessage(
                        replyId ?? senderId,
                        $"Threads …{senderId[Math.Max(0, senderId.Length - 4)..]}",
                        text,
                        replyId,
                        sentAt));
                }
            }
        }

        return result;
    }

    public async Task<string?> SendTextAsync(Conversation conversation, string text, CancellationToken ct = default)
    {
        var client = httpClientFactory.CreateClient();
        string url;
        object payload;

        // Nếu ID có định dạng dài (ID của bài đăng/bình luận), ta gửi dưới dạng Reply bài đăng Threads
        if (conversation.ExternalId.Length > 15)
        {
            url = $"https://graph.threads.net/v1.0/{conversation.ExternalId}/replies?access_token={Uri.EscapeDataString(Opts.PageAccessToken)}";
            payload = new
            {
                media_type = "REPLY",
                text = text
            };
        }
        else
        {
            url = $"https://graph.threads.net/v1.0/me/messages?access_token={Uri.EscapeDataString(Opts.PageAccessToken)}";
            payload = new
            {
                recipient = new { id = conversation.ExternalId },
                message = new { text }
            };
        }

        var response = await client.PostAsync(url, ZaloAdapter.JsonContent(payload), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Threads API error {(int)response.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    public Task<string?> SendImageAsync(Conversation conversation, string imageUrl, byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        // Threads API chưa hỗ trợ gửi trực tiếp hình ảnh trong tin nhắn Response qua webhook dễ dàng, fallback kèm link
        return SendTextAsync(conversation, $"{imageUrl}\n({fileName})", ct);
    }

    public Task<string?> SendFileAsync(Conversation conversation, string fileUrl, byte[] fileBytes, string fileName, string mimeType, CancellationToken ct = default)
    {
        return SendTextAsync(conversation, $"[File: {fileName}] {fileUrl}", ct);
    }

    public Task<IReadOnlyList<HistoryMessage>> FetchHistoryAsync(int maxConversations, CancellationToken ct = default)
    {
        // Threads API chưa hỗ trợ đồng bộ hàng loạt các cuộc hội thoại cũ một cách dễ dàng
        return Task.FromResult<IReadOnlyList<HistoryMessage>>([]);
    }

    public async Task<CustomerProfile?> FetchProfileAsync(string externalId, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return null;
        try
        {
            var client = httpClientFactory.CreateClient();
            var body = await client.GetStringAsync(
                $"https://graph.threads.net/v1.0/{externalId}?fields=username,profile_pic&access_token={Uri.EscapeDataString(Opts.PageAccessToken)}", ct);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var name = root.TryGetProperty("username", out var n) ? n.GetString() : null;
            var avatar = root.TryGetProperty("profile_pic", out var p) ? p.GetString() : null;
            return new CustomerProfile(name, avatar);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Threads: không lấy được profile của {Id}", externalId);
            return null;
        }
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (!IsConfigured)
            return new(false, "Chưa kết nối (chưa có Page access token).");
        try
        {
            var client = httpClientFactory.CreateClient();
            var response = await client.GetAsync(
                $"https://graph.threads.net/v1.0/me?fields=id,username&access_token={Uri.EscapeDataString(Opts.PageAccessToken)}", ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            if (response.IsSuccessStatusCode && doc.RootElement.TryGetProperty("username", out var u))
                return new(true, $"Token còn sống — Đã liên kết với Threads: @{u.GetString()}.");
            
            var msg = doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m)
                ? m.GetString() : body;
            return new(false, $"Threads báo lỗi: {msg}");
        }
        catch (Exception ex)
        {
            return new(false, $"Không gọi được Threads API: {ex.Message}");
        }
    }
}
