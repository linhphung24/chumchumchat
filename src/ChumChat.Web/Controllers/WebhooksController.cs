using ChumChat.Web.Channels;
using ChumChat.Web.Data;
using ChumChat.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace ChumChat.Web.Controllers;

[ApiController]
[Route("webhooks")]
public class WebhooksController(
    IEnumerable<IChannelAdapter> adapters,
    InboxService inbox,
    AutoReplyService autoReply,
    WebhookLogService webhookLog,
    ChannelSettingsStore settings,
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
}
