using System.Text.Json;
using ChumChat.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace ChumChat.Web.Services;

public class IposSyncService(
    IDbContextFactory<AppDbContext> dbFactory,
    IHttpClientFactory httpClientFactory,
    ILogger<IposSyncService> logger)
{
    private const string IposMenuUrl = "https://weborder.ipos.vn/api/v1/menu?pos_parent=BANHCHUMCHUM&pos_id=30925";

    public async Task<(int Total, int NewCount, int UpdatedCount)> SyncMenuAsync(CancellationToken ct = default)
    {
        logger.LogInformation("Bắt đầu đồng bộ menu từ iPOS...");
        var client = httpClientFactory.CreateClient();
        
        // Cần giả lập User-Agent của browser để tránh bị iPOS chặn (Cloudflare / Security)
        client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        
        var response = await client.GetAsync(IposMenuUrl, ct);
        response.EnsureSuccessStatusCode();

        var jsonStr = await response.Content.ReadAsStringAsync(ct);
        
        // Debug: Lưu file JSON thô ra wwwroot để kiểm tra cấu trúc
        try
        {
            var debugPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot", "ipos_debug.json");
            await File.WriteAllTextAsync(debugPath, jsonStr, ct);
        }
        catch { }

        using var doc = JsonDocument.Parse(jsonStr);
        
        if (!doc.RootElement.TryGetProperty("data", out var data) || 
            !data.TryGetProperty("items", out var items) || 
            items.ValueKind != JsonValueKind.Array)
        {
            var preview = jsonStr.Length > 200 ? jsonStr[..200] + "..." : jsonStr;
            throw new Exception($"Không lấy được dữ liệu món ăn từ iPOS. Phản hồi trả về: {preview}");
        }

        await using var db = await dbFactory.CreateDbContextAsync(ct);
        
        // Load các sản phẩm hiện tại để đối chiếu nhanh trong RAM
        var existingProducts = await db.Products.ToDictionaryAsync(p => p.Sku, p => p, ct);
        
        int total = 0;
        int newCount = 0;
        int updatedCount = 0;

        foreach (var item in items.EnumerateArray())
        {
            total++;

            // Lấy mã định danh độc nhất SKU
            string sku = "";
            if (item.TryGetProperty("store_item_id", out var skuProp) && skuProp.ValueKind == JsonValueKind.String)
            {
                sku = skuProp.GetString() ?? "";
            }
            
            // Nếu không có store_item_id thì fallback dùng id số của iPOS
            if (string.IsNullOrEmpty(sku) && item.TryGetProperty("id", out var idProp))
            {
                sku = idProp.ValueKind == JsonValueKind.Number ? idProp.GetInt64().ToString() : idProp.ToString();
            }

            if (string.IsNullOrEmpty(sku))
            {
                continue; // Bỏ qua nếu hoàn toàn không có mã định danh
            }

            // Lấy các thông tin khác
            string name = item.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
            string description = item.TryGetProperty("description", out var descProp) ? descProp.GetString() ?? "" : "";
            string imageUrl = item.TryGetProperty("image_url", out var imgProp) ? imgProp.GetString() ?? "" : "";
            
            // Lấy giá mang đi (ta_price) hoặc giá tại quán (ots_price)
            long price = 0;
            if (item.TryGetProperty("ta_price", out var taPriceProp) && taPriceProp.ValueKind == JsonValueKind.Number)
            {
                price = taPriceProp.GetInt64();
            }
            else if (item.TryGetProperty("ots_price", out var otsPriceProp) && otsPriceProp.ValueKind == JsonValueKind.Number)
            {
                price = otsPriceProp.GetInt64();
            }

            // Trạng thái sản phẩm
            bool isActive = false;
            if (item.TryGetProperty("status", out var statusProp))
            {
                isActive = statusProp.GetString() == "ACTIVE";
            }

            if (existingProducts.TryGetValue(sku, out var product))
            {
                // Kiểm tra xem có thay đổi gì không trước khi update
                bool changed = false;
                if (product.Name != name) { product.Name = name; changed = true; }
                if (product.Price != price) { product.Price = price; changed = true; }
                if (product.ImageUrl != imageUrl) { product.ImageUrl = imageUrl; changed = true; }
                if (product.Description != description) { product.Description = description; changed = true; }
                if (product.IsActive != isActive) { product.IsActive = isActive; changed = true; }

                if (changed)
                {
                    db.Products.Update(product);
                    updatedCount++;
                }
            }
            else
            {
                var newProduct = new Product
                {
                    Sku = sku,
                    Name = name,
                    Price = price,
                    ImageUrl = imageUrl,
                    Description = description,
                    IsActive = isActive
                };
                db.Products.Add(newProduct);
                newCount++;
            }
        }

        if (newCount > 0 || updatedCount > 0)
        {
            await db.SaveChangesAsync(ct);
        }

        logger.LogInformation("Đồng bộ hoàn tất. Tổng: {Total}, Thêm mới: {New}, Cập nhật: {Updated}", total, newCount, updatedCount);
        return (total, newCount, updatedCount);
    }
}
