using ChumChat.Web.Channels;
using ChumChat.Web.Data;
using ChumChat.Web.Services;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace ChumChat.Web.Controllers;

[ApiController]
[Route("api/v1/mobile")]
public class MobileApiController(
    InboxService inbox,
    StaffService staffSvc,
    ChannelSettingsStore store,
    ILogger<MobileApiController> logger) : ControllerBase
{
    // DTOs cho Mobile API
    public record MobileLoginRequest(string Username, string Password);
    public record MobileLoginResponse(bool Success, string Message, int? StaffId, string? Name, bool IsAdmin);
    public record MobileReplyRequest(string Text, string? ImageUrl);
    public record MobileToggleAiRequest(bool Enabled);

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] MobileLoginRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
            return BadRequest(new MobileLoginResponse(false, "Vui lòng nhập đầy đủ tên đăng nhập và mật khẩu.", null, null, false));

        var staff = await staffSvc.ValidateLoginAsync(req.Username, req.Password);
        if (staff is null)
            return Unauthorized(new MobileLoginResponse(false, "Sai tên đăng nhập hoặc mật khẩu.", null, null, false));

        return Ok(new MobileLoginResponse(true, "Đăng nhập thành công", staff.Id, staff.DisplayName, staff.IsAdmin));
    }

    [HttpGet("conversations")]
    public async Task<IActionResult> GetConversations(
        [FromQuery] ChannelType? channel = null,
        [FromQuery] bool mineOnly = false,
        [FromQuery] int? staffId = null,
        [FromQuery] string? search = null)
    {
        int? assignedId = mineOnly && staffId.HasValue ? staffId.Value : null;
        var convs = await inbox.GetConversationsAsync(channel, assignedId, search);
        return Ok(convs);
    }

    [HttpGet("conversations/{id:int}/messages")]
    public async Task<IActionResult> GetMessages(int id)
    {
        try
        {
            await inbox.MarkReadAsync(id);
            var messages = await inbox.GetMessagesAsync(id);
            var orders = await inbox.GetOrdersAsync(id);
            return Ok(new { messages, orders });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi khi lấy tin nhắn cho cuộc hội thoại {Id}", id);
            return BadRequest(new { error = $"Lỗi lấy dữ liệu tin nhắn: {ex.Message}" });
        }
    }

    [HttpPost("conversations/{id:int}/reply")]
    public async Task<IActionResult> SendReply(int id, [FromBody] MobileReplyRequest req)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(req.Text) && string.IsNullOrWhiteSpace(req.ImageUrl))
                return BadRequest(new { error = "Nội dung tin nhắn không được để trống" });

            ReplyImage? img = null;
            if (!string.IsNullOrWhiteSpace(req.ImageUrl))
            {
                img = new ReplyImage(req.ImageUrl, req.ImageUrl, [], Path.GetFileName(req.ImageUrl));
            }

            var msg = await inbox.SendReplyAsync(id, req.Text ?? "", img);
            if (msg.Status == MessageStatus.Failed)
            {
                return BadRequest(new { error = !string.IsNullOrWhiteSpace(msg.Error) ? msg.Error : "Gửi tin nhắn thất bại qua kênh kết nối nền tảng." });
            }
            return Ok(new
            {
                id = msg.Id,
                conversationId = msg.ConversationId,
                direction = (int)msg.Direction,
                status = (int)msg.Status,
                text = msg.Text,
                attachmentUrl = msg.AttachmentUrl,
                externalMessageId = msg.ExternalMessageId,
                error = msg.Error,
                sentAt = msg.SentAt
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi SendReply Mobile API cho cuộc hội thoại {Id}", id);
            return StatusCode(500, new { error = ex.Message });
        }
    }

    [HttpPost("conversations/{id:int}/read")]
    public async Task<IActionResult> MarkRead(int id)
    {
        await inbox.MarkReadAsync(id);
        return Ok(new { success = true });
    }

    [HttpGet("ai/status")]
    public IActionResult GetAiStatus()
    {
        return Ok(new { enabled = store.Ai.IsAutoReplyEnabled });
    }

    [HttpPost("ai/toggle")]
    public async Task<IActionResult> ToggleAi([FromBody] MobileToggleAiRequest req)
    {
        var opts = store.Ai;
        opts.IsAutoReplyEnabled = req.Enabled;
        await store.SaveAiAsync(opts);
        return Ok(new { enabled = store.Ai.IsAutoReplyEnabled });
    }

    [HttpGet("products")]
    public async Task<IActionResult> GetProducts()
    {
        var products = await inbox.GetAllProductsAsync();
        return Ok(products);
    }

    [HttpGet("orders/summary")]
    public async Task<IActionResult> GetOrdersSummary([FromQuery] string? search, [FromQuery] string? product, [FromQuery] string? area, [FromQuery] bool? grouped, [FromQuery] string? status)
    {
        var orders = await inbox.GetAllOrdersSummaryAsync(search, product, area, grouped, status ?? "active");
        return Ok(orders);
    }

    [HttpPost("orders/{id}/status")]
    public async Task<IActionResult> UpdateOrderStatus(int id, [FromBody] MobileUpdateStatusRequest req)
    {
        await inbox.UpdateOrderStatusAsync(id, req.Status);
        return Ok(new { success = true });
    }

    [HttpPost("conversations/{id}/analyze-customer")]
    public async Task<IActionResult> AnalyzeCustomer(int id, [FromServices] AiSuggestionService aiSvc)
    {
        var conv = await inbox.AnalyzeCustomerProfileAsync(id, aiSvc);
        if (conv == null) return NotFound(new { message = "Hội thoại không tồn tại" });
        return Ok(new { customerTags = conv.CustomerTags, aiCustomerNote = conv.AiCustomerNote });
    }

    [HttpPost("orders/group")]
    public async Task<IActionResult> GroupOrders([FromBody] MobileGroupOrdersRequest req)
    {
        if (req.OrderIds == null || req.OrderIds.Count == 0)
            return BadRequest(new { error = "Chưa chọn đơn hàng nào" });

        var code = await inbox.BatchGroupOrdersAsync(req.OrderIds, req.BatchCode);
        return Ok(new { success = true, batchCode = code });
    }

    [HttpPost("orders/ungroup")]
    public async Task<IActionResult> UngroupOrders([FromBody] MobileGroupOrdersRequest req)
    {
        if (req.OrderIds == null || req.OrderIds.Count == 0)
            return BadRequest(new { error = "Chưa chọn đơn hàng nào" });

        await inbox.BatchUngroupOrdersAsync(req.OrderIds);
        return Ok(new { success = true });
    }

    [HttpPost("orders/clear")]
    public async Task<IActionResult> ClearAllOrders()
    {
        await inbox.ClearAllOrdersAsync();
        return Ok(new { success = true });
    }

    [HttpPost("orders/lalamove-estimate")]
    public async Task<IActionResult> EstimateLalamoveGroupFee([FromBody] MobileGroupOrdersRequest req, [FromServices] LalamoveService lalamoveSvc, [FromServices] ChannelSettingsStore store)
    {
        if (req.OrderIds == null || req.OrderIds.Count == 0)
            return BadRequest(new { error = "Chưa chọn đơn hàng nào" });

        var lalaOpts = store.Lalamove;
        if (string.IsNullOrEmpty(lalaOpts.ApiKey))
            return BadRequest(new { error = "Chưa cấu hình Lalamove API Key trong phần Cài đặt" });

        var orders = await inbox.GetOrdersByIdsAsync(req.OrderIds);
        if (orders.Count == 0)
            return BadRequest(new { error = "Không tìm thấy đơn hàng" });

        var recipients = new List<LalamoveMultiStopRecipient>();
        foreach (var o in orders)
        {
            var addr = o.CustomerAddress;
            if (string.IsNullOrWhiteSpace(addr)) continue;

            var coords = await lalamoveSvc.GeocodeAddressAsync(addr);
            if (coords.Lat == 0 && coords.Lng == 0)
            {
                coords = (lalaOpts.SenderLat != 0 ? lalaOpts.SenderLat : 21.028511, lalaOpts.SenderLng != 0 ? lalaOpts.SenderLng : 105.854444);
            }

            recipients.Add(new LalamoveMultiStopRecipient
            {
                OrderId = o.Id,
                Name = string.IsNullOrWhiteSpace(o.Title) ? "Khách hàng" : o.Title,
                Phone = string.IsNullOrWhiteSpace(o.CustomerPhone) ? "0900000000" : o.CustomerPhone,
                Address = addr,
                Lat = coords.Lat,
                Lng = coords.Lng
            });
        }

        if (recipients.Count == 0)
            return BadRequest(new { error = "Các đơn hàng được chọn chưa có địa chỉ giao hàng hợp lệ" });

        try
        {
            var res = await lalamoveSvc.EstimateMultiStopFeeAsync(
                lalaOpts.SenderLat, lalaOpts.SenderLng, lalaOpts.SenderAddress,
                recipients, lalaOpts);

            return Ok(new
            {
                quotationId = res.QuotationId,
                totalFee = res.TotalFee,
                count = recipients.Count,
                recipients = res.Recipients
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("orders/lalamove-book")]
    public async Task<IActionResult> BookLalamoveGroupOrders([FromBody] MobileBookLalamoveRequest req, [FromServices] LalamoveService lalamoveSvc, [FromServices] ChannelSettingsStore store)
    {
        if (req.OrderIds == null || req.OrderIds.Count == 0)
            return BadRequest(new { error = "Chưa chọn đơn hàng nào" });

        var lalaOpts = store.Lalamove;
        if (string.IsNullOrEmpty(lalaOpts.ApiKey))
            return BadRequest(new { error = "Chưa cấu hình Lalamove API Key trong phần Cài đặt" });

        var orders = await inbox.GetOrdersByIdsAsync(req.OrderIds);
        if (orders.Count == 0)
            return BadRequest(new { error = "Không tìm thấy đơn hàng" });

        var recipients = new List<LalamoveMultiStopRecipient>();
        foreach (var o in orders)
        {
            var addr = o.CustomerAddress;
            if (string.IsNullOrWhiteSpace(addr)) continue;

            var coords = await lalamoveSvc.GeocodeAddressAsync(addr);
            if (coords.Lat == 0 && coords.Lng == 0)
            {
                coords = (lalaOpts.SenderLat != 0 ? lalaOpts.SenderLat : 21.028511, lalaOpts.SenderLng != 0 ? lalaOpts.SenderLng : 105.854444);
            }

            recipients.Add(new LalamoveMultiStopRecipient
            {
                OrderId = o.Id,
                Name = string.IsNullOrWhiteSpace(o.Title) ? "Khách hàng" : o.Title,
                Phone = string.IsNullOrWhiteSpace(o.CustomerPhone) ? "0900000000" : o.CustomerPhone,
                Address = addr,
                Lat = coords.Lat,
                Lng = coords.Lng
            });
        }

        try
        {
            string quotationId = req.QuotationId ?? "";
            string senderStopId = "";
            
            var est = await lalamoveSvc.EstimateMultiStopFeeAsync(
                lalaOpts.SenderLat, lalaOpts.SenderLng, lalaOpts.SenderAddress,
                recipients, lalaOpts);
            quotationId = est.QuotationId;
            senderStopId = est.SenderStopId;
            recipients = est.Recipients;

            var bookRes = await lalamoveSvc.CreateMultiStopOrderAsync(
                quotationId,
                senderStopId,
                lalaOpts.SenderName, lalaOpts.SenderMobile,
                recipients, lalaOpts);

            var batchCode = await inbox.BatchGroupOrdersAsync(req.OrderIds, req.BatchCode);
            await inbox.BatchUpdateLalamoveOrderInfoAsync(req.OrderIds, bookRes.OrderId, bookRes.ShareLink);

            return Ok(new
            {
                success = true,
                orderId = bookRes.OrderId,
                shareLink = bookRes.ShareLink,
                batchCode = batchCode,
                message = $"✔ Đã đặt thành công chuyến ghép Lalamove ({bookRes.OrderId}) cho {req.OrderIds.Count} đơn!"
            });
        }
        catch (Exception ex)
        {
            return BadRequest(new { error = ex.Message });
        }
    }

    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] Order order)
    {
        if (order.ConversationId <= 0)
            return BadRequest(new { error = "ConversationId không hợp lệ" });

        var created = await inbox.CreateOrderAsync(order);
        return Ok(created);
    }
}

public record MobileGroupOrdersRequest(List<int> OrderIds, string? BatchCode);
public record MobileBookLalamoveRequest(List<int> OrderIds, string? BatchCode, string? QuotationId);
public record MobileUpdateStatusRequest(string Status);
