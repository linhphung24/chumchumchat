using ChumChat.Web.Channels;
using ChumChat.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace ChumChat.Web.Services;

// Ảnh nhân viên đính kèm khi trả lời: LocalUrl để hiển thị trong app,
// PublicUrl cho kênh dùng URL trực tiếp, Bytes cho kênh bắt buộc upload riêng
public record ReplyImage(string LocalUrl, string PublicUrl, byte[] Bytes, string FileName);

public class InboxService(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<IChannelAdapter> adapters,
    InboxEvents events,
    PushNotificationService pushNotificationService,
    ChannelSettingsStore settings,
    AiSuggestionService aiService,
    ILogger<InboxService> logger)
{
    // Trả về Id hội thoại nếu đã lưu tin mới; null nếu là webhook trùng (bỏ qua).
    public async Task<int?> HandleInboundAsync(ChannelType channel, InboundMessage inbound, bool simulated = false)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        var conversation = await db.Conversations
            .FirstOrDefaultAsync(c => c.Channel == channel && c.ExternalId == inbound.ExternalConversationId);

        var isNew = conversation is null;
        if (conversation is null)
        {
            conversation = new Conversation
            {
                Channel = channel,
                ExternalId = inbound.ExternalConversationId,
                CustomerName = inbound.CustomerName
            };
            db.Conversations.Add(conversation);
        }
        else if (!string.IsNullOrEmpty(inbound.CustomerName) && !inbound.CustomerName.Contains('…'))
        {
            // Tên chứa '…' là placeholder do adapter tự sinh (kênh không gửi kèm tên thật)
            // → không ghi đè lên tên đã có
            conversation.CustomerName = inbound.CustomerName;
        }

        // Nền tảng có thể gửi lại webhook (retry) → chặn trùng theo mã tin nhắn gốc
        if (!string.IsNullOrEmpty(inbound.ExternalMessageId))
        {
            var exists = await db.Messages.AnyAsync(m =>
                m.ConversationId == conversation.Id && m.ExternalMessageId == inbound.ExternalMessageId);
            if (exists)
            {
                logger.LogDebug("{Channel}: tin {MsgId} đã có, bỏ qua", channel, inbound.ExternalMessageId);
                return null;
            }
        }

        conversation.Messages.Add(new Message
        {
            Direction = inbound.Direction,
            Status = simulated ? MessageStatus.Simulated : MessageStatus.Sent,
            Text = inbound.Text,
            AttachmentUrl = inbound.AttachmentUrl,
            ExternalMessageId = inbound.ExternalMessageId,
            SentAt = inbound.SentAt
        });
        conversation.LastMessageAt = inbound.SentAt;
        conversation.LastMessagePreview = Truncate(
            string.IsNullOrEmpty(inbound.Text) && inbound.AttachmentUrl is not null ? "📷 Hình ảnh" : inbound.Text, 80);
        conversation.UnreadCount++;

        await db.SaveChangesAsync();

        // Gửi thông báo đẩy bất đồng bộ cho nhân viên liên quan (cả tin thật lẫn giả lập để test dễ dàng)
        _ = pushNotificationService.SendNotificationToStaffAsync(
            conversation.AssignedStaffId,
            $"Tin mới từ {conversation.CustomerName} ({channel})",
            Truncate(string.IsNullOrEmpty(inbound.Text) && inbound.AttachmentUrl is not null ? "📷 Gửi một file/ảnh đính kèm" : inbound.Text, 100),
            $"/?c={conversation.Id}"
        );

        events.NotifyChanged();

        if (!simulated)
            logger.LogInformation("{Channel}: đã lưu tin mới từ {Name} (msg {MsgId})",
                channel, conversation.CustomerName, inbound.ExternalMessageId ?? "-");

        // Hội thoại mới (không phải giả lập): lấy tên + avatar thật của khách ở chế độ nền,
        // không chặn luồng webhook. Xong thì lưu lại và báo UI cập nhật.
        if (isNew && !simulated)
            _ = FetchAndSaveProfileAsync(channel, conversation.Id, inbound.ExternalConversationId);

        // Kích hoạt AI tự động trả lời nếu được bật và hội thoại chưa gán cho nhân viên
        if (settings.Ai.IsAutoReplyEnabled && conversation.AssignedStaffId == null && !string.IsNullOrWhiteSpace(inbound.Text))
            _ = TriggerAutoPilotAsync(conversation.Id);

        return conversation.Id;
    }

    private async Task TriggerAutoPilotAsync(int conversationId)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync();
            var conversation = await db.Conversations.Include(c => c.Messages).FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is null || conversation.AssignedStaffId != null) return;

            // Lấy 15 tin nhắn gần nhất
            var messages = conversation.Messages.OrderBy(m => m.SentAt).TakeLast(15).ToList();
            var lastInboundText = messages.LastOrDefault(m => m.Direction == MessageDirection.Inbound)?.Text?.ToLowerInvariant() ?? "";
            
            // Nếu khách hỏi về bánh gato -> dừng AI tự động trả lời để nhân viên vào tư vấn trực tiếp
            if (lastInboundText.Contains("gato"))
            {
                logger.LogInformation("Tin nhắn chứa 'gato' — dừng AI tự động để nhân viên tư vấn.");
                return;
            }
            
            // Lấy AI reply
            var reply = await aiService.SuggestReplyAsync(conversation, messages, isAutoPilot: true);
            
            // Xử lý chốt đơn nếu có [ORDER_READY]
            if (reply.Contains("[ORDER_READY]"))
            {
                var idx = reply.IndexOf("[ORDER_READY]");
                var jsonStr = reply[(idx + 13)..].Trim();
                try
                {
                    var orderData = System.Text.Json.JsonDocument.Parse(jsonStr).RootElement;
                    var pickupTime = orderData.TryGetProperty("pickupTime", out var pt) ? pt.GetString() : "";
                    var userNote = orderData.TryGetProperty("note", out var un) ? un.GetString() : "";
                    var fullNote = "Đơn chốt tự động bởi AI";
                    if (!string.IsNullOrEmpty(pickupTime)) fullNote += $" | Giờ lấy: {pickupTime}";
                    if (!string.IsNullOrEmpty(userNote)) fullNote += $" | Ghi chú: {userNote}";

                    var custName = orderData.TryGetProperty("customerName", out var cn) ? cn.GetString() : "";
                    if (string.IsNullOrWhiteSpace(custName) || custName == "Tên")
                    {
                        custName = conversation.CustomerName;
                    }

                    var order = new Order
                    {
                        Title = custName,
                        CustomerPhone = orderData.TryGetProperty("phone", out var p) ? p.GetString() : "",
                        CustomerAddress = orderData.TryGetProperty("address", out var a) ? a.GetString() : "",
                        Note = fullNote,
                        Amount = orderData.TryGetProperty("totalPrice", out var t) && t.TryGetDecimal(out var amt) ? (long)amt : 0,
                        ConversationId = conversationId,
                        CreatedAt = DateTime.UtcNow
                    };

                    if (orderData.TryGetProperty("items", out var items) && items.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            order.Items.Add(new OrderItem
                            {
                                ProductName = item.TryGetProperty("name", out var n) ? n.GetString() ?? "Sản phẩm" : "Sản phẩm",
                                Quantity = item.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qty) ? qty : 1,
                                UnitPrice = item.TryGetProperty("unitPrice", out var u) && u.TryGetDecimal(out var up) ? (long)up : 0
                            });
                        }
                    }

                    db.Orders.Add(order);
                    
                    // Gắn tag hội thoại
                    if (string.IsNullOrEmpty(conversation.Tag) || !conversation.Tag.Contains("Chốt đơn"))
                        conversation.Tag = string.IsNullOrEmpty(conversation.Tag) ? "Chốt đơn" : conversation.Tag + ", Chốt đơn";
                        
                    await db.SaveChangesAsync();
                    events.NotifyChanged();
                    logger.LogInformation("AI AutoPilot đã chốt đơn thành công cho hội thoại {Id}", conversationId);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Lỗi khi parse JSON chốt đơn của AI");
                }
            }

            // Gửi tin nhắn trả lời
            if (!string.IsNullOrWhiteSpace(reply))
            {
                await SendReplyAsync(conversationId, reply);
            }
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Lỗi trong luồng AI AutoPilot cho hội thoại {Id}", conversationId);
        }
    }

    // Bổ sung avatar cho hội thoại cũ (tạo trước khi có tính năng, hoặc lần trước lấy hụt)
    public async Task EnsureProfileAsync(int conversationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conv = await db.Conversations.AsNoTracking().FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conv is null || !string.IsNullOrEmpty(conv.AvatarUrl))
            return;
        var adapter = adapters.First(a => a.Channel == conv.Channel);
        if (!adapter.IsConfigured)
            return;
        _ = FetchAndSaveProfileAsync(conv.Channel, conv.Id, conv.ExternalId);
    }

    private async Task FetchAndSaveProfileAsync(ChannelType channel, int conversationId, string externalId)
    {
        try
        {
            var adapter = adapters.First(a => a.Channel == channel);
            var profile = await adapter.FetchProfileAsync(externalId);
            if (profile is null || (string.IsNullOrEmpty(profile.Name) && string.IsNullOrEmpty(profile.AvatarUrl)))
                return;

            await using var db = await dbFactory.CreateDbContextAsync();
            var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
            if (conversation is null)
                return;
            if (!string.IsNullOrEmpty(profile.Name))
                conversation.CustomerName = profile.Name;
            if (!string.IsNullOrEmpty(profile.AvatarUrl))
                conversation.AvatarUrl = profile.AvatarUrl;
            await db.SaveChangesAsync();
            events.NotifyChanged();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Lấy profile khách {Channel}/{Id} thất bại", channel, externalId);
        }
    }

    // Gửi trả lời (văn bản hoặc ảnh): đẩy qua API của kênh tương ứng rồi lưu lại.
    // Kênh chưa cấu hình credentials → lưu ở trạng thái Simulated (chỉ hiện trong app).
    public async Task<Message> SendReplyAsync(int conversationId, string text, ReplyImage? image = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId);
        var adapter = adapters.First(a => a.Channel == conversation.Channel);

        var message = new Message
        {
            ConversationId = conversationId,
            Direction = MessageDirection.Outbound,
            Text = text,
            AttachmentUrl = image?.LocalUrl,
            SentAt = DateTime.UtcNow
        };

        if (!adapter.IsConfigured)
        {
            message.Status = MessageStatus.Simulated;
        }
        else
        {
            try
            {
                // Lưu message_id nền tảng cấp để lần đồng bộ sau không nhân đôi tin này
                message.ExternalMessageId = image is not null
                    ? await adapter.SendImageAsync(conversation, image.PublicUrl, image.Bytes, image.FileName)
                    : await adapter.SendTextAsync(conversation, text);
                message.Status = MessageStatus.Sent;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Gửi tin thất bại qua {Channel}", conversation.Channel);
                message.Status = MessageStatus.Failed;
                message.Error = ex.Message;
            }
        }

        db.Messages.Add(message);
        conversation.LastMessageAt = message.SentAt;
        conversation.LastMessagePreview = Truncate(image is not null && string.IsNullOrEmpty(text) ? "📷 Hình ảnh" : text, 80);
        await db.SaveChangesAsync();
        events.NotifyChanged();
        return message;
    }

    public async Task<Message> SendStickerAsync(int conversationId, string keyword)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations.FirstAsync(c => c.Id == conversationId);

        var message = new Message
        {
            ConversationId = conversationId,
            Direction = MessageDirection.Outbound,
            Text = $"😊 [Sticker Zalo: {keyword}]",
            SentAt = DateTime.UtcNow
        };

        if (conversation.Channel == ChannelType.ZaloPersonal)
        {
            var adapter = adapters.OfType<ZaloPersonalAdapter>().FirstOrDefault();
            if (adapter is not null && adapter.IsConfigured)
            {
                try
                {
                    await adapter.SendStickerAsync(conversation, keyword);
                    message.Status = MessageStatus.Sent;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Gửi sticker Zalo thất bại");
                    message.Status = MessageStatus.Failed;
                    message.Error = ex.Message;
                }
            }
        }

        db.Messages.Add(message);
        conversation.LastMessageAt = message.SentAt;
        conversation.LastMessagePreview = message.Text;
        await db.SaveChangesAsync();
        events.NotifyChanged();
        return message;
    }

    // ===== Lịch sử đặt hàng =====

    public async Task<List<Order>> GetOrdersAsync(int conversationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Orders.AsNoTracking()
            .Include(o => o.Items)
            .Where(o => o.ConversationId == conversationId)
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
    }

    public async Task AddOrderAsync(int conversationId, string title, long amount, string note, string? trelloCardUrl = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.Orders.Add(new Order
        {
            ConversationId = conversationId,
            Title = title,
            Amount = amount,
            Note = note,
            TrelloCardUrl = trelloCardUrl,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        events.NotifyChanged();
    }

    public async Task UpdateOrderAhamoveAsync(int orderId, string? ahamoveOrderId, string? trackingLink)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var order = await db.Orders.FindAsync(orderId);
        if (order is not null)
        {
            order.AhamoveOrderId = ahamoveOrderId;
            order.AhamoveTrackingLink = trackingLink;
            order.AhamoveStatus = string.IsNullOrEmpty(ahamoveOrderId) ? null : "ASSIGNING_DRIVER";
            await db.SaveChangesAsync();
            events.NotifyChanged();
        }
    }

    // Tạo đơn hàng đầy đủ với danh sách sản phẩm (form tạo đơn kiểu Pancake)
    public async Task<Order> CreateOrderAsync(Order order)
    {
        await using var db = await dbFactory.CreateDbContextAsync();

        // Tính tổng tiền từ items
        var itemsTotal = order.Items.Sum(i => (long)i.Quantity * i.UnitPrice);
        order.Amount = itemsTotal + order.ShippingFee - order.Discount;
        order.CreatedAt = DateTime.UtcNow;

        // Tạo title tự động nếu chưa có
        if (string.IsNullOrWhiteSpace(order.Title))
        {
            var conv = await db.Conversations.AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == order.ConversationId);
            order.Title = $"Đơn hàng — {conv?.CustomerName ?? "Khách"}";
        }

        db.Orders.Add(order);
        await db.SaveChangesAsync();
        events.NotifyChanged();
        return order;
    }

    // ===== Tìm kiếm khách hàng (autocomplete) =====
    public class CustomerInfo
    {
        public string Name { get; set; } = "";
        public string Phone { get; set; } = "";
        public string Address { get; set; } = "";
    }

    public async Task<List<CustomerInfo>> SearchCustomersAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 3) return [];

        await using var db = await dbFactory.CreateDbContextAsync();
        
        var matchingConvs = await db.Conversations.AsNoTracking()
            .Where(c => c.CustomerName.Contains(query))
            .Select(c => c.Id)
            .ToListAsync();

        var orders = await db.Orders.AsNoTracking()
            .Where(o => o.CustomerPhone.Contains(query) || matchingConvs.Contains(o.ConversationId))
            .OrderByDescending(o => o.CreatedAt)
            .ToListAsync();
            
        var conversationIds = orders.Select(o => o.ConversationId).Distinct().ToList();
        var convs = await db.Conversations.AsNoTracking()
            .Where(c => conversationIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => c.CustomerName);

        var result = new List<CustomerInfo>();
        var seenPhones = new HashSet<string>();

        foreach (var o in orders)
        {
            if (string.IsNullOrWhiteSpace(o.CustomerPhone)) continue;
            var phoneClean = o.CustomerPhone.Trim();
            if (seenPhones.Contains(phoneClean)) continue;

            seenPhones.Add(phoneClean);
            convs.TryGetValue(o.ConversationId, out var name);
            result.Add(new CustomerInfo
            {
                Name = name ?? "Khách hàng",
                Phone = phoneClean,
                Address = o.CustomerAddress
            });

            if (result.Count >= 10) break;
        }

        return result;
    }

    public async Task UpdateCustomerInfoAsync(int conversationId, string name, string phone, string address)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conv = await db.Conversations.FindAsync(conversationId);
        if (conv is not null)
        {
            conv.CustomerName = name;
            conv.CustomerPhone = phone;
            conv.CustomerAddress = address;
            await db.SaveChangesAsync();
            events.NotifyChanged();
        }
    }

    // ===== Quản lý sản phẩm =====

    public async Task<List<Product>> GetAllProductsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Products.AsNoTracking()
            .Where(p => p.IsActive)
            .OrderBy(p => p.Name)
            .ToListAsync();
    }

    public async Task<List<Product>> SearchProductsAsync(string keyword)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var kw = keyword.Trim().ToLower();
        return await db.Products.AsNoTracking()
            .Where(p => p.IsActive &&
                (p.Name.ToLower().Contains(kw) || p.Sku.ToLower().Contains(kw)))
            .OrderBy(p => p.Name)
            .Take(20)
            .ToListAsync();
    }

    public async Task<Product> AddProductAsync(string name, long price, string sku = "")
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var product = new Product
        {
            Name = name.Trim(),
            Price = price,
            Sku = sku.Trim(),
            IsActive = true
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product;
    }

    public async Task<List<Conversation>> GetConversationsAsync(ChannelType? channel = null, int? assignedStaffId = null, string? searchQuery = null)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var query = db.Conversations.AsNoTracking();
        if (channel is not null)
            query = query.Where(c => c.Channel == channel);
        if (assignedStaffId is not null)
            query = query.Where(c => c.AssignedStaffId == assignedStaffId);

        if (!string.IsNullOrWhiteSpace(searchQuery))
        {
            var q = searchQuery.Trim().ToLower();
            var matchingConvIds = await db.Messages.AsNoTracking()
                .Where(m => m.Text != null && m.Text.ToLower().Contains(q))
                .Select(m => m.ConversationId)
                .Distinct()
                .ToListAsync();

            query = query.Where(c =>
                (c.CustomerName != null && c.CustomerName.ToLower().Contains(q)) ||
                (c.CustomerPhone != null && c.CustomerPhone.Contains(q)) ||
                matchingConvIds.Contains(c.Id));
        }

        return await query.OrderByDescending(c => c.LastMessageAt).ToListAsync();
    }

    public async Task<List<Message>> GetMessagesAsync(int conversationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Messages.AsNoTracking()
            .Where(m => m.ConversationId == conversationId)
            .OrderBy(m => m.SentAt).ThenBy(m => m.Id)
            .ToListAsync();
    }

    public async Task MarkReadAsync(int conversationId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conversation is { UnreadCount: > 0 })
        {
            conversation.UnreadCount = 0;
            await db.SaveChangesAsync();
            events.NotifyChanged();
        }
    }

    // Nhập tin nhắn lịch sử lấy từ API đồng bộ: chống trùng theo mã tin gốc,
    // không tăng số chưa đọc (tin cũ coi như đã xem), cập nhật tên khách thật nếu có
    public async Task<(int Conversations, int Messages)> ImportHistoryAsync(
        ChannelType channel, IReadOnlyList<HistoryMessage> history)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var newMessages = 0;
        var touchedConversations = 0;

        foreach (var group in history.GroupBy(h => h.ExternalConversationId))
        {
            var conversation = await db.Conversations
                .FirstOrDefaultAsync(c => c.Channel == channel && c.ExternalId == group.Key);
            if (conversation is null)
            {
                conversation = new Conversation { Channel = channel, ExternalId = group.Key };
                db.Conversations.Add(conversation);
            }

            var realName = group.Select(h => h.CustomerName)
                .FirstOrDefault(n => !string.IsNullOrEmpty(n) && !n.Contains('…'));
            if (realName is not null)
                conversation.CustomerName = realName;
            else if (string.IsNullOrEmpty(conversation.CustomerName))
                conversation.CustomerName = $"{channel} …{group.Key[Math.Max(0, group.Key.Length - 4)..]}";

            // Nạp toàn bộ tin đã có của hội thoại để chống trùng (theo mã tin, hoặc theo nội dung+thời gian
            // cho tin cũ lưu không kèm mã — VD tin shop tự gửi qua app trước đây)
            var existing = conversation.Id == 0
                ? new List<Message>()
                : await db.Messages.Where(m => m.ConversationId == conversation.Id).ToListAsync();
            var existingIds = existing.Where(m => m.ExternalMessageId is not null)
                .Select(m => m.ExternalMessageId!).ToHashSet();

            foreach (var item in group.OrderBy(h => h.SentAt))
            {
                // Đã có theo mã tin gốc → bỏ qua
                if (item.ExternalMessageId is not null && existingIds.Contains(item.ExternalMessageId))
                    continue;

                // Chỉ đối chiếu với tin cũ CHƯA có mã (tin shop gửi qua app trước đây) — đó là trường hợp
                // duy nhất mã tin không giúp chống trùng. Khớp theo nội dung + chiều + thời gian ±2 phút,
                // rồi ghi bù mã tin vào bản cũ để lần sau khớp ngay. (Không đối chiếu tin đã có mã để
                // tránh gộp nhầm 2 tin giống hệt nhau khách gửi liền nhau — chúng có mã khác nhau.)
                var dup = existing.FirstOrDefault(m =>
                    m.ExternalMessageId is null &&
                    m.Direction == item.Direction &&
                    m.Text == item.Text &&
                    m.AttachmentUrl is null &&
                    Math.Abs((m.SentAt - item.SentAt).TotalSeconds) <= 120);
                if (dup is not null)
                {
                    if (item.ExternalMessageId is not null)
                    {
                        dup.ExternalMessageId = item.ExternalMessageId;
                        existingIds.Add(item.ExternalMessageId);
                    }
                    continue;
                }

                var added = new Message
                {
                    ConversationId = conversation.Id,
                    Direction = item.Direction,
                    Status = MessageStatus.Sent,
                    Text = item.Text,
                    ExternalMessageId = item.ExternalMessageId,
                    SentAt = item.SentAt
                };
                conversation.Messages.Add(added);
                existing.Add(added);
                if (item.ExternalMessageId is not null)
                    existingIds.Add(item.ExternalMessageId);
                newMessages++;

                if (item.SentAt >= conversation.LastMessageAt)
                {
                    conversation.LastMessageAt = item.SentAt;
                    conversation.LastMessagePreview = Truncate(item.Text, 80);
                }
            }
            touchedConversations++;
        }

        await db.SaveChangesAsync();
        events.NotifyChanged();
        return (touchedConversations, newMessages);
    }

    public async Task AssignAsync(int conversationId, int? staffId)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conversation is not null && conversation.AssignedStaffId != staffId)
        {
            conversation.AssignedStaffId = staffId;
            await db.SaveChangesAsync();
            events.NotifyChanged();
        }
    }

    public async Task SetTagAsync(int conversationId, string tag)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conversation is not null && conversation.Tag != tag)
        {
            conversation.Tag = tag;
            await db.SaveChangesAsync();
            events.NotifyChanged();
        }
    }

    public async Task ClearChannelDataAsync(ChannelType channel)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var convs = await db.Conversations
            .Where(c => c.Channel == channel)
            .ToListAsync();

        if (convs.Count == 0) return;

        var convIds = convs.Select(c => c.Id).ToList();

        // 1. Xóa tất cả tin nhắn
        var msgs = await db.Messages.Where(m => convIds.Contains(m.ConversationId)).ToListAsync();
        db.Messages.RemoveRange(msgs);

        // 2. Xóa các đơn hàng liên quan
        var orders = await db.Orders.Where(o => convIds.Contains(o.ConversationId)).ToListAsync();
        db.Orders.RemoveRange(orders);

        // 3. Xóa tất cả hội thoại của kênh
        db.Conversations.RemoveRange(convs);

        await db.SaveChangesAsync();
        events.NotifyChanged();
    }

    public bool IsChannelConfigured(ChannelType channel) =>
        adapters.First(a => a.Channel == channel).IsConfigured;

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";
}
