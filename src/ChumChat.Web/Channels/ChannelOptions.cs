namespace ChumChat.Web.Channels;

// Cấu hình các kênh. Nguồn chính là database (chỉnh qua trang /settings);
// section "Channels" trong appsettings.json chỉ dùng làm giá trị khởi tạo lần đầu.
public class ChannelsOptions
{
    public const string SectionName = "Channels";

    public ZaloOptions Zalo { get; set; } = new();
    public MessengerOptions Messenger { get; set; } = new();
    public ShopeeOptions Shopee { get; set; } = new();
    public TikTokShopOptions TikTokShop { get; set; } = new();
}

// App tạo tại https://developers.zalo.me (liên kết với Official Account)
public class ZaloOptions
{
    public string AppId { get; set; } = "";
    public string OaSecretKey { get; set; } = "";

    // Do OAuth tự điền sau khi bấm "Kết nối"
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime? TokenExpiresAt { get; set; }
    public string AccountName { get; set; } = "";
}

// App tạo tại https://developers.facebook.com (thêm sản phẩm Messenger)
public class MessengerOptions
{
    public string AppId { get; set; } = "";
    public string AppSecret { get; set; } = "";

    // Chuỗi tự đặt, dùng khi Facebook xác minh webhook (GET /webhooks/messenger)
    public string VerifyToken { get; set; } = "chumchat-verify-token";

    // Do OAuth tự điền: token của Page (loại dài hạn, không cần refresh)
    public string PageAccessToken { get; set; } = "";
    public string PageId { get; set; } = "";
    public string AccountName { get; set; } = "";
}

// App tạo tại https://open.shopee.com
public class ShopeeOptions
{
    public string PartnerId { get; set; } = "";
    public string PartnerKey { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://partner.shopeemobile.com";

    // Do OAuth tự điền sau khi shop cấp quyền
    public string ShopId { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime? TokenExpiresAt { get; set; }
    public string AccountName { get; set; } = "";
}

// Zalo cá nhân qua sidecar zca-js (xem thư mục sidecars/zalo-personal).
// KHÔNG phải API chính thức — Zalo có thể khóa tài khoản nếu phát hiện.
public class ZaloPersonalOptions
{
    // Địa chỉ sidecar Node.js (chạy cùng máy với app)
    public string SidecarUrl { get; set; } = "http://localhost:3311";

    // Chuỗi bí mật tự đặt, phải trùng với API_KEY của sidecar
    public string ApiKey { get; set; } = "";

    public string AccountName { get; set; } = "";
}

// Gợi ý trả lời bằng AI. Hỗ trợ nhiều nhà cung cấp: Claude/Anthropic, OpenAI (ChatGPT), Google Gemini, DeepSeek.
public class AiOptions
{
    // "anthropic" | "openai" | "gemini" | "deepseek"
    public string Provider { get; set; } = "anthropic";

    public string ApiKey { get; set; } = "";

    // Model của nhà cung cấp đang chọn (VD claude-opus-4-8, gpt-4o, gemini-2.5-flash, deepseek-chat)
    public string Model { get; set; } = "claude-opus-4-8";

    // Thông tin shop để AI trả lời đúng bối cảnh (tên shop, sản phẩm, chính sách, giọng điệu...)
    public string ShopContext { get; set; } = "";
}

// Tích hợp Trello: tạo thẻ khi chốt đơn — lấy key/token tại trello.com/power-ups/admin
public class TrelloOptions
{
    public string ApiKey { get; set; } = "";
    public string Token { get; set; } = "";

    // ID của danh sách (list/cột) trên board sẽ nhận thẻ mới
    public string ListId { get; set; } = "";
}

// App tạo tại https://partner.tiktokshop.com
public class TikTokShopOptions
{
    public string AppKey { get; set; } = "";
    public string AppSecret { get; set; } = "";
    public string ApiBaseUrl { get; set; } = "https://open-api.tiktokglobalshop.com";

    // Do OAuth tự điền sau khi shop cấp quyền
    public string ShopCipher { get; set; } = "";
    public string AccessToken { get; set; } = "";
    public string RefreshToken { get; set; } = "";
    public DateTime? TokenExpiresAt { get; set; }
    public string AccountName { get; set; } = "";
}
