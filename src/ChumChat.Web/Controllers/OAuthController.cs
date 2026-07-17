using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ChumChat.Web.Data;
using ChumChat.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChumChat.Web.Controllers;

// Luồng "Kết nối" các kênh: /oauth/{kênh}/start chuyển hướng sang trang cấp quyền
// của nền tảng; callback nhận code, đổi lấy access token và lưu vào cấu hình.
[Route("oauth")]
public class OAuthController(
    ChannelSettingsStore settings,
    OAuthStateCache states,
    IHttpClientFactory httpClientFactory,
    ILogger<OAuthController> logger) : Controller
{
    private string BaseUrl => $"{Request.Scheme}://{Request.Host}";

    // ============ ZALO OA ============

    [HttpGet("zalo/start")]
    public IActionResult ZaloStart()
    {
        var o = settings.Zalo;
        if (string.IsNullOrEmpty(o.AppId))
            return SettingsRedirect("zalo", error: "Nhập App ID và Secret Key của Zalo trước khi kết nối");

        var state = states.Create(ChannelType.Zalo);
        
        // Tạo PKCE code_verifier & code_challenge cho Zalo OAuth v4
        var verifier = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
        using var sha256 = SHA256.Create();
        var challengeBytes = sha256.ComputeHash(Encoding.ASCII.GetBytes(verifier));
        var challenge = Convert.ToBase64String(challengeBytes)
            .Replace("+", "-")
            .Replace("/", "_")
            .TrimEnd('=');

        states.StoreCodeVerifier(state, verifier);

        var redirect = Uri.EscapeDataString($"{BaseUrl}/oauth/zalo/callback");
        return Redirect($"https://oauth.zaloapp.com/v4/oa/permission?app_id={o.AppId}&redirect_uri={redirect}&code_challenge={challenge}&state={state}");
    }

    [HttpGet("zalo/callback")]
    public async Task<IActionResult> ZaloCallback(string? code, string? state, string? oa_id)
    {
        if (!states.Validate(state, ChannelType.Zalo))
            return SettingsRedirect("zalo", error: "Phiên kết nối hết hạn, thử lại");
        
        var verifier = states.GetCodeVerifier(state!);
        states.Remove(state!);

        if (string.IsNullOrEmpty(code))
            return SettingsRedirect("zalo", error: "Zalo không trả về mã cấp quyền");
        if (string.IsNullOrEmpty(verifier))
            return SettingsRedirect("zalo", error: "Không tìm thấy mã xác thực phiên kết nối (code verifier), thử lại");

        var o = settings.Zalo;
        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://oauth.zaloapp.com/v4/oa/access_token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["code"] = code,
                ["app_id"] = o.AppId,
                ["grant_type"] = "authorization_code",
                ["code_verifier"] = verifier
            })
        };
        request.Headers.Add("secret_key", o.AppSecretKey);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("access_token", out var token))
        {
            logger.LogError("Zalo OAuth lỗi: {Body}", body);
            return SettingsRedirect("zalo", error: $"Zalo từ chối cấp token — Chi tiết phản hồi từ Zalo: {body}");
        }

        o.AccessToken = token.GetString() ?? "";
        o.RefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        o.TokenExpiresAt = DateTime.UtcNow.AddSeconds(ParseSeconds(doc.RootElement, "expires_in", 90000));
        o.AccountName = string.IsNullOrEmpty(oa_id) ? "Zalo OA" : $"OA {oa_id}";
        await settings.SaveZaloAsync(o);
        return SettingsRedirect("zalo", ok: true);
    }

    // ============ FACEBOOK MESSENGER ============

    [HttpGet("messenger/start")]
    public IActionResult MessengerStart()
    {
        var o = settings.Messenger;
        if (string.IsNullOrEmpty(o.AppId) || string.IsNullOrEmpty(o.AppSecret))
            return SettingsRedirect("messenger", error: "Nhập App ID và App Secret của Facebook trước khi kết nối");

        var state = states.Create(ChannelType.Messenger);
        var redirect = Uri.EscapeDataString($"{BaseUrl}/oauth/messenger/callback");
        var scope = "pages_show_list,pages_messaging,pages_manage_metadata";
        return Redirect($"https://www.facebook.com/v21.0/dialog/oauth?client_id={o.AppId}&redirect_uri={redirect}&state={state}&response_type=code&scope={scope}");
    }

    [HttpGet("messenger/callback")]
    public async Task<IActionResult> MessengerCallback(string? code, string? state)
    {
        if (!states.Validate(state, ChannelType.Messenger))
            return SettingsRedirect("messenger", error: "Phiên kết nối hết hạn, thử lại");
        if (string.IsNullOrEmpty(code))
            return SettingsRedirect("messenger", error: "Facebook không trả về mã cấp quyền (có thể bạn đã bấm Hủy)");

        var o = settings.Messenger;
        var client = httpClientFactory.CreateClient();
        var redirect = Uri.EscapeDataString($"{BaseUrl}/oauth/messenger/callback");

        // Bước 1: code → user token ngắn hạn
        var tokenBody = await client.GetStringAsync(
            $"https://graph.facebook.com/v21.0/oauth/access_token?client_id={o.AppId}&redirect_uri={redirect}&client_secret={o.AppSecret}&code={Uri.EscapeDataString(code)}");
        var shortToken = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString();

        // Bước 2: đổi sang user token dài hạn (để page token nhận được cũng là loại dài hạn)
        var longBody = await client.GetStringAsync(
            $"https://graph.facebook.com/v21.0/oauth/access_token?grant_type=fb_exchange_token&client_id={o.AppId}&client_secret={o.AppSecret}&fb_exchange_token={Uri.EscapeDataString(shortToken!)}");
        var longToken = JsonDocument.Parse(longBody).RootElement.GetProperty("access_token").GetString();

        // Bước 3: lấy danh sách Page mà tài khoản quản lý
        var pagesBody = await client.GetStringAsync(
            $"https://graph.facebook.com/v21.0/me/accounts?access_token={Uri.EscapeDataString(longToken!)}");
        using var pagesDoc = JsonDocument.Parse(pagesBody);

        var pages = new List<OAuthStateCache.FacebookPage>();
        foreach (var p in pagesDoc.RootElement.GetProperty("data").EnumerateArray())
        {
            pages.Add(new OAuthStateCache.FacebookPage(
                p.GetProperty("id").GetString() ?? "",
                p.GetProperty("name").GetString() ?? "",
                p.GetProperty("access_token").GetString() ?? ""));
        }

        if (pages.Count == 0)
            return SettingsRedirect("messenger", error: "Tài khoản Facebook này không quản lý Page nào");

        if (pages.Count == 1)
        {
            states.Remove(state!);
            await SaveMessengerPageAsync(pages[0]);
            return SettingsRedirect("messenger", ok: true);
        }

        // Nhiều Page → hiện trang chọn
        states.StorePages(state!, pages);
        var links = string.Join("", pages.Select(p =>
            $"<li><a href=\"/oauth/messenger/select?state={state}&page={p.Id}\">{System.Net.WebUtility.HtmlEncode(p.Name)}</a></li>"));
        return Content($$"""
            <!DOCTYPE html><html lang="vi"><head><meta charset="utf-8"><title>Chọn Page</title>
            <style>body{font-family:system-ui;max-width:480px;margin:60px auto;padding:0 16px}
            li{margin:10px 0;font-size:1.1rem}</style></head>
            <body><h2>Chọn Fanpage muốn kết nối</h2><ul>{{links}}</ul></body></html>
            """, "text/html");
    }

    [HttpGet("messenger/select")]
    public async Task<IActionResult> MessengerSelect(string? state, string? page)
    {
        var pages = states.GetPages(state ?? "");
        var chosen = pages?.FirstOrDefault(p => p.Id == page);
        if (chosen is null)
            return SettingsRedirect("messenger", error: "Phiên chọn Page hết hạn, kết nối lại");

        states.Remove(state!);
        await SaveMessengerPageAsync(chosen);
        return SettingsRedirect("messenger", ok: true);
    }

    private async Task SaveMessengerPageAsync(OAuthStateCache.FacebookPage page)
    {
        var o = settings.Messenger;
        o.PageAccessToken = page.AccessToken;
        o.PageId = page.Id;
        o.AccountName = page.Name;
        await settings.SaveMessengerAsync(o);

        // Đăng ký app nhận webhook tin nhắn của Page này
        var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync(
            $"https://graph.facebook.com/v21.0/{page.Id}/subscribed_apps?subscribed_fields=messages&access_token={Uri.EscapeDataString(page.AccessToken)}",
            content: null);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("Không subscribe được webhook cho Page {Page}: {Body}",
                page.Name, await response.Content.ReadAsStringAsync());
    }

    // ============ INSTAGRAM ============

    [HttpGet("instagram/start")]
    public IActionResult InstagramStart()
    {
        var o = settings.Instagram;
        if (string.IsNullOrEmpty(o.AppId) || string.IsNullOrEmpty(o.AppSecret))
            return SettingsRedirect("instagram", error: "Nhập App ID và App Secret của Facebook trước khi kết nối");

        var state = states.Create(ChannelType.Instagram);
        var redirect = Uri.EscapeDataString($"{BaseUrl}/oauth/instagram/callback");
        var scope = "instagram_basic,instagram_manage_messages,pages_show_list,pages_manage_metadata";
        return Redirect($"https://www.facebook.com/v21.0/dialog/oauth?client_id={o.AppId}&redirect_uri={redirect}&state={state}&response_type=code&scope={scope}");
    }

    [HttpGet("instagram/callback")]
    public async Task<IActionResult> InstagramCallback(string? code, string? state)
    {
        if (!states.Validate(state, ChannelType.Instagram))
            return SettingsRedirect("instagram", error: "Phiên kết nối hết hạn, thử lại");
        if (string.IsNullOrEmpty(code))
            return SettingsRedirect("instagram", error: "Facebook không trả về mã cấp quyền (có thể bạn đã bấm Hủy)");

        var o = settings.Instagram;
        var client = httpClientFactory.CreateClient();
        var redirect = Uri.EscapeDataString($"{BaseUrl}/oauth/instagram/callback");

        // Bước 1: code → user token ngắn hạn
        var tokenBody = await client.GetStringAsync(
            $"https://graph.facebook.com/v21.0/oauth/access_token?client_id={o.AppId}&redirect_uri={redirect}&client_secret={o.AppSecret}&code={Uri.EscapeDataString(code)}");
        var shortToken = JsonDocument.Parse(tokenBody).RootElement.GetProperty("access_token").GetString();

        // Bước 2: đổi sang user token dài hạn
        var longBody = await client.GetStringAsync(
            $"https://graph.facebook.com/v21.0/oauth/access_token?grant_type=fb_exchange_token&client_id={o.AppId}&client_secret={o.AppSecret}&fb_exchange_token={Uri.EscapeDataString(shortToken!)}");
        var longToken = JsonDocument.Parse(longBody).RootElement.GetProperty("access_token").GetString();

        // Bước 3: lấy danh sách Page có liên kết Instagram
        var pagesBody = await client.GetStringAsync(
            $"https://graph.facebook.com/v21.0/me/accounts?fields=id,name,access_token,instagram_business_account&access_token={Uri.EscapeDataString(longToken!)}");
        using var pagesDoc = JsonDocument.Parse(pagesBody);

        var pages = new List<OAuthStateCache.FacebookPage>();
        foreach (var p in pagesDoc.RootElement.GetProperty("data").EnumerateArray())
        {
            if (p.TryGetProperty("instagram_business_account", out var ig) && ig.TryGetProperty("id", out var igId))
            {
                var pageName = p.GetProperty("name").GetString() ?? "";
                pages.Add(new OAuthStateCache.FacebookPage(
                    p.GetProperty("id").GetString() ?? "",
                    $"{pageName} (IG ID: {igId.GetString()})",
                    p.GetProperty("access_token").GetString() ?? ""));
            }
        }

        if (pages.Count == 0)
            return SettingsRedirect("instagram", error: "Tài khoản Facebook này không quản lý Fanpage nào có liên kết Instagram Business");

        if (pages.Count == 1)
        {
            states.Remove(state!);
            await SaveInstagramPageAsync(pages[0]);
            return SettingsRedirect("instagram", ok: true);
        }

        states.StorePages(state!, pages);
        var links = string.Join("", pages.Select(p =>
            $"<li><a href=\"/oauth/instagram/select?state={state}&page={p.Id}\">{System.Net.WebUtility.HtmlEncode(p.Name)}</a></li>"));
        return Content($$"""
            <!DOCTYPE html><html lang="vi"><head><meta charset="utf-8"><title>Chọn Tài khoản Instagram</title>
            <style>body{font-family:system-ui;max-width:480px;margin:60px auto;padding:0 16px}
            li{margin:10px 0;font-size:1.1rem}</style></head>
            <body><h2>Chọn tài khoản Instagram muốn kết nối</h2><ul>{{links}}</ul></body></html>
            """, "text/html");
    }

    [HttpGet("instagram/select")]
    public async Task<IActionResult> InstagramSelect(string? state, string? page)
    {
        var pages = states.GetPages(state ?? "");
        var chosen = pages?.FirstOrDefault(p => p.Id == page);
        if (chosen is null)
            return SettingsRedirect("instagram", error: "Phiên chọn tài khoản hết hạn, kết nối lại");

        states.Remove(state!);
        await SaveInstagramPageAsync(chosen);
        return SettingsRedirect("instagram", ok: true);
    }

    private async Task SaveInstagramPageAsync(OAuthStateCache.FacebookPage page)
    {
        var o = settings.Instagram;
        o.PageAccessToken = page.AccessToken;
        o.PageId = page.Id;
        o.AccountName = page.Name;

        // Trích xuất lại IG ID từ tên (vì lúc nãy nhét tạm vào name để dễ parse)
        var match = System.Text.RegularExpressions.Regex.Match(page.Name, @"\(IG ID: (\d+)\)");
        if (match.Success)
            o.InstagramAccountId = match.Groups[1].Value;

        await settings.SaveInstagramAsync(o);

        // Đăng ký app nhận webhook messages cho Page này
        var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync(
            $"https://graph.facebook.com/v21.0/{page.Id}/subscribed_apps?subscribed_fields=messages&access_token={Uri.EscapeDataString(page.AccessToken)}",
            content: null);
        if (!response.IsSuccessStatusCode)
            logger.LogWarning("Không subscribe được webhook cho IG/Page {Page}: {Body}",
                page.Name, await response.Content.ReadAsStringAsync());
    }

    // ============ SHOPEE ============

    [HttpGet("shopee/start")]
    public IActionResult ShopeeStart()
    {
        var o = settings.Shopee;
        if (string.IsNullOrEmpty(o.PartnerId) || string.IsNullOrEmpty(o.PartnerKey))
            return SettingsRedirect("shopee", error: "Nhập Partner ID và Partner Key của Shopee trước khi kết nối");

        const string path = "/api/v2/shop/auth_partner";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sign = HmacHex(o.PartnerKey, o.PartnerId + path + timestamp);
        var redirect = Uri.EscapeDataString($"{BaseUrl}/oauth/shopee/callback");
        return Redirect($"{o.ApiBaseUrl}{path}?partner_id={o.PartnerId}&timestamp={timestamp}&sign={sign}&redirect={redirect}");
    }

    [HttpGet("shopee/callback")]
    public async Task<IActionResult> ShopeeCallback(string? code, string? shop_id)
    {
        // Shopee không có tham số state; định danh dựa trên chữ ký partner khi đổi token
        if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(shop_id))
            return SettingsRedirect("shopee", error: "Shopee không trả về code/shop_id");

        var o = settings.Shopee;
        const string path = "/api/v2/auth/token/get";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var sign = HmacHex(o.PartnerKey, o.PartnerId + path + timestamp);
        var url = $"{o.ApiBaseUrl}{path}?partner_id={o.PartnerId}&timestamp={timestamp}&sign={sign}";

        var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync(url, JsonBody(new
        {
            code,
            shop_id = long.Parse(shop_id),
            partner_id = long.Parse(o.PartnerId)
        }));
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("access_token", out var token) || string.IsNullOrEmpty(token.GetString()))
        {
            logger.LogError("Shopee OAuth lỗi: {Body}", body);
            return SettingsRedirect("shopee", error: "Shopee từ chối cấp token — xem log server");
        }

        o.ShopId = shop_id;
        o.AccessToken = token.GetString() ?? "";
        o.RefreshToken = doc.RootElement.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        o.TokenExpiresAt = DateTime.UtcNow.AddSeconds(ParseSeconds(doc.RootElement, "expire_in", 14400));
        o.AccountName = $"Shop {shop_id}";
        await settings.SaveShopeeAsync(o);
        return SettingsRedirect("shopee", ok: true);
    }

    // ============ TIKTOK SHOP ============

    [HttpGet("tiktok/start")]
    public IActionResult TikTokStart()
    {
        var o = settings.TikTokShop;
        if (string.IsNullOrEmpty(o.AppKey) || string.IsNullOrEmpty(o.AppSecret))
            return SettingsRedirect("tiktok", error: "Nhập App Key và App Secret của TikTok Shop trước khi kết nối");

        var state = states.Create(ChannelType.TikTokShop);
        // Redirect URL phải khai báo sẵn trong app trên partner.tiktokshop.com
        return Redirect($"https://auth.tiktok-shops.com/oauth/authorize?app_key={o.AppKey}&state={state}");
    }

    [HttpGet("tiktok/callback")]
    public async Task<IActionResult> TikTokCallback(string? code, string? state)
    {
        if (!states.Validate(state, ChannelType.TikTokShop))
            return SettingsRedirect("tiktok", error: "Phiên kết nối hết hạn, thử lại");
        states.Remove(state!);
        if (string.IsNullOrEmpty(code))
            return SettingsRedirect("tiktok", error: "TikTok không trả về mã cấp quyền");

        var o = settings.TikTokShop;
        var client = httpClientFactory.CreateClient();
        var body = await client.GetStringAsync(
            $"https://auth.tiktok-shops.com/api/v2/token/get?app_key={o.AppKey}&app_secret={o.AppSecret}&auth_code={Uri.EscapeDataString(code)}&grant_type=authorized_code");
        using var doc = JsonDocument.Parse(body);

        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("access_token", out var token) || string.IsNullOrEmpty(token.GetString()))
        {
            logger.LogError("TikTok OAuth lỗi: {Body}", body);
            return SettingsRedirect("tiktok", error: "TikTok từ chối cấp token — xem log server");
        }

        o.AccessToken = token.GetString() ?? "";
        o.RefreshToken = data.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
        if (data.TryGetProperty("access_token_expire_in", out var exp) && exp.TryGetInt64(out var expEpoch))
            o.TokenExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expEpoch).UtcDateTime;
        o.AccountName = data.TryGetProperty("seller_name", out var sn) ? sn.GetString() ?? "TikTok Shop" : "TikTok Shop";

        // Lấy shop_cipher — cần cho mọi request API sau này
        try
        {
            o.ShopCipher = await FetchTikTokShopCipherAsync(o) ?? o.ShopCipher;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Không lấy được shop_cipher TikTok (có thể điền tay sau)");
        }

        await settings.SaveTikTokAsync(o);
        return SettingsRedirect("tiktok", ok: true);
    }

    private async Task<string?> FetchTikTokShopCipherAsync(Channels.TikTokShopOptions o)
    {
        const string path = "/authorization/202309/shops";
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var baseString = $"{o.AppSecret}{path}app_key{o.AppKey}timestamp{timestamp}{o.AppSecret}";
        var sign = HmacHex(o.AppSecret, baseString);

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"{o.ApiBaseUrl}{path}?app_key={o.AppKey}&timestamp={timestamp}&sign={sign}");
        request.Headers.Add("x-tts-access-token", o.AccessToken);

        var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.TryGetProperty("data", out var data) &&
            data.TryGetProperty("shops", out var shops) &&
            shops.GetArrayLength() > 0)
        {
            return shops[0].TryGetProperty("cipher", out var cipher) ? cipher.GetString() : null;
        }
        logger.LogWarning("TikTok /shops không trả về shop nào: {Body}", body);
        return null;
    }

    // ============ Dùng chung ============

    private RedirectResult SettingsRedirect(string channel, bool ok = false, string? error = null)
    {
        if (error is not null)
            logger.LogWarning("OAuth {Channel}: {Error}", channel, error);
        var query = ok ? $"connected={channel}" : $"error={Uri.EscapeDataString(error ?? "unknown")}";
        return Redirect($"/settings?{query}");
    }

    private static string HmacHex(string key, string data)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(data))).ToLowerInvariant();
    }

    private static StringContent JsonBody(object payload) =>
        new(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

    private static long ParseSeconds(JsonElement root, string property, long fallback)
    {
        if (!root.TryGetProperty(property, out var el))
            return fallback;
        // Một số API trả số dạng chuỗi
        if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
            return n;
        return long.TryParse(el.GetString(), out var s) ? s : fallback;
    }
}
