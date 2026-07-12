using System.Text.Json;
using ChumChat.Web.Channels;
using ChumChat.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace ChumChat.Web.Services;

// Nguồn cấu hình kênh duy nhất của app: đọc/ghi database, cache trong RAM.
// appsettings.json (section Channels) chỉ dùng seed lần chạy đầu tiên.
public class ChannelSettingsStore(
    IDbContextFactory<AppDbContext> dbFactory,
    IConfiguration configuration,
    ILogger<ChannelSettingsStore> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private ZaloOptions zalo = new();
    private MessengerOptions messenger = new();
    private ShopeeOptions shopee = new();
    private TikTokShopOptions tikTok = new();
    private ZaloPersonalOptions zaloPersonal = new();
    private TrelloOptions trello = new();
    private AiOptions ai = new();

    public event Action? Changed;

    public ZaloOptions Zalo => zalo;
    public MessengerOptions Messenger => messenger;
    public ShopeeOptions Shopee => shopee;
    public TikTokShopOptions TikTokShop => tikTok;
    public ZaloPersonalOptions ZaloPersonal => zaloPersonal;
    public TrelloOptions Trello => trello;
    public AiOptions Ai => ai;

    // Gọi một lần lúc khởi động, sau khi database sẵn sàng
    public async Task InitializeAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var rows = await db.ChannelConnections.AsNoTracking().ToListAsync();

        zalo = Load<ZaloOptions>(rows, ChannelType.Zalo, "Zalo");
        messenger = Load<MessengerOptions>(rows, ChannelType.Messenger, "Messenger");
        shopee = Load<ShopeeOptions>(rows, ChannelType.Shopee, "Shopee");
        tikTok = Load<TikTokShopOptions>(rows, ChannelType.TikTokShop, "TikTokShop");
        zaloPersonal = Load<ZaloPersonalOptions>(rows, ChannelType.ZaloPersonal, "ZaloPersonal");

        trello = await LoadAppSettingAsync<TrelloOptions>(db, "Trello");
        ai = await LoadAppSettingAsync<AiOptions>(db, "Ai");
    }

    private async Task<T> LoadAppSettingAsync<T>(AppDbContext db, string key) where T : new()
    {
        var row = await db.AppSettings.AsNoTracking().FirstOrDefaultAsync(s => s.Key == key);
        if (row is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(row.Json) ?? new T();
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Cấu hình {Key} trong DB hỏng, dùng mặc định", key);
            }
        }
        return new T();
    }

    private T Load<T>(List<ChannelConnection> rows, ChannelType channel, string configKey) where T : new()
    {
        var row = rows.FirstOrDefault(r => r.Channel == channel);
        if (row is not null)
        {
            try
            {
                return JsonSerializer.Deserialize<T>(row.SettingsJson) ?? new T();
            }
            catch (JsonException ex)
            {
                logger.LogError(ex, "Cấu hình {Channel} trong DB hỏng, dùng mặc định", channel);
            }
        }

        // Chưa có trong DB → seed từ appsettings (nếu có)
        return configuration.GetSection($"{ChannelsOptions.SectionName}:{configKey}").Get<T>() ?? new T();
    }

    public Task SaveTrelloAsync(TrelloOptions options) => SaveAppSettingAsync("Trello", options, () => trello = options);
    public Task SaveAiAsync(AiOptions options) => SaveAppSettingAsync("Ai", options, () => ai = options);

    private async Task SaveAppSettingAsync<T>(string key, T options, Action applyToCache)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.AppSettings.FirstOrDefaultAsync(s => s.Key == key);
        if (row is null)
        {
            row = new AppSetting { Key = key };
            db.AppSettings.Add(row);
        }
        row.Json = JsonSerializer.Serialize(options, JsonOpts);
        await db.SaveChangesAsync();
        applyToCache();
        Changed?.Invoke();
    }

    public Task SaveZaloAsync(ZaloOptions options) => SaveAsync(ChannelType.Zalo, options, () => zalo = options);
    public Task SaveMessengerAsync(MessengerOptions options) => SaveAsync(ChannelType.Messenger, options, () => messenger = options);
    public Task SaveShopeeAsync(ShopeeOptions options) => SaveAsync(ChannelType.Shopee, options, () => shopee = options);
    public Task SaveZaloPersonalAsync(ZaloPersonalOptions options) => SaveAsync(ChannelType.ZaloPersonal, options, () => zaloPersonal = options);
    public Task SaveTikTokAsync(TikTokShopOptions options) => SaveAsync(ChannelType.TikTokShop, options, () => tikTok = options);

    private async Task SaveAsync(ChannelType channel, object options, Action applyToCache)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var row = await db.ChannelConnections.FirstOrDefaultAsync(r => r.Channel == channel);
        if (row is null)
        {
            row = new ChannelConnection { Channel = channel };
            db.ChannelConnections.Add(row);
        }
        row.SettingsJson = JsonSerializer.Serialize(options, options.GetType(), JsonOpts);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        applyToCache();
        Changed?.Invoke();
    }
}
