using System.Text.Json;
using ChumChat.Web.Data;
using ChumChat.Web.Services;

namespace ChumChat.Web.Channels;

// Zalo tài khoản cá nhân, qua sidecar Node.js dùng thư viện zca-js (sidecars/zalo-personal).
// Sidecar đăng nhập bằng QR, nghe tin nhắn đến rồi POST vào /webhooks/zalopersonal;
// chiều gửi đi thì adapter này gọi HTTP sang sidecar.
// LƯU Ý: đây là API không chính thức — Zalo có thể khóa tài khoản nếu phát hiện.
public class ZaloPersonalAdapter(
    ChannelSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    ILogger<ZaloPersonalAdapter> logger) : IChannelAdapter
{
    private ZaloPersonalOptions Opts => settings.ZaloPersonal;

    public ChannelType Channel => ChannelType.ZaloPersonal;

    public bool IsConfigured =>
        !string.IsNullOrEmpty(Opts.SidecarUrl) && !string.IsNullOrEmpty(Opts.ApiKey);

    // Sidecar gửi kèm header X-Api-Key trùng chuỗi bí mật đã cấu hình
    public bool VerifySignature(string rawBody, IHeaderDictionary headers, string requestUrl)
    {
        if (string.IsNullOrEmpty(Opts.ApiKey))
            return false;
        return headers["X-Api-Key"].ToString() == Opts.ApiKey;
    }

    // Payload do sidecar chuẩn hóa sẵn:
    // { "userId": "...", "name": "...", "text": "...", "msgId": "...", "ts": 1712..., "attachmentUrl": "..." }
    public IReadOnlyList<InboundMessage> ParseWebhook(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        var userId = root.TryGetProperty("userId", out var u) ? u.GetString() : null;
        if (string.IsNullOrEmpty(userId))
        {
            logger.LogDebug("ZaloPersonal: payload không có userId, bỏ qua");
            return [];
        }

        var text = root.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
        var attachmentUrl = root.TryGetProperty("attachmentUrl", out var a) ? a.GetString() : null;
        if (string.IsNullOrEmpty(text) && string.IsNullOrEmpty(attachmentUrl))
            return [];

        var name = root.TryGetProperty("name", out var n) && !string.IsNullOrEmpty(n.GetString())
            ? n.GetString()!
            : $"Zalo CN …{userId[Math.Max(0, userId.Length - 4)..]}";

        var sentAt = DateTime.UtcNow;
        if (root.TryGetProperty("ts", out var ts) && ts.TryGetInt64(out var unixMs))
            sentAt = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;

        var isOutbound = root.TryGetProperty("isOutbound", out var ob) && ob.GetBoolean();

        return [new InboundMessage(
            userId,
            name,
            text,
            root.TryGetProperty("msgId", out var mi) ? mi.GetString() : null,
            sentAt,
            attachmentUrl,
            Direction: isOutbound ? MessageDirection.Outbound : MessageDirection.Inbound)];
    }

    public Task<string?> SendTextAsync(Conversation conversation, string text, CancellationToken ct = default) =>
        PostToSidecarAsync("/send", new { threadId = conversation.ExternalId, text }, ct);

    public Task<string?> SendImageAsync(Conversation conversation, string imageUrl, byte[] imageBytes, string fileName, CancellationToken ct = default) =>
        PostToSidecarAsync("/send-image", new { threadId = conversation.ExternalId, url = imageUrl }, ct);

    public Task<string?> SendFileAsync(Conversation conversation, string fileUrl, byte[] fileBytes, string fileName, string mimeType, CancellationToken ct = default) =>
        PostToSidecarAsync("/send-file", new { threadId = conversation.ExternalId, url = fileUrl, fileName }, ct);

    public Task<string?> SendStickerAsync(Conversation conversation, string keywordOrStickerId, CancellationToken ct = default) =>
        PostToSidecarAsync("/send-sticker", new { threadId = conversation.ExternalId, keyword = keywordOrStickerId }, ct);

    private async Task<string?> PostToSidecarAsync(string path, object payload, CancellationToken ct)
    {
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, Opts.SidecarUrl.TrimEnd('/') + path)
        {
            Content = ZaloAdapter.JsonContent(payload)
        };
        request.Headers.Add("X-Api-Key", Opts.ApiKey);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(request, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Không gọi được sidecar Zalo cá nhân tại {Opts.SidecarUrl} — sidecar đã chạy chưa? ({ex.Message})");
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Sidecar Zalo cá nhân lỗi {(int)response.StatusCode}: {body}");
        return null; // sidecar chưa trả message_id
    }

    public Task<IReadOnlyList<HistoryMessage>> FetchHistoryAsync(int maxConversations, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<HistoryMessage>>(Array.Empty<HistoryMessage>());

    // Sidecar đã gửi kèm tên hiển thị trong payload; chưa lấy avatar riêng
    public Task<CustomerProfile?> FetchProfileAsync(string externalId, CancellationToken ct = default) =>
        Task.FromResult<CustomerProfile?>(null);

    // Zalo cá nhân dùng nút "Kiểm tra & hiện mã QR" riêng; ở đây chỉ báo trạng thái cấu hình
    public Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default) =>
        Task.FromResult(new ConnectionTestResult(IsConfigured,
            IsConfigured ? "Đã cấu hình sidecar — dùng nút 'Kiểm tra & hiện mã QR'." : "Chưa cấu hình sidecar."));
}
