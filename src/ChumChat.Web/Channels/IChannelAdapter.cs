using ChumChat.Web.Data;

namespace ChumChat.Web.Channels;

// Một tin nhắn đến, đã được chuẩn hóa từ payload webhook của từng nền tảng
public record InboundMessage(
    string ExternalConversationId,
    string CustomerName,
    string Text,
    string? ExternalMessageId,
    DateTime SentAt,
    string? AttachmentUrl = null);

// Một tin nhắn lịch sử (cả 2 chiều) lấy qua API đồng bộ tin cũ
public record HistoryMessage(
    string ExternalConversationId,
    string CustomerName,
    MessageDirection Direction,
    string Text,
    string? ExternalMessageId,
    DateTime SentAt);

public interface IChannelAdapter
{
    ChannelType Channel { get; }

    // true khi đã điền đủ credentials trong appsettings
    bool IsConfigured { get; }

    // Xác minh chữ ký webhook. Trả về true nếu hợp lệ,
    // hoặc nếu kênh chưa cấu hình secret (để dev/test không bị chặn).
    bool VerifySignature(string rawBody, IHeaderDictionary headers, string requestUrl);

    // Chuẩn hóa payload webhook thành danh sách tin nhắn đến.
    // Payload không phải tin nhắn văn bản (sự kiện follow, đơn hàng...) trả về danh sách rỗng.
    IReadOnlyList<InboundMessage> ParseWebhook(string rawBody);

    // Gửi tin và trả về message_id do nền tảng cấp (để chống trùng khi đồng bộ). null nếu không có.
    Task<string?> SendTextAsync(Conversation conversation, string text, CancellationToken ct = default);

    // Gửi ảnh cho khách. imageUrl: URL công khai của ảnh (Zalo/Messenger dùng trực tiếp);
    // imageBytes + fileName: dữ liệu gốc cho kênh bắt buộc upload qua API riêng (Shopee/TikTok).
    // Trả về message_id nếu có.
    Task<string?> SendImageAsync(Conversation conversation, string imageUrl, byte[] imageBytes, string fileName, CancellationToken ct = default);

    // Gửi file (PDF, tài liệu...) cho khách. fileUrl công khai + bytes gốc. Kênh không hỗ trợ file
    // thì gửi tạm đường link. Trả về message_id nếu có.
    Task<string?> SendFileAsync(Conversation conversation, string fileUrl, byte[] fileBytes, string fileName, string mimeType, CancellationToken ct = default);

    // Kéo lịch sử hội thoại gần nhất từ API của nền tảng (đồng bộ tin cũ).
    // Chỉ gọi được khi IsConfigured (đã có access token).
    Task<IReadOnlyList<HistoryMessage>> FetchHistoryAsync(int maxConversations, CancellationToken ct = default);

    // Lấy tên + avatar thật của khách từ API nền tảng. Trả về null nếu không hỗ trợ/không lấy được.
    Task<CustomerProfile?> FetchProfileAsync(string externalId, CancellationToken ct = default);

    // Kiểm tra token còn sống không: gọi thử một API đọc nhẹ và trả về kết quả để chẩn đoán.
    Task<ConnectionTestResult> TestConnectionAsync(CancellationToken ct = default);
}

public record ConnectionTestResult(bool Ok, string Message);

public record CustomerProfile(string? Name, string? AvatarUrl);
