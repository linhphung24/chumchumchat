using System.Text;
using System.Text.Json;
using ChumChat.Web.Channels;
using ChumChat.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace ChumChat.Web.Services;

// Trả lời tự động theo kịch bản: khách nhắn trúng từ khóa/ý định → bot tự gửi file + tin kèm.
public class AutoReplyService(
    IDbContextFactory<AppDbContext> dbFactory,
    IEnumerable<IChannelAdapter> adapters,
    ChannelSettingsStore settings,
    InboxEvents events,
    IWebHostEnvironment env,
    IHttpClientFactory httpClientFactory,
    ILogger<AutoReplyService> logger)
{
    // Không gửi lại cùng file cho một khách trong vòng 30 phút (tránh spam khi khách nhắn nhiều)
    private static readonly TimeSpan Cooldown = TimeSpan.FromMinutes(30);

    // ===== CRUD =====

    public async Task<List<AutoReplyRule>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AutoReplyRules.AsNoTracking()
            .OrderBy(r => r.SortOrder).ThenBy(r => r.Id).ToListAsync();
    }

    public async Task SaveAsync(AutoReplyRule rule)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        if (rule.Id == 0)
        {
            rule.SortOrder = (await db.AutoReplyRules.Select(r => (int?)r.SortOrder).MaxAsync() ?? 0) + 1;
            db.AutoReplyRules.Add(rule);
        }
        else
        {
            db.AutoReplyRules.Update(rule);
        }
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.AutoReplyRules.FirstOrDefaultAsync(r => r.Id == id);
        if (item is not null)
        {
            db.AutoReplyRules.Remove(item);
            await db.SaveChangesAsync();
        }
    }

    public async Task SetEnabledAsync(int id, bool enabled)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.AutoReplyRules.FirstOrDefaultAsync(r => r.Id == id);
        if (item is not null)
        {
            item.Enabled = enabled;
            await db.SaveChangesAsync();
        }
    }

    // ===== Xử lý tin khách đến: tìm kịch bản khớp và tự gửi =====

    // baseUrl: địa chỉ công khai của app (để nền tảng tải file). Gọi từ WebhooksController.
    public async Task TryAutoReplyAsync(int conversationId, string customerText, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(customerText))
            return;

        var rules = (await GetAllAsync()).Where(r => r.Enabled && !string.IsNullOrEmpty(r.FileUrl)).ToList();
        if (rules.Count == 0)
            return;

        var matched = await MatchRuleAsync(rules, customerText);
        if (matched is null)
            return;

        await using var db = await dbFactory.CreateDbContextAsync();
        var conversation = await db.Conversations.FirstOrDefaultAsync(c => c.Id == conversationId);
        if (conversation is null)
            return;

        // Chống spam: đã gửi file này cho khách gần đây thì thôi
        var since = DateTime.UtcNow - Cooldown;
        var recentlySent = await db.Messages.AnyAsync(m =>
            m.ConversationId == conversationId &&
            m.Direction == MessageDirection.Outbound &&
            m.AttachmentUrl == matched.FileUrl &&
            m.SentAt >= since);
        if (recentlySent)
            return;

        var adapter = adapters.First(a => a.Channel == conversation.Channel);
        if (!adapter.IsConfigured)
            return;

        try
        {
            // Gửi tin kèm (nếu có)
            if (!string.IsNullOrWhiteSpace(matched.ReplyText))
            {
                var textId = await adapter.SendTextAsync(conversation, matched.ReplyText);
                await SaveOutboundAsync(db, conversationId, matched.ReplyText, null, textId);
            }

            // Gửi file: ảnh thì gửi dạng ảnh, còn lại gửi dạng file
            var relative = matched.FileUrl.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var path = Path.Combine(env.WebRootPath, relative);
            var bytes = await File.ReadAllBytesAsync(path);
            var publicUrl = baseUrl.TrimEnd('/') + matched.FileUrl;

            string? fileMsgId;
            if (matched.FileMime.StartsWith("image/"))
                fileMsgId = await adapter.SendImageAsync(conversation, publicUrl, bytes, matched.FileName);
            else
                fileMsgId = await adapter.SendFileAsync(conversation, publicUrl, bytes, matched.FileName, matched.FileMime);

            await SaveOutboundAsync(db, conversationId, $"📎 {matched.FileName}", matched.FileUrl, fileMsgId);

            logger.LogInformation("Auto-reply: đã gửi '{Rule}' cho hội thoại {Conv}", matched.Name, conversationId);
            events.NotifyChanged();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Auto-reply: gửi kịch bản '{Rule}' thất bại", matched.Name);
        }
    }

    private static async Task SaveOutboundAsync(AppDbContext db, int conversationId, string text, string? attachmentUrl, string? externalId)
    {
        db.Messages.Add(new Message
        {
            ConversationId = conversationId,
            Direction = MessageDirection.Outbound,
            Status = MessageStatus.Sent,
            Text = text,
            AttachmentUrl = attachmentUrl,
            ExternalMessageId = externalId,
            SentAt = DateTime.UtcNow
        });
        var conv = await db.Conversations.FirstAsync(c => c.Id == conversationId);
        conv.LastMessageAt = DateTime.UtcNow;
        conv.LastMessagePreview = text.Length <= 80 ? text : text[..80] + "…";
        await db.SaveChangesAsync();
    }

    // Khớp kịch bản: ưu tiên từ khóa (nhanh, miễn phí); nếu không trúng và có AI + mô tả ý định thì hỏi AI.
    private async Task<AutoReplyRule?> MatchRuleAsync(List<AutoReplyRule> rules, string text)
    {
        // Bỏ dấu tiếng Việt để khớp cả khi khách gõ không dấu ("gia nhan" khớp "giá nhân")
        var norm = RemoveDiacritics(text);
        foreach (var rule in rules)
        {
            var keywords = rule.Keywords
                .Split([',', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(RemoveDiacritics);
            if (keywords.Any(k => k.Length > 0 && norm.Contains(k)))
                return rule;
        }

        // AI hiểu ý (chỉ khi có key + có kịch bản mô tả ý định)
        var withDesc = rules.Where(r => !string.IsNullOrWhiteSpace(r.MatchDescription)).ToList();
        if (withDesc.Count == 0 || string.IsNullOrEmpty(settings.Ai.ApiKey))
            return null;

        try
        {
            var idx = await ClassifyWithAiAsync(withDesc, text);
            return idx is int i && i >= 1 && i <= withDesc.Count ? withDesc[i - 1] : null;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Auto-reply: phân loại AI lỗi, bỏ qua");
            return null;
        }
    }

    // Bỏ dấu tiếng Việt + về chữ thường để so khớp không phân biệt dấu
    private static string RemoveDiacritics(string s)
    {
        s = s.ToLowerInvariant().Replace('đ', 'd');
        var normalized = s.Normalize(System.Text.NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC).Trim();
    }

    // Gọi AI trả về số kịch bản khớp (1..N) hoặc 0. Dùng đúng nhà cung cấp đang chọn (chỉ cần text).
    private async Task<int?> ClassifyWithAiAsync(List<AutoReplyRule> rules, string text)
    {
        var sb = new StringBuilder("Khách nhắn: \"").Append(text).Append("\"\n\nCác kịch bản:\n");
        for (var i = 0; i < rules.Count; i++)
            sb.Append(i + 1).Append(". ").Append(rules[i].MatchDescription).Append('\n');
        sb.Append("\nTin của khách khớp kịch bản số mấy? Chỉ trả về đúng MỘT con số (0 nếu không khớp kịch bản nào).");
        var prompt = sb.ToString();

        var opts = settings.Ai;
        var provider = opts.Provider?.ToLowerInvariant() ?? "anthropic";
        string reply = provider switch
        {
            "gemini" => await GeminiTextAsync(opts, prompt),
            _ => await OpenAiOrClaudeTextAsync(provider, opts, prompt),
        };

        var digits = new string(reply.Where(char.IsDigit).ToArray());
        return int.TryParse(digits.Length > 0 ? digits[..1] : "", out var n) ? n : null;
    }

    // Claude/OpenAI/DeepSeek đều nhận qua chat completions (Anthropic có endpoint riêng nhưng để đơn giản
    // và vì chỉ cần một con số, ta gọi Anthropic bằng SDK ở AiSuggestionService — ở đây dùng HTTP cho openai/deepseek,
    // còn Anthropic thì gọi messages API qua HTTP luôn cho gọn).
    private async Task<string> OpenAiOrClaudeTextAsync(string provider, AiOptions opts, string prompt)
    {
        var client = httpClientFactory.CreateClient();
        if (provider == "anthropic")
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", opts.ApiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(JsonSerializer.Serialize(new
            {
                model = opts.Model,
                max_tokens = 8,
                messages = new[] { new { role = "user", content = prompt } }
            }), Encoding.UTF8, "application/json");
            var body = await (await client.SendAsync(req)).Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("content", out var c) && c.GetArrayLength() > 0
                ? c[0].GetProperty("text").GetString() ?? "" : "";
        }

        var url = provider == "deepseek" ? "https://api.deepseek.com/chat/completions" : "https://api.openai.com/v1/chat/completions";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", opts.ApiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model = opts.Model,
            max_tokens = 8,
            messages = new[] { new { role = "user", content = prompt } }
        }), Encoding.UTF8, "application/json");
        var respBody = await (await client.SendAsync(request)).Content.ReadAsStringAsync();
        using var d = JsonDocument.Parse(respBody);
        return d.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    private async Task<string> GeminiTextAsync(AiOptions opts, string prompt)
    {
        var client = httpClientFactory.CreateClient();
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{opts.Model}:generateContent?key={Uri.EscapeDataString(opts.ApiKey)}";
        var body = await (await client.PostAsync(url, new StringContent(JsonSerializer.Serialize(new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = prompt } } } },
            generationConfig = new { maxOutputTokens = 8 }
        }), Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out var cands) || cands.GetArrayLength() == 0)
            return "";
        return string.Join("", cands[0].GetProperty("content").GetProperty("parts").EnumerateArray()
            .Where(p => p.TryGetProperty("text", out _)).Select(p => p.GetProperty("text").GetString()));
    }
}
