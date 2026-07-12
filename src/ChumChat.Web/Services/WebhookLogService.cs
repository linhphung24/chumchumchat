using System.Collections.Concurrent;
using ChumChat.Web.Data;

namespace ChumChat.Web.Services;

public record WebhookLogEntry(DateTime At, ChannelType Channel, string Status, string Detail);

// Nhật ký webhook trong RAM (100 sự kiện gần nhất kể từ khi app khởi động) —
// để admin tự chẩn đoán "nền tảng có gọi tới không / vì sao tin bị loại" mà không cần SSH xem log.
public class WebhookLogService
{
    public const string StatusSaved = "saved";
    public const string StatusDuplicate = "duplicate";
    public const string StatusSignatureFail = "signature_fail";
    public const string StatusNoMessage = "no_message";
    public const string StatusError = "error";

    private readonly ConcurrentQueue<WebhookLogEntry> entries = new();

    public void Add(ChannelType channel, string status, string detail)
    {
        var trimmed = detail.Length > 400 ? detail[..400] + "…" : detail;
        entries.Enqueue(new WebhookLogEntry(DateTime.UtcNow, channel, status, trimmed));
        while (entries.Count > 100 && entries.TryDequeue(out _)) { }
    }

    // Mới nhất trước
    public IReadOnlyList<WebhookLogEntry> GetRecent() => entries.Reverse().ToList();
}
