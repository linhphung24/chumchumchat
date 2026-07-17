using System.Text.Json;
using ChumChat.Web.Data;
using Microsoft.EntityFrameworkCore;
using WebPush;
using PushSubscription = ChumChat.Web.Data.PushSubscription;

namespace ChumChat.Web.Services;

public class PushNotificationService(
    IDbContextFactory<AppDbContext> dbFactory,
    ChannelSettingsStore settingsStore,
    ILogger<PushNotificationService> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task SubscribeAsync(int staffId, string endpoint, string p256dh, string auth)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var sub = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint);
        if (sub is null)
        {
            sub = new PushSubscription { Endpoint = endpoint };
            db.PushSubscriptions.Add(sub);
        }
        sub.StaffId = staffId;
        sub.P256dh = p256dh;
        sub.Auth = auth;
        sub.CreatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task UnsubscribeAsync(string endpoint)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var sub = await db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint);
        if (sub is not null)
        {
            db.PushSubscriptions.Remove(sub);
            await db.SaveChangesAsync();
        }
    }

    public async Task SendNotificationToStaffAsync(int? staffId, string title, string message, string url)
    {
        var vapid = settingsStore.Vapid;
        if (string.IsNullOrEmpty(vapid.PublicKey) || string.IsNullOrEmpty(vapid.PrivateKey))
        {
            logger.LogWarning("VAPID Keys are not generated/loaded, skipping Push.");
            return;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        IQueryable<PushSubscription> query = db.PushSubscriptions.AsNoTracking();
        if (staffId.HasValue)
        {
            query = query.Where(s => s.StaffId == staffId.Value);
        }

        var subs = await query.ToListAsync();
        if (subs.Count == 0) return;

        var payloadObj = new { title, message, url };
        var payloadJson = JsonSerializer.Serialize(payloadObj, JsonOpts);

        var details = new VapidDetails(vapid.Subject, vapid.PublicKey, vapid.PrivateKey);
        var webPushClient = new WebPushClient();

        foreach (var sub in subs)
        {
            // Gửi bất đồng bộ ở chế độ nền để không làm chậm luồng chính
            _ = Task.Run(async () =>
            {
                try
                {
                    var pushSub = new WebPush.PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                    await webPushClient.SendNotificationAsync(pushSub, payloadJson, details);
                    logger.LogDebug("Push sent to staff {StaffId} endpoint {Endpoint}", sub.StaffId, sub.Endpoint);
                }
                catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    logger.LogInformation("Push subscription gone/expired for staff {StaffId}, removing: {Endpoint}", sub.StaffId, sub.Endpoint);
                    await UnsubscribeAsync(sub.Endpoint);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Failed to send push to staff {StaffId}", sub.StaffId);
                }
            });
        }
    }

    public class PushTestResult
    {
        public int TotalSubscriptions { get; set; }
        public int SuccessCount { get; set; }
        public List<string> Errors { get; set; } = new();
    }

    public async Task<PushTestResult> SendTestNotificationAsync(string title, string message, string url)
    {
        var result = new PushTestResult();
        var vapid = settingsStore.Vapid;
        if (string.IsNullOrEmpty(vapid.PublicKey) || string.IsNullOrEmpty(vapid.PrivateKey))
        {
            result.Errors.Add("VAPID Keys are not generated/loaded on server.");
            return result;
        }

        await using var db = await dbFactory.CreateDbContextAsync();
        var subs = await db.PushSubscriptions.AsNoTracking().ToListAsync();
        result.TotalSubscriptions = subs.Count;

        if (subs.Count == 0)
        {
            result.Errors.Add("Không có thiết bị (subscription) nào trong cơ sở dữ liệu.");
            return result;
        }

        var payloadObj = new { title, message, url };
        var payloadJson = JsonSerializer.Serialize(payloadObj, JsonOpts);
        var details = new VapidDetails(vapid.Subject, vapid.PublicKey, vapid.PrivateKey);
        var webPushClient = new WebPushClient();

        foreach (var sub in subs)
        {
            try
            {
                var pushSub = new WebPush.PushSubscription(sub.Endpoint, sub.P256dh, sub.Auth);
                await webPushClient.SendNotificationAsync(pushSub, payloadJson, details);
                result.SuccessCount++;
            }
            catch (WebPushException ex) when (ex.StatusCode == System.Net.HttpStatusCode.Gone || ex.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                result.Errors.Add($"Endpoint đã hết hạn hoặc bị xóa. Tiến hành xóa khỏi database.");
                await UnsubscribeAsync(sub.Endpoint);
            }
            catch (Exception ex)
            {
                result.Errors.Add($"Lỗi gửi đến endpoint ({sub.Endpoint.Substring(0, Math.Min(sub.Endpoint.Length, 30))}...): {ex.Message}");
            }
        }

        return result;
    }
}
