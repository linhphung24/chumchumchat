using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthropic;
using ChumChat.Web.Channels;
using ChumChat.Web.Data;
using Ant = Anthropic.Models.Messages;

namespace ChumChat.Web.Services;

// Gợi ý câu trả lời cho nhân viên bằng AI, dựa trên hội thoại + kho kiến thức
// (thông tin tiệm, câu hỏi thường gặp, ảnh tư liệu đọc bằng thị giác).
// Hỗ trợ nhiều nhà cung cấp: Claude (SDK), OpenAI/DeepSeek (chat completions), Gemini (generateContent).
public class AiSuggestionService(
    ChannelSettingsStore settings,
    AiKnowledgeService knowledge,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment env,
    ILogger<AiSuggestionService> logger)
{
    private const int MaxKnowledgeImages = 6;

    public bool IsConfigured => !string.IsNullOrEmpty(settings.Ai.ApiKey);

    // Một ảnh tư liệu đã đọc sẵn: base64 + kiểu MIME
    private record LoadedImage(string Base64, string MediaType, string Caption);

    public async Task<string> SuggestReplyAsync(Conversation conversation, IReadOnlyList<Message> messages, CancellationToken ct = default)
    {
        var opts = settings.Ai;
        if (string.IsNullOrEmpty(opts.ApiKey))
            throw new InvalidOperationException("Chưa cấu hình AI — vào tab Trợ lý AI điền API key");

        var system = await BuildSystemPromptAsync(opts);
        var userText = BuildUserText(conversation, messages);
        var images = await LoadImagesAsync();

        var provider = opts.Provider?.ToLowerInvariant() ?? "anthropic";
        var suggestion = provider switch
        {
            "openai" => await OpenAiCompatibleAsync("https://api.openai.com/v1/chat/completions", opts, system, userText, images, includeImages: true, ct),
            "deepseek" => await OpenAiCompatibleAsync("https://api.deepseek.com/chat/completions", opts, system, userText, images, includeImages: false, ct),
            "gemini" => await GeminiAsync(opts, system, userText, images, ct),
            _ => await AnthropicAsync(opts, system, userText, images, ct),
        };

        suggestion = suggestion.Trim();
        if (string.IsNullOrEmpty(suggestion))
            throw new InvalidOperationException("AI không trả về gợi ý (có thể do nội dung bị từ chối)");
        return suggestion;
    }

    // ===== Ngữ cảnh dùng chung cho mọi nhà cung cấp =====

    private async Task<string> BuildSystemPromptAsync(AiOptions opts)
    {
        var system = new StringBuilder(
            "Bạn là trợ lý bán hàng, soạn giúp nhân viên chăm sóc khách hàng một câu trả lời bằng tiếng Việt. " +
            "Trả lời lịch sự, thân thiện, ngắn gọn, đi thẳng vào nhu cầu của khách. " +
            "Ưu tiên dùng thông tin tiệm, câu hỏi thường gặp và ảnh tư liệu bên dưới để trả lời chính xác. " +
            "Nếu không chắc chắn thông tin, đừng bịa — gợi ý nhân viên hỏi thêm. " +
            "Chỉ trả về đúng nội dung tin nhắn để gửi cho khách, không thêm giải thích hay dấu ngoặc kép.");

        if (!string.IsNullOrWhiteSpace(opts.ShopContext))
            system.Append("\n\n### Thông tin tiệm:\n").Append(opts.ShopContext.Trim());

        var faqs = await knowledge.GetFaqsAsync();
        if (faqs.Count > 0)
        {
            system.Append("\n\n### Câu hỏi thường gặp:");
            foreach (var f in faqs)
                system.Append($"\n- Hỏi: {f.Question}\n  Đáp: {f.Answer}");
        }
        return system.ToString();
    }

    private static string BuildUserText(Conversation conversation, IReadOnlyList<Message> messages)
    {
        var transcript = new StringBuilder();
        foreach (var msg in messages.TakeLast(15))
        {
            var who = msg.Direction == MessageDirection.Inbound ? "Khách" : "Nhân viên";
            var text = string.IsNullOrEmpty(msg.Text) && msg.AttachmentUrl is not null ? "[gửi một hình ảnh]" : msg.Text;
            transcript.AppendLine($"{who}: {text}");
        }
        return $"Đây là hội thoại với khách qua kênh {conversation.Channel}:\n\n{transcript}\n\n" +
               "Soạn giúp tôi câu trả lời tiếp theo để gửi cho khách.";
    }

    private async Task<List<LoadedImage>> LoadImagesAsync()
    {
        var result = new List<LoadedImage>();
        foreach (var img in (await knowledge.GetImagesAsync()).Take(MaxKnowledgeImages))
        {
            try
            {
                var relative = img.Url.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
                var path = Path.Combine(env.WebRootPath, relative);
                if (!File.Exists(path))
                    continue;
                var mediaType = Path.GetExtension(path).ToLowerInvariant() switch
                {
                    ".png" => "image/png",
                    ".gif" => "image/gif",
                    ".webp" => "image/webp",
                    _ => "image/jpeg"
                };
                result.Add(new LoadedImage(Convert.ToBase64String(await File.ReadAllBytesAsync(path)), mediaType, img.Caption));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Không đọc được ảnh tư liệu {Url}", img.Url);
            }
        }
        return result;
    }

    // ===== Claude / Anthropic (SDK) =====

    private async Task<string> AnthropicAsync(AiOptions opts, string system, string userText, List<LoadedImage> images, CancellationToken ct)
    {
        AnthropicClient client = new() { ApiKey = opts.ApiKey };

        var content = new List<Ant.ContentBlockParam> { new Ant.TextBlockParam { Text = userText } };
        if (images.Count > 0)
        {
            content.Add(new Ant.TextBlockParam { Text = "\nCác ảnh tư liệu của tiệm để tham khảo:" });
            foreach (var img in images)
            {
                if (!string.IsNullOrWhiteSpace(img.Caption))
                    content.Add(new Ant.TextBlockParam { Text = $"Ảnh — {img.Caption}:" });
                content.Add(new Ant.ImageBlockParam
                {
                    Source = new Ant.Base64ImageSource
                    {
                        MediaType = img.MediaType switch
                        {
                            "image/png" => Ant.MediaType.ImagePng,
                            "image/gif" => Ant.MediaType.ImageGif,
                            "image/webp" => Ant.MediaType.ImageWebP,
                            _ => Ant.MediaType.ImageJpeg
                        },
                        Data = img.Base64
                    }
                });
            }
        }

        var response = await client.Messages.Create(new Ant.MessageCreateParams
        {
            Model = opts.Model,
            MaxTokens = 600,
            System = system,
            Messages = [new() { Role = Ant.Role.User, Content = content }]
        }, ct);

        return string.Join("", response.Content.Select(b => b.Value).OfType<Ant.TextBlock>().Select(t => t.Text));
    }

    // ===== OpenAI (ChatGPT) & DeepSeek — cùng định dạng chat completions =====

    private async Task<string> OpenAiCompatibleAsync(string url, AiOptions opts, string system, string userText,
        List<LoadedImage> images, bool includeImages, CancellationToken ct)
    {
        object userContent;
        if (includeImages && images.Count > 0)
        {
            var parts = new List<object> { new { type = "text", text = userText } };
            foreach (var img in images)
            {
                if (!string.IsNullOrWhiteSpace(img.Caption))
                    parts.Add(new { type = "text", text = $"Ảnh — {img.Caption}:" });
                parts.Add(new { type = "image_url", image_url = new { url = $"data:{img.MediaType};base64,{img.Base64}" } });
            }
            userContent = parts;
        }
        else
        {
            userContent = userText;
        }

        var payload = new
        {
            model = opts.Model,
            max_tokens = 600,
            messages = new object[]
            {
                new { role = "system", content = system },
                new { role = "user", content = userContent }
            }
        };

        var client = httpClientFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", opts.ApiKey);

        var response = await client.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"AI lỗi {(int)response.StatusCode}: {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    // ===== Google Gemini =====

    private async Task<string> GeminiAsync(AiOptions opts, string system, string userText, List<LoadedImage> images, CancellationToken ct)
    {
        var parts = new List<object> { new { text = userText } };
        foreach (var img in images)
        {
            if (!string.IsNullOrWhiteSpace(img.Caption))
                parts.Add(new { text = $"Ảnh — {img.Caption}:" });
            parts.Add(new { inline_data = new { mime_type = img.MediaType, data = img.Base64 } });
        }

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = system } } },
            contents = new object[] { new { role = "user", parts } },
            generationConfig = new { maxOutputTokens = 600 }
        };

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{opts.Model}:generateContent?key={Uri.EscapeDataString(opts.ApiKey)}";
        var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync(url,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"), ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gemini lỗi {(int)response.StatusCode}: {Trim(body)}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
            throw new InvalidOperationException($"Gemini không trả về gợi ý: {Trim(body)}");
        var textParts = candidates[0].GetProperty("content").GetProperty("parts").EnumerateArray()
            .Where(p => p.TryGetProperty("text", out _))
            .Select(p => p.GetProperty("text").GetString());
        return string.Join("", textParts);
    }

    private static string Trim(string s) => s.Length <= 300 ? s : s[..300];
}
