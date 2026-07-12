using System.Text;
using System.Text.Json;
using ChumChat.Web.Data;

namespace ChumChat.Web.Services;

// Tạo thẻ Trello từ một hội thoại (dùng khi chốt đơn xong)
public class TrelloService(
    ChannelSettingsStore settings,
    IHttpClientFactory httpClientFactory,
    ILogger<TrelloService> logger)
{
    public bool IsConfigured =>
        !string.IsNullOrEmpty(settings.Trello.ApiKey) &&
        !string.IsNullOrEmpty(settings.Trello.Token) &&
        !string.IsNullOrEmpty(settings.Trello.ListId);

    // Dựng sẵn nội dung thẻ để hiện popup xem trước (người dùng sửa được trước khi tạo)
    public (string Name, string Desc) BuildCard(Conversation conversation, IReadOnlyList<Message> messages)
    {
        var name = $"🧾 {conversation.CustomerName} — đơn qua {ChannelLabel(conversation.Channel)}";

        var desc = new StringBuilder();
        desc.AppendLine($"**Khách hàng:** {conversation.CustomerName}");
        desc.AppendLine($"**Kênh:** {ChannelLabel(conversation.Channel)}");
        desc.AppendLine($"**Thời điểm chốt:** {DateTime.Now:HH:mm dd/MM/yyyy}");
        desc.AppendLine();
        desc.AppendLine("---");
        desc.AppendLine("**Hội thoại gần nhất:**");
        foreach (var msg in messages.TakeLast(15))
        {
            var who = msg.Direction == MessageDirection.Inbound ? "Khách" : "Shop";
            var text = string.IsNullOrEmpty(msg.Text) && msg.AttachmentUrl is not null ? "📷 [Hình ảnh]" : msg.Text;
            desc.AppendLine($"- **{who}** ({msg.SentAt.ToLocalTime():HH:mm dd/MM}): {text}");
        }
        return (name, desc.ToString());
    }

    // Trả về URL của thẻ vừa tạo
    public async Task<string> CreateCardAsync(string name, string desc)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Chưa cấu hình Trello — vào trang Cấu hình điền API Key, Token và List ID");

        var o = settings.Trello;
        var client = httpClientFactory.CreateClient();
        var response = await client.PostAsync("https://api.trello.com/1/cards", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["key"] = o.ApiKey,
                ["token"] = o.Token,
                ["idList"] = o.ListId,
                ["name"] = name,
                ["desc"] = desc,
                ["pos"] = "top"
            }));

        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("Trello API lỗi {Status}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException($"Trello từ chối ({(int)response.StatusCode}): {body}");
        }

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.TryGetProperty("shortUrl", out var url)
            ? url.GetString() ?? ""
            : doc.RootElement.GetProperty("url").GetString() ?? "";
    }

    private static string ChannelLabel(ChannelType channel) => channel switch
    {
        ChannelType.Zalo => "Zalo",
        ChannelType.Messenger => "Messenger",
        ChannelType.Shopee => "Shopee",
        ChannelType.TikTokShop => "TikTok Shop",
        _ => channel.ToString()
    };
}
