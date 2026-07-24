using ChumChat.Web.Channels;
using ChumChat.Web.Data;
using ChumChat.Web.Services;
using Microsoft.AspNetCore.Mvc;

using Microsoft.EntityFrameworkCore;

namespace ChumChat.Web.Controllers;

[ApiController]
[Route("webhooks")]
public class WebhooksController(
    IEnumerable<IChannelAdapter> adapters,
    InboxService inbox,
    AutoReplyService autoReply,
    WebhookLogService webhookLog,
    ChannelSettingsStore settings,
    IDbContextFactory<AppDbContext> dbFactory,
    ILogger<WebhooksController> logger) : ControllerBase
{
    // Facebook gọi GET này một lần khi đăng ký webhook để xác minh
    [HttpGet("messenger")]
    public IActionResult VerifyMessenger(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && verifyToken == settings.Messenger.VerifyToken)
            return Content(challenge ?? "");
        return Forbid();
    }

    [HttpGet("instagram")]
    public IActionResult VerifyInstagram(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && verifyToken == settings.Instagram.VerifyToken)
            return Content(challenge ?? "");
        return Forbid();
    }

    [HttpPost("zalo")]
    public Task<IActionResult> Zalo() => HandleAsync(ChannelType.Zalo);

    [HttpPost("messenger")]
    public Task<IActionResult> Messenger() => HandleAsync(ChannelType.Messenger);

    [HttpPost("instagram")]
    public Task<IActionResult> Instagram() => HandleAsync(ChannelType.Instagram);

    [HttpPost("shopee")]
    public Task<IActionResult> Shopee() => HandleAsync(ChannelType.Shopee);

    [HttpPost("tiktok")]
    public Task<IActionResult> TikTok() => HandleAsync(ChannelType.TikTokShop);

    // Nhận tin từ sidecar Zalo cá nhân (zca-js), không phải từ Zalo trực tiếp
    [HttpPost("zalopersonal")]
    public Task<IActionResult> ZaloPersonal() => HandleAsync(ChannelType.ZaloPersonal);

    [HttpPost("messengerpersonal")]
    public Task<IActionResult> MessengerPersonal() => HandleAsync(ChannelType.MessengerPersonal);

    // Threads gọi GET này một lần khi đăng ký webhook để xác minh
    [HttpGet("threads")]
    public IActionResult VerifyThreads(
        [FromQuery(Name = "hub.mode")] string? mode,
        [FromQuery(Name = "hub.verify_token")] string? verifyToken,
        [FromQuery(Name = "hub.challenge")] string? challenge)
    {
        if (mode == "subscribe" && verifyToken == settings.Threads.VerifyToken)
            return Content(challenge ?? "");
        return Forbid();
    }

    [HttpPost("threads")]
    public Task<IActionResult> Threads() => HandleAsync(ChannelType.Threads);

    private async Task<IActionResult> HandleAsync(ChannelType channel)
    {
        var adapter = adapters.First(a => a.Channel == channel);
        if (!adapter.IsConfigured)
        {
            logger.LogWarning("{Channel}: Nhận tin từ webhook nhưng adapter chưa được cấu hình. Bỏ qua.", channel);
            return Ok();
        }

        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        var requestUrl = $"{Request.Scheme}://{Request.Host}{Request.Path}";
        if (!adapter.VerifySignature(rawBody, Request.Headers, requestUrl))
        {
            // Vẫn trả 200: các nền tảng gửi request "test webhook" không kèm chữ ký
            // và đòi 200 mới cho lưu cấu hình. Payload không xác minh được thì bỏ qua,
            // không xử lý — kèm log đủ dữ liệu để đối chiếu công thức chữ ký nếu cần.
            logger.LogWarning("{Channel}: chữ ký webhook không hợp lệ — bỏ qua payload. Headers: {Headers}. Body: {Body}",
                channel,
                string.Join("; ", Request.Headers
                    .Where(h => h.Key.StartsWith("X-") || h.Key == "Authorization")
                    .Select(h => $"{h.Key}={h.Value}")),
                rawBody);

            var errorDetail = rawBody;
            if (adapter is ZaloAdapter zaloAdapter && !string.IsNullOrEmpty(zaloAdapter.LastVerificationError))
            {
                errorDetail = $"{zaloAdapter.LastVerificationError} | Body: {rawBody}";
            }

            webhookLog.Add(channel, WebhookLogService.StatusSignatureFail, errorDetail);
            return Ok();
        }

        try
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var savedAny = false;
            foreach (var inbound in adapter.ParseWebhook(rawBody))
            {
                savedAny = true;
                var convId = await inbox.HandleInboundAsync(channel, inbound);
                if (convId is int cid)
                {
                    webhookLog.Add(channel, WebhookLogService.StatusSaved, $"{inbound.CustomerName}: {inbound.Text}");
                    // Tin khách mới (không phải webhook trùng) và là tin văn bản → thử trả lời tự động theo kịch bản
                    if (!string.IsNullOrWhiteSpace(inbound.Text))
                        _ = autoReply.TryAutoReplyAsync(cid, inbound.Text, baseUrl);
                }
                else
                {
                    webhookLog.Add(channel, WebhookLogService.StatusDuplicate, $"{inbound.CustomerName}: {inbound.Text}");
                }
            }
            if (!savedAny)
                webhookLog.Add(channel, WebhookLogService.StatusNoMessage, rawBody);
        }
        catch (Exception ex)
        {
            // Vẫn trả 200 để nền tảng không gửi lại liên tục; lỗi đã ghi log để xử lý
            logger.LogError(ex, "{Channel}: lỗi xử lý webhook. Payload: {Body}", channel, rawBody);
            webhookLog.Add(channel, WebhookLogService.StatusError, $"{ex.Message} | {rawBody}");
        }

        return Ok();
    }

    [HttpPost("lalamove")]
    public async Task<IActionResult> Lalamove()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        logger.LogInformation("Lalamove Webhook: Received payload: {Body}", rawBody);
        webhookLog.Add(ChannelType.Zalo, "Lalamove", rawBody); // Log webhook for transparency in settings tab

        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(rawBody);
            var root = doc.RootElement;
            
            root.TryGetProperty("data", out var data);
            
            var eventName = root.TryGetProperty("event", out var evProp) ? evProp.GetString() : "";
            var orderId = ExtractOrderId(root, data);
            var status = ExtractStatus(root, data);

            if (eventName == "DRIVER_ASSIGNED" && string.IsNullOrEmpty(status))
            {
                status = "ON_GOING";
            }

            if (!string.IsNullOrEmpty(orderId) && !string.IsNullOrEmpty(status))
            {
                await using var db = await dbFactory.CreateDbContextAsync();
                var order = await db.Orders
                    .FirstOrDefaultAsync(o => o.AhamoveOrderId == $"Lala:{orderId}" || o.AhamoveOrderId == orderId);

                if (order is not null)
                {
                    if (order.AhamoveStatus != status)
                    {
                        order.AhamoveStatus = status;
                        await db.SaveChangesAsync();
                        logger.LogInformation("Lalamove Webhook: Updated order {OrderId} status to {Status}", order.Id, status);
                    }

                    if (status == "ON_GOING" && !string.IsNullOrEmpty(order.AhamoveTrackingLink))
                    {
                        var alreadySent = await db.Messages.AnyAsync(m => 
                            m.ConversationId == order.ConversationId && 
                            m.Text.Contains(order.AhamoveTrackingLink));

                        if (!alreadySent)
                        {
                            var msgText = $"🚚 Tiệm đã tìm được tài xế giao hàng Lalamove cho bạn! Bạn có thể theo dõi hành trình di chuyển trực tiếp của tài xế tại đây nhé: {order.AhamoveTrackingLink}";
                            _ = inbox.SendReplyAsync(order.ConversationId, msgText);
                            logger.LogInformation("Lalamove Webhook: Sent driver notification to conversation {ConvId} for order {OrderId}", order.ConversationId, order.Id);
                        }
                    }
                }
                else
                {
                    logger.LogWarning("Lalamove Webhook: Order not found for Lala:{OrderId} or orderId:{OrderId}", orderId, orderId);
                }
            }
            else
            {
                logger.LogWarning("Lalamove Webhook: Could not extract orderId ({OrderId}) or status ({Status}) from event {Event}", orderId, status, eventName);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lalamove Webhook error: {Message}", ex.Message);
        }

        return Ok(new { status = "success" });
    }

    private static string? ExtractOrderId(System.Text.Json.JsonElement root, System.Text.Json.JsonElement data)
    {
        if (data.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (data.TryGetProperty("orderId", out var p1)) return GetRawOrString(p1);
            if (data.TryGetProperty("id", out var p2)) return GetRawOrString(p2);
            if (data.TryGetProperty("order", out var orderObj))
            {
                if (orderObj.TryGetProperty("orderId", out var p3)) return GetRawOrString(p3);
                if (orderObj.TryGetProperty("id", out var p4)) return GetRawOrString(p4);
            }
        }
        if (root.TryGetProperty("orderId", out var p5)) return GetRawOrString(p5);
        if (root.TryGetProperty("id", out var p6)) return GetRawOrString(p6);
        return null;
    }

    private static string? ExtractStatus(System.Text.Json.JsonElement root, System.Text.Json.JsonElement data)
    {
        if (data.ValueKind == System.Text.Json.JsonValueKind.Object)
        {
            if (data.TryGetProperty("status", out var p1)) return p1.GetString();
            if (data.TryGetProperty("order", out var orderObj) && orderObj.TryGetProperty("status", out var p2)) return p2.GetString();
        }
        if (root.TryGetProperty("status", out var p3)) return p3.GetString();
        return null;
    }

    private static string? GetRawOrString(System.Text.Json.JsonElement element)
    {
        return element.ValueKind == System.Text.Json.JsonValueKind.Number 
            ? element.GetRawText() 
            : element.GetString();
    }
}
