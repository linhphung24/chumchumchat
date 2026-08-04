namespace ChumChat.Web.Data;

public enum ChannelType
{
    Zalo,
    Messenger,
    Shopee,
    TikTokShop,

    // Zalo tài khoản cá nhân, qua sidecar zca-js (unofficial — có rủi ro khóa tài khoản)
    ZaloPersonal,
    
    // Facebook Messenger tài khoản cá nhân, qua sidecar (unofficial)
    MessengerPersonal,
    
    Instagram,
    Threads,
    GoogleLocation
}

public enum MessageDirection
{
    Inbound,
    Outbound
}

public enum MessageStatus
{
    Sent,
    Failed,
    Simulated
}

public class Conversation
{
    public int Id { get; set; }

    public ChannelType Channel { get; set; }

    // Định danh hội thoại/khách hàng phía nền tảng
    // (Zalo: user_id, Messenger: PSID, Shopee: to_id, TikTok: conversation_id)
    public string ExternalId { get; set; } = "";

    public string CustomerName { get; set; } = "";

    // Ảnh đại diện thật của khách (lấy qua API getprofile / Graph)
    public string? AvatarUrl { get; set; }

    // Nhân viên được phân công phụ trách hội thoại này (null = chưa ai nhận)
    public int? AssignedStaffId { get; set; }

    public DateTime LastMessageAt { get; set; }

    public string LastMessagePreview { get; set; } = "";

    public int UnreadCount { get; set; }

    // Thẻ trạng thái do nhân viên gắn: Đang tư vấn / Chờ phản hồi / Đã chốt đơn / Hủy
    public string Tag { get; set; } = "";
    
    public string CustomerPhone { get; set; } = "";
    public string CustomerAddress { get; set; } = "";
 
    // Ghi chú & Đánh giá phân loại khách hàng do AI / Hệ thống phân tích (VD: "⚡ Chốt nhanh", "💳 CK trước", "📦 Khách sỉ", "🧐 Cần tư vấn kỹ")
    public string? CustomerTags { get; set; } = "";
    public string? AiCustomerNote { get; set; } = "";

    public List<Message> Messages { get; set; } = [];
}

// Tài khoản nhân viên đăng nhập vào app
public class Staff
{
    public int Id { get; set; }

    public string Username { get; set; } = "";

    public string DisplayName { get; set; } = "";

    // Mật khẩu băm bằng PBKDF2 (không lưu plaintext)
    public string PasswordHash { get; set; } = "";

    public bool IsAdmin { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTime CreatedAt { get; set; }
}

// Kịch bản trả lời tự động: khách hỏi trúng thì bot tự gửi file (PDF/ảnh) + tin nhắn kèm
public class AutoReplyRule
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    // Từ khóa kích hoạt (mỗi dòng/dấu phẩy một từ), khớp không phân biệt hoa thường
    public string Keywords { get; set; } = "";

    // Mô tả ý định để AI hiểu (VD "khi khách hỏi giá nhân bánh trung thu") — tùy chọn
    public string MatchDescription { get; set; } = "";

    // Tin nhắn gửi kèm file (tùy chọn)
    public string ReplyText { get; set; } = "";

    // File đính kèm lưu tại /uploads/auto/...
    public string FileUrl { get; set; } = "";
    public string FileName { get; set; } = "";
    public string FileMime { get; set; } = "";

    public bool Enabled { get; set; } = true;

    public int SortOrder { get; set; }
}

// Câu hỏi thường gặp — AI dùng làm kiến thức để trả lời khách
public class AiFaq
{
    public int Id { get; set; }

    public string Question { get; set; } = "";

    public string Answer { get; set; } = "";

    public int SortOrder { get; set; }
}

// Ảnh tư liệu (menu, bảng giá, catalog...) — AI đọc bằng thị giác để trả lời
public class AiKnowledgeImage
{
    public int Id { get; set; }

    // Đường dẫn nội bộ /uploads/ai/...
    public string Url { get; set; } = "";

    // Mô tả ngắn giúp AI hiểu ảnh chứa gì
    public string Caption { get; set; } = "";

    public DateTime CreatedAt { get; set; }
}

// Câu trả lời mẫu dùng nhanh trong khung chat
public class QuickReply
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    public string Content { get; set; } = "";

    public int SortOrder { get; set; }
}

// Sản phẩm — dùng để chọn nhanh khi tạo đơn hàng
public class Product
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public string Sku { get; set; } = "";

    // Giá bán mặc định (VND)
    public long Price { get; set; }

    public string ImageUrl { get; set; } = "";

    public string Description { get; set; } = "";

    public bool IsActive { get; set; } = true;

    // Số lượng còn lại trong kho (Tồn kho)
    public int StockQuantity { get; set; } = 999;
}

// Lịch sử đặt hàng của khách, ghi tay hoặc tự ghi khi tạo thẻ Trello
public class Order
{
    public int Id { get; set; }

    public int ConversationId { get; set; }

    public Conversation? Conversation { get; set; }

    public string Title { get; set; } = "";

    // Tổng tiền VND (tính từ items + shipping - discount)
    public long Amount { get; set; }

    public string Note { get; set; } = "";

    public string? TrelloCardUrl { get; set; }

    public DateTime CreatedAt { get; set; }

    // Thông tin khách hàng khi tạo đơn
    public string CustomerPhone { get; set; } = "";

    public string CustomerAddress { get; set; } = "";

    // Hình thức thanh toán: "COD", "Chuyển khoản", "Tiền mặt"
    public string PaymentMethod { get; set; } = "";

    public long ShippingFee { get; set; }

    public long Discount { get; set; }

    // Danh sách sản phẩm trong đơn
    public List<OrderItem> Items { get; set; } = [];

    // Giao hàng Ahamove
    public string? AhamoveOrderId { get; set; }
    public string? AhamoveTrackingLink { get; set; }
    public string? AhamoveStatus { get; set; }

    // Đơn ghép chuyến giao
    public bool IsGrouped { get; set; }
    public string? GroupBatchCode { get; set; }
}

// Dòng sản phẩm trong đơn hàng
public class OrderItem
{
    public int Id { get; set; }

    public int OrderId { get; set; }

    public Order? Order { get; set; }

    public string ProductName { get; set; } = "";

    public int Quantity { get; set; } = 1;

    // Đơn giá VND
    public long UnitPrice { get; set; }
}

// Cấu hình chung không gắn với kênh chat nào (VD: Trello), lưu JSON theo key
public class AppSetting
{
    public int Id { get; set; }

    public string Key { get; set; } = "";

    public string Json { get; set; } = "{}";
}

// Cấu hình + token của một kênh, lưu dạng JSON (mỗi kênh một dòng)
// để người dùng chỉnh qua trang /settings thay vì sửa appsettings.json
public class ChannelConnection
{
    public int Id { get; set; }

    public ChannelType Channel { get; set; }

    public string SettingsJson { get; set; } = "{}";

    public DateTime UpdatedAt { get; set; }
}

public class Message
{
    public int Id { get; set; }

    public int ConversationId { get; set; }

    public Conversation? Conversation { get; set; }

    public MessageDirection Direction { get; set; }

    public MessageStatus Status { get; set; } = MessageStatus.Sent;

    public string Text { get; set; } = "";

    // URL ảnh đính kèm (ảnh khách gửi: URL CDN của nền tảng; ảnh shop gửi: /uploads/... nội bộ)
    public string? AttachmentUrl { get; set; }

    public string? ExternalMessageId { get; set; }

    // Ghi chú lỗi khi gửi thất bại
    public string? Error { get; set; }

    public DateTime SentAt { get; set; }
}

public class PushSubscription
{
    public int Id { get; set; }
    public int StaffId { get; set; }
    public string Endpoint { get; set; } = "";
    public string P256dh { get; set; } = "";
    public string Auth { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
