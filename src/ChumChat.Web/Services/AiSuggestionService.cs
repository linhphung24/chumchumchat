using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Anthropic;
using ChumChat.Web.Channels;
using ChumChat.Web.Data;
using Microsoft.EntityFrameworkCore;
using Ant = Anthropic.Models.Messages;

namespace ChumChat.Web.Services;

// Gợi ý câu trả lời cho nhân viên bằng AI, dựa trên hội thoại + kho kiến thức
// (thông tin tiệm, câu hỏi thường gặp, ảnh tư liệu đọc bằng thị giác).
// Hỗ trợ nhiều nhà cung cấp: Claude (SDK), OpenAI/DeepSeek (chat completions), Gemini (generateContent).
public class AiSuggestionService(
    ChannelSettingsStore settings,
    AiKnowledgeService knowledge,
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    IWebHostEnvironment env,
    ILogger<AiSuggestionService> logger)
{
    private const int MaxKnowledgeImages = 6;

    public bool IsConfigured => !string.IsNullOrEmpty(settings.Ai.ApiKey);

    // Một ảnh tư liệu đã đọc sẵn: base64 + kiểu MIME
    private record LoadedImage(string Base64, string MediaType, string Caption);

    public async Task<string> SuggestReplyAsync(Conversation conversation, IReadOnlyList<Message> messages, bool isAutoPilot = false, CancellationToken ct = default)
    {
        var opts = settings.Ai;
        if (string.IsNullOrEmpty(opts.ApiKey))
            throw new InvalidOperationException("Chưa cấu hình AI — vào tab Trợ lý AI điền API key");

        var system = await BuildSystemPromptAsync(opts, isAutoPilot);
        var userText = BuildUserText(conversation, messages, isAutoPilot);
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

        if (!isAutoPilot && suggestion.Contains("[ORDER_READY]"))
        {
            var idx = suggestion.IndexOf("[ORDER_READY]");
            suggestion = suggestion[..idx].Trim();
        }
        return suggestion;
    }

    // ===== Ngữ cảnh dùng chung cho mọi nhà cung cấp =====

    private async Task<string> BuildSystemPromptAsync(AiOptions opts, bool isAutoPilot)
    {
        var system = new StringBuilder(
            "Bạn là nhân viên bán hàng của tiệm ChumChum Bakery.\n" +
            "QUY TẮC BẮT BUỘC KHÔNG NGOẠI LỆ:\n" +
            "1. MỖI LẦN KHÁCH NHẮN NÓI MUA HÀNG LÀ TẠO MỘT ĐƠN HÀNG MỚI ĐỘC LẬP HOÀN TOÀN!\n" +
            "2. TUYỆT ĐỐI KHÔNG HỎI HOẶC NHẮC VỀ ĐƠN HÀNG CŨ (Không hỏi 'có muốn gộp đơn cũ không', không nhắc 'đơn đã thanh toán trước đó', không nhắc 'Đơn cũ (...)').\n" +
            "3. TUYỆT ĐỐI KHÔNG NÓI HAY NHẮC VỀ BÁNH GATO nếu tin nhắn mới nhất của khách không chủ động hỏi về bánh gato!\n" +
            "4. Hãy luôn kiểm tra 'Danh sách thực đơn & Bảng giá chính thức (iPOS)' ở bên dưới để tư vấn món cho khách.\n" +
            "- Nếu món khách hỏi CÓ trong iPOS: Báo có và tư vấn giá bán / mô tả sản phẩm cho khách theo đúng danh sách.\n" +
            "- Nếu món khách hỏi KHÔNG CÓ trong iPOS: Báo tiệm hiện tại chưa có món đó một cách lịch sự.\n" +
            "5. QUY TẮC LẬP ĐƠN HÀNG MỚI: CHỈ TÍNH VÀ TỔNG HỢP CÁC MÓN MÀ KHÁCH TRỰC TIẾP ĐẶT TRONG TIN NHẮN MỚI NÀY! TUYỆT ĐỐI KHÔNG TỰ CỘNG THÊM CÁC ĐƠN CŨ HOẶC MÓN TRONG LỊCH SỬ THỬ NGHIỆM VÀO ĐƠN MỚI.\n" +
            "6. Trả lời lịch sự, thân thiện, ngắn gọn, đi thẳng vào nhu cầu của khách.\n");

        if (isAutoPilot)
        {
            system.Append(
                "QUY TẮC PHẢN HỒI TỰ ĐỘNG CHỐT ĐƠN:\n" +
                "1. Khi khách hỏi BÁNH GATO: Dừng tự động trả lời để nhân viên tư vấn trực tiếp.\n" +
                "2. Nếu khách CHỈ HỎI THÔNG TIN HOẶC HỎI MÓN CÓ/KHÔNG CÓ: Trả lời đúng câu hỏi dựa trên thực đơn iPOS. KHÔNG lặp lại đơn cũ đã giao/thanh toán, KHÔNG tự ghép đơn hàng cũ, KHÔNG xin thông tin khách khi khách chưa yêu cầu mua hàng.\n" +
                "3. CHỈ KHI KHÁCH BẮT ĐẦU CHỌN MÓN VÀ MUỐN ĐẶT HÀNG: Khéo léo xin khách các thông tin theo đúng danh sách 5 mục sau:\n" +
                "   1. **Họ và tên**\n" +
                "   2. **Số điện thoại**\n" +
                "   3. **Địa chỉ** (nếu cần ship)\n" +
                "   4. **Thời gian cần lấy**\n" +
                "   5. **Ghi chú thêm**\n" +
                "4. Khi đã có ĐỦ thông tin và khách ĐỒNG Ý chốt đơn, bạn PHẢI in ra đoạn JSON ở cuối câu trả lời (bắt đầu bằng [ORDER_READY]):\n" +
                "[ORDER_READY]\n" +
                "{\"customerName\": \"Tên\", \"phone\": \"SĐT\", \"address\": \"Địa chỉ\", \"pickupTime\": \"Thời gian lấy\", \"note\": \"Ghi chú\", \"items\": [{\"name\": \"Tên bánh\", \"quantity\": 1, \"unitPrice\": 50000}], \"totalPrice\": 50000}\n" +
                "5. Khi khách nói đã chuyển khoản / thanh toán xong (hoặc gửi ảnh biên lai), bạn hãy xác nhận và sinh JSON trên để hệ thống ghi nhận.\n");
        }
        else
        {
            system.Append("Chỉ trả về đúng nội dung tin nhắn để gửi cho khách, không thêm giải thích hay dấu ngoặc kép.");
        }

        if (!string.IsNullOrWhiteSpace(opts.ShopContext))
        {
            try
            {
                using var doc = JsonDocument.Parse(opts.ShopContext);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("shopInfo", out var info) && !string.IsNullOrWhiteSpace(info.GetString()))
                        system.Append("\n\n### Thông tin tiệm:\n").Append(info.GetString()!.Trim());
                    if (root.TryGetProperty("deliveryProcess", out var dev) && !string.IsNullOrWhiteSpace(dev.GetString()))
                        system.Append("\n\n### Quy trình giao/nhận hàng:\n").Append(dev.GetString()!.Trim());
                    if (root.TryGetProperty("aiTone", out var tone) && !string.IsNullOrWhiteSpace(tone.GetString()))
                        system.Append("\n\n### Giọng điệu tư vấn:\n").Append(tone.GetString()!.Trim());
                    if (root.TryGetProperty("systemRules", out var sRules) && !string.IsNullOrWhiteSpace(sRules.GetString()))
                        system.Append("\n\n### QUY TẮC & CHỈ ĐẠO RIÊNG DÀNH CHO AI:\n").Append(sRules.GetString()!.Trim());
                }
                else
                {
                    system.Append("\n\n### Thông tin tiệm:\n").Append(opts.ShopContext.Trim());
                }
            }
            catch
            {
                system.Append("\n\n### Thông tin tiệm:\n").Append(opts.ShopContext.Trim());
            }
        }

        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var products = await db.Products.AsNoTracking()
                .Where(p => p.IsActive)
                .ToListAsync();

            if (products.Count > 0)
            {
                system.Append("\n\n### Danh sách thực đơn & Bảng giá & Tồn kho chính thức (Đồng bộ từ iPOS):\n");
                system.Append("QUY TẮC BÁO TỒN KHO: Kiểm tra kỹ số lượng tồn kho bên dưới. Nếu món nào ghi 'HẾT HÀNG' (hoặc số lượng tồn = 0), AI PHẢI BÁO NGAY LÀ MÓN ĐÓ TẠM HẾT VÀ GỢI Ý CÁC MÓN KHÁC CÒN HÀNG!\n");

                foreach (var p in products)
                {
                    var stockStatus = p.StockQuantity <= 0 ? " [🔴 HẾT HÀNG / TẠM HẾT MÓN]" : $" [Còn {p.StockQuantity} phần]";
                    system.Append($"- {p.Name}: {p.Price:N0}đ{stockStatus}");
                    if (!string.IsNullOrWhiteSpace(p.Description))
                    {
                        system.Append($" ({p.Description})");
                    }
                    system.Append("\n");
                }
            }
        }

        var faqs = await knowledge.GetFaqsAsync();
        if (faqs.Count > 0)
        {
            system.Append("\n\n### Câu hỏi thường gặp:");
            foreach (var f in faqs)
                system.Append($"\n- Hỏi: {f.Question}\n  Đáp: {f.Answer}");
        }
        return system.ToString();
    }

    private string BuildUserText(Conversation conversation, IReadOnlyList<Message> messages, bool isAutoPilot)
    {
        var transcript = new StringBuilder();
        
        // Lấy 6 tin nhắn mới nhất
        var recent = messages.TakeLast(6).ToList();
        var lastInbound = recent.LastOrDefault(m => m.Direction == MessageDirection.Inbound)?.Text?.ToLowerInvariant() ?? "";
        bool customerAskedGatoNow = lastInbound.Contains("gato");

        foreach (var msg in recent)
        {
            var who = msg.Direction == MessageDirection.Inbound ? "Khách" : "Nhân viên";
            var text = string.IsNullOrEmpty(msg.Text) && msg.AttachmentUrl is not null ? "[gửi một hình ảnh]" : msg.Text;

            // Nếu khách hiện tại không hỏi về bánh gato, loại bỏ các câu tin nhắn tự động cũ chứa từ 'gato' hoặc 'đơn cũ' để không làm bẩn context AI
            if (msg.Direction == MessageDirection.Outbound && !customerAskedGatoNow)
            {
                if (text.Contains("bánh gato") || text.Contains("gato") || text.Contains("Đơn cũ") || text.Contains("thanh toán trước đó") || text.Contains("đặt kèm vào đơn"))
                {
                    continue;
                }
            }

            transcript.AppendLine($"{who}: {text}");
        }

        var bankInfo = "";
        if (isAutoPilot && !string.IsNullOrEmpty(settings.Ai.BankName) && !string.IsNullOrEmpty(settings.Ai.BankAccount))
        {
            bankInfo = $"\n\n[HƯỚNG DẪN TẠO MÃ QR THANH TOÁN]:\n" +
                       $"Nếu khách có nhu cầu chuyển khoản trước, bạn gửi link ảnh QR sau cho khách:\n" +
                       $"https://img.vietqr.io/image/{settings.Ai.BankName}-{settings.Ai.BankAccount}-compact2.jpg?amount={{TỔNG_TIỀN}}&accountName={Uri.EscapeDataString(settings.Ai.BankAccountName)}\n" +
                       $"Thay {{TỔNG_TIỀN}} bằng tổng tiền đơn (viết liền không phẩy). Khách có thể chuyển khoản trước hoặc thanh toán sau khi nhận hàng đều được, không ép khách phải thanh toán trước mới chốt đơn.";
        }

        return $"Đây là hội thoại với khách qua kênh {conversation.Channel}:\n\n{transcript}{bankInfo}\n\n" +
               "Soạn giúp tôi câu trả lời tiếp theo để gửi cho khách (Chú ý: Mỗi lần nhắn mua là tạo một đơn mới hoàn toàn, tuyệt đối không nhắc đơn cũ hay bánh gato nếu khách không hỏi).";
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
