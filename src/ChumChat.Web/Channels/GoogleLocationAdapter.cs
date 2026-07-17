using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ChumChat.Web.Data;
using ChumChat.Web.Services;

namespace ChumChat.Web.Channels;

// Google Locations (Google Business Profile Reviews API)
// Mô hình hóa mỗi Đánh giá Google Maps thành một cuộc Hội thoại (Conversation) trong Inbox,
// và các Phản hồi (Reply) từ shop thành tin nhắn Outbound.
public class GoogleLocationAdapter(
    ChannelSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    ILogger<GoogleLocationAdapter> logger) : IChannelAdapter
{
    private GoogleLocationOptions Opts => settings.GoogleLocation;

    public ChannelType Channel => ChannelType.GoogleLocation;

    public bool IsConfigured => 
        !string.IsNullOrEmpty(Opts.AccountId) && 
        !string.IsNullOrEmpty(Opts.LocationId) && 
        !string.IsNullOrEmpty(Opts.AccessToken);

    public bool VerifySignature(string rawBody, IHeaderDictionary headers, string requestUrl)
    {
        // Google Location reviews được đồng bộ kéo về bằng polling (hoặc pub/sub), 
        // không dùng webhook đẩy trực tiếp trực tiếp qua controller.
        return true;
    }

    public IReadOnlyList<InboundMessage> ParseWebhook(string rawBody)
    {
        return [];
    }

    // Gửi tin nhắn = Đăng phản hồi review lên Google Maps
    public async Task<string?> SendTextAsync(Conversation conversation, string text, CancellationToken ct = default)
    {
        var token = await GetValidAccessTokenAsync(ct);
        var client = httpClientFactory.CreateClient();
        
        // reviewName có dạng: accounts/{accountId}/locations/{locationId}/reviews/{reviewId}
        var reviewName = conversation.ExternalId;
        if (!reviewName.Contains("/reviews/"))
        {
            reviewName = $"accounts/{Opts.AccountId}/locations/{Opts.LocationId}/reviews/{conversation.ExternalId}";
        }

        var url = $"https://mybusiness.googleapis.com/v4/{reviewName}/reply";
        using var request = new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = ZaloAdapter.JsonContent(new { comment = text })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Google Reviews API error {(int)response.StatusCode}: {body}");

        return conversation.ExternalId + "-reply";
    }

    public Task<string?> SendImageAsync(Conversation conversation, string imageUrl, byte[] imageBytes, string fileName, CancellationToken ct = default)
    {
        // Google Reviews phản hồi chỉ hỗ trợ văn bản.
        return SendTextAsync(conversation, $"[Ảnh gửi kèm: {fileName}] {imageUrl}", ct);
    }

    public Task<string?> SendFileAsync(Conversation conversation, string fileUrl, byte[] fileBytes, string fileName, string mimeType, CancellationToken ct = default)
    {
        // Google Reviews phản hồi chỉ hỗ trợ văn bản.
        return SendTextAsync(conversation, $"[File gửi kèm: {fileName}] {fileUrl}", ct);
    }

    // Polling lấy các đánh giá gần đây để lưu vào database
    public async Task<IReadOnlyList<HistoryMessage>> FetchHistoryAsync(int maxConversations, CancellationToken ct = default)
    {
        if (!IsConfigured)
            return [];

        var result = new List<HistoryMessage>();
        try
        {
            var token = await GetValidAccessTokenAsync(ct);
            var client = httpClientFactory.CreateClient();
            var url = $"https://mybusiness.googleapis.com/v4/accounts/{Opts.AccountId}/locations/{Opts.LocationId}/reviews?pageSize={Math.Min(maxConversations, 50)}";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Google Reviews API error {(int)response.StatusCode}: {body}");

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("reviews", out var reviews))
                return [];

            foreach (var r in reviews.EnumerateArray())
            {
                var reviewId = r.GetProperty("reviewId").GetString() ?? "";
                var reviewerName = r.GetProperty("reviewer").GetProperty("displayName").GetString() ?? "Khách hàng Google";
                var comment = r.TryGetProperty("comment", out var cEl) ? cEl.GetString() ?? "" : "";
                var starRating = r.TryGetProperty("starRating", out var sEl) ? sEl.GetString() ?? "" : "";

                var ratingStars = starRating switch
                {
                    "ONE" => "⭐",
                    "TWO" => "⭐⭐",
                    "THREE" => "⭐⭐⭐",
                    "FOUR" => "⭐⭐⭐⭐",
                    "FIVE" => "⭐⭐⭐⭐⭐",
                    _ => ""
                };

                // Nội dung tin nhắn đến: kèm theo số sao đánh giá
                var inboundText = $"{ratingStars} {comment}".Trim();

                var createTime = DateTime.UtcNow;
                if (r.TryGetProperty("createTime", out var ctEl) && DateTime.TryParse(ctEl.GetString(), out var dt))
                    createTime = dt.ToUniversalTime();

                // 1. Lưu tin của khách đánh giá
                result.Add(new HistoryMessage(
                    reviewId,
                    reviewerName,
                    MessageDirection.Inbound,
                    inboundText,
                    reviewId,
                    createTime
                ));

                // 2. Lưu tin phản hồi của shop (nếu có)
                if (r.TryGetProperty("reviewReply", out var reply))
                {
                    var replyComment = reply.TryGetProperty("comment", out var rcEl) ? rcEl.GetString() ?? "" : "";
                    var replyTime = createTime;
                    if (reply.TryGetProperty("updateTime", out var utEl) && DateTime.TryParse(utEl.GetString(), out var udt))
                        replyTime = udt.ToUniversalTime();

                    if (!string.IsNullOrEmpty(replyComment))
                    {
                        result.Add(new HistoryMessage(
                            reviewId,
                            reviewerName,
                            MessageDirection.Outbound,
                            replyComment,
                            reviewId + "-reply",
                            replyTime
                        ));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "GoogleLocation: Lỗi cào lịch sử đánh giá từ Google Business");
        }

        return result;
    }

    public Task<CustomerProfile?> FetchProfileAsync(string externalId, CancellationToken ct = default)
    {
        return Task.FromResult<CustomerProfile?>(null);
    }

    public async Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(Opts.AccountId) || string.IsNullOrEmpty(Opts.LocationId))
            return new(false, "Chưa điền Account ID hoặc Location ID.");

        try
        {
            var token = await GetValidAccessTokenAsync(ct);
            var client = httpClientFactory.CreateClient();
            var url = $"https://mybusiness.googleapis.com/v4/accounts/{Opts.AccountId}/locations/{Opts.LocationId}";
            
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var response = await client.SendAsync(request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (response.IsSuccessStatusCode)
            {
                using var doc = JsonDocument.Parse(body);
                var name = doc.RootElement.TryGetProperty("locationName", out var ln) ? ln.GetString() : "Google Location";
                return new(true, $"Kết nối thành công tới địa điểm: {name}");
            }

            return new(false, $"Lỗi API Google: {body}");
        }
        catch (Exception ex)
        {
            return new(false, $"Không kết nối được Google API: {ex.Message}");
        }
    }

    // Tự động gia hạn Access Token sử dụng Refresh Token nếu đã hết hạn
    private async Task<string> GetValidAccessTokenAsync(CancellationToken ct)
    {
        if (Opts.TokenExpiresAt.HasValue && Opts.TokenExpiresAt.Value > DateTime.UtcNow.AddMinutes(5))
            return Opts.AccessToken;

        if (string.IsNullOrEmpty(Opts.RefreshToken))
            throw new InvalidOperationException("Google Refresh Token trống. Vui lòng kết nối lại tài khoản.");

        var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync("https://oauth2.googleapis.com/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            { "client_id", Opts.ClientId },
            { "client_secret", Opts.ClientSecret },
            { "refresh_token", Opts.RefreshToken },
            { "grant_type", "refresh_token" }
        }), ct);

        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Lỗi refresh access token Google: {body}");

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        
        Opts.AccessToken = root.GetProperty("access_token").GetString() ?? "";
        var expiresIn = root.GetProperty("expires_in").GetInt32();
        Opts.TokenExpiresAt = DateTime.UtcNow.AddSeconds(expiresIn);

        // Lưu cập nhật vào DB
        await settings.SaveGoogleLocationAsync(Opts);
        return Opts.AccessToken;
    }
}
