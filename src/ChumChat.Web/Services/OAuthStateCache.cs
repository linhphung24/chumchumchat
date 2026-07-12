using System.Collections.Concurrent;
using ChumChat.Web.Data;

namespace ChumChat.Web.Services;

// Chống CSRF cho luồng OAuth: phát "state" ngẫu nhiên khi bắt đầu,
// nền tảng trả lại state ở callback thì mới chấp nhận.
// Kèm chỗ chứa tạm danh sách Page Facebook giữa bước callback và bước chọn page.
public class OAuthStateCache
{
    public record FacebookPage(string Id, string Name, string AccessToken);

    private sealed record Entry(ChannelType Channel, DateTime ExpiresAt)
    {
        public List<FacebookPage>? Pages { get; set; }
    }

    private readonly ConcurrentDictionary<string, Entry> entries = new();

    public string Create(ChannelType channel)
    {
        Cleanup();
        var state = Guid.NewGuid().ToString("N");
        entries[state] = new Entry(channel, DateTime.UtcNow.AddMinutes(15));
        return state;
    }

    public bool Validate(string? state, ChannelType channel) =>
        state is not null &&
        entries.TryGetValue(state, out var e) &&
        e.Channel == channel && e.ExpiresAt > DateTime.UtcNow;

    public void StorePages(string state, List<FacebookPage> pages)
    {
        if (entries.TryGetValue(state, out var e))
            e.Pages = pages;
    }

    public List<FacebookPage>? GetPages(string state) =>
        entries.TryGetValue(state, out var e) && e.ExpiresAt > DateTime.UtcNow ? e.Pages : null;

    public void Remove(string state) => entries.TryRemove(state, out _);

    private void Cleanup()
    {
        foreach (var (key, entry) in entries)
            if (entry.ExpiresAt <= DateTime.UtcNow)
                entries.TryRemove(key, out _);
    }
}
