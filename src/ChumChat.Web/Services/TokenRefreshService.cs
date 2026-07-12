using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChumChat.Web.Channels;

namespace ChumChat.Web.Services;

// Chạy nền: 5 phút kiểm tra một lần, token nào sắp hết hạn (dưới 30 phút)
// thì tự gọi API refresh của nền tảng đó và lưu token mới.
// Messenger dùng Page token dài hạn nên không cần refresh.
public class TokenRefreshService(
    ChannelSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    ILogger<TokenRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshBefore = TimeSpan.FromMinutes(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RefreshExpiringTokensAsync();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Lỗi khi refresh token");
            }
            await Task.Delay(CheckInterval, stoppingToken);
        }
    }

    private async Task RefreshExpiringTokensAsync()
    {
        if (NeedsRefresh(settings.Zalo.RefreshToken, settings.Zalo.TokenExpiresAt))
            await RefreshZaloAsync();

        if (NeedsRefresh(settings.Shopee.RefreshToken, settings.Shopee.TokenExpiresAt))
            await RefreshShopeeAsync();

        if (NeedsRefresh(settings.TikTokShop.RefreshToken, settings.TikTokShop.TokenExpiresAt))
            await RefreshTikTokAsync();
    }

    private static bool NeedsRefresh(string refreshToken, DateTime? expiresAt) =>
        !string.IsNullOrEmpty(refreshToken) &&
        expiresAt is not null &&
        expiresAt.Value - DateTime.UtcNow < RefreshBefore;

    private async Task RefreshZaloAsync()
    {
        var o = settings.Zalo;
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.zaloapp.com/v4/oa/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["refresh_token"] = o.RefreshToken,
                ["app_id"] = o.AppId,
                ["grant_type"] = "refresh_token"
            })
        };
        request.Headers.Add("secret_key", o.OaSecretKey);

        var body = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var token) || string.IsNullOrEmpty(token.GetString()))
        {
            logger.LogError("Refresh token Zalo thất bại (cần bấm Kết nối lại trên /settings): {Body}", body);
            return;
        }

        o.AccessToken = token.GetString() ?? "";
        // Zalo cấp refresh token MỚI mỗi lần dùng — bắt buộc lưu lại ngay
        if (doc.RootElement.TryGetProperty("refresh_token", out var rt))
            o.RefreshToken = rt.GetString() ?? o.RefreshToken;
        o.TokenExpiresAt = DateTime.UtcNow.AddSeconds(ParseSeconds(doc.RootElement, "expires_in", 90000));
        await settings.SaveZaloAsync(o);
        logger.LogInformation("Đã gia hạn token Zalo, hết hạn lúc {Expires:u}", o.TokenExpiresAt);
    }

    private async Task RefreshShopeeAsync()
    {
        var o = settings.Shopee;
        const string path = "/api/v2/auth/access_token/get";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sign = HmacHex(o.PartnerKey, o.PartnerId + path + timestamp);
        var url = $"{o.ApiBaseUrl}{path}?partner_id={o.PartnerId}&timestamp={timestamp}&sign={sign}";

        var client = httpClientFactory.CreateClient();
        var payload = JsonSerializer.Serialize(new
        {
            refresh_token = o.RefreshToken,
            partner_id = long.Parse(o.PartnerId),
            shop_id = long.Parse(o.ShopId)
        });
        var response = await client.PostAsync(url, new StringContent(payload, Encoding.UTF8, "application/json"));
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("access_token", out var token) || string.IsNullOrEmpty(token.GetString()))
        {
            logger.LogError("Refresh token Shopee thất bại (cần bấm Kết nối lại trên /settings): {Body}", body);
            return;
        }

        o.AccessToken = token.GetString() ?? "";
        if (doc.RootElement.TryGetProperty("refresh_token", out var rt))
            o.RefreshToken = rt.GetString() ?? o.RefreshToken;
        o.TokenExpiresAt = DateTime.UtcNow.AddSeconds(ParseSeconds(doc.RootElement, "expire_in", 14400));
        await settings.SaveShopeeAsync(o);
        logger.LogInformation("Đã gia hạn token Shopee, hết hạn lúc {Expires:u}", o.TokenExpiresAt);
    }

    private async Task RefreshTikTokAsync()
    {
        var o = settings.TikTokShop;
        var client = httpClientFactory.CreateClient();
        var body = await client.GetStringAsync(
            $"https://auth.tiktok-shops.com/api/v2/token/refresh?app_key={o.AppKey}&app_secret={o.AppSecret}&refresh_token={Uri.EscapeDataString(o.RefreshToken)}&grant_type=refresh_token");
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("access_token", out var token) || string.IsNullOrEmpty(token.GetString()))
        {
            logger.LogError("Refresh token TikTok thất bại (cần bấm Kết nối lại trên /settings): {Body}", body);
            return;
        }

        o.AccessToken = token.GetString() ?? "";
        if (data.TryGetProperty("refresh_token", out var rt))
            o.RefreshToken = rt.GetString() ?? o.RefreshToken;
        if (data.TryGetProperty("access_token_expire_in", out var exp) && exp.TryGetInt64(out var expEpoch))
            o.TokenExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expEpoch).UtcDateTime;
        await settings.SaveTikTokAsync(o);
        logger.LogInformation("Đã gia hạn token TikTok, hết hạn lúc {Expires:u}", o.TokenExpiresAt);
    }

    private static string HmacHex(string key, string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private static long ParseSeconds(JsonElement root, string property, long fallback)
    {
        if (!root.TryGetProperty(property, out var el))
            return fallback;
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
            return n;
        return long.TryParse(el.GetString(), out var s) ? s : fallback;
    }
}
