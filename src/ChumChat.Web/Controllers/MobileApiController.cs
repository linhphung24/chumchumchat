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
        await inbox.MarkReadAsync(id);
        var messages = await inbox.GetMessagesAsync(id);
        var orders = await inbox.GetOrdersAsync(id);
        return Ok(new { messages, orders });
    }

    [HttpPost("conversations/{id:int}/reply")]
    public async Task<IActionResult> SendReply(int id, [FromBody] MobileReplyRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Text) && string.IsNullOrWhiteSpace(req.ImageUrl))
            return BadRequest(new { error = "Nội dung tin nhắn không được để trống" });

        ReplyImage? img = null;
        if (!string.IsNullOrWhiteSpace(req.ImageUrl))
        {
            img = new ReplyImage(req.ImageUrl, req.ImageUrl, [], Path.GetFileName(req.ImageUrl));
        }

        var msg = await inbox.SendReplyAsync(id, req.Text ?? "", img);
        return Ok(msg);
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

    [HttpPost("orders")]
    public async Task<IActionResult> CreateOrder([FromBody] Order order)
    {
        if (order.ConversationId <= 0)
            return BadRequest(new { error = "ConversationId không hợp lệ" });

        var created = await inbox.CreateOrderAsync(order);
        return Ok(created);
    }
}
