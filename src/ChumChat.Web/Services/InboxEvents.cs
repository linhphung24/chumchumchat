namespace ChumChat.Web.Services;

// Event bus in-process: webhook nhận tin mới → báo cho các circuit Blazor đang mở
// để UI tự refresh mà không cần polling.
public class InboxEvents
{
    public event Action? Changed;

    public void NotifyChanged() => Changed?.Invoke();
}
