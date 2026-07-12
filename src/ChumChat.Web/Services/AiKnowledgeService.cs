using ChumChat.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace ChumChat.Web.Services;

// Quản lý kho kiến thức cho AI: câu hỏi thường gặp (FAQ) và ảnh tư liệu.
public class AiKnowledgeService(IDbContextFactory<AppDbContext> dbFactory)
{
    // ===== FAQ =====

    public async Task<List<AiFaq>> GetFaqsAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AiFaqs.AsNoTracking()
            .OrderBy(f => f.SortOrder).ThenBy(f => f.Id).ToListAsync();
    }

    public async Task AddFaqAsync(string question, string answer)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var maxOrder = await db.AiFaqs.Select(f => (int?)f.SortOrder).MaxAsync() ?? 0;
        db.AiFaqs.Add(new AiFaq { Question = question.Trim(), Answer = answer.Trim(), SortOrder = maxOrder + 1 });
        await db.SaveChangesAsync();
    }

    public async Task DeleteFaqAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.AiFaqs.FirstOrDefaultAsync(f => f.Id == id);
        if (item is not null)
        {
            db.AiFaqs.Remove(item);
            await db.SaveChangesAsync();
        }
    }

    // ===== Ảnh tư liệu =====

    public async Task<List<AiKnowledgeImage>> GetImagesAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.AiKnowledgeImages.AsNoTracking()
            .OrderByDescending(i => i.Id).ToListAsync();
    }

    public async Task AddImageAsync(string url, string caption)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.AiKnowledgeImages.Add(new AiKnowledgeImage
        {
            Url = url,
            Caption = caption.Trim(),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
    }

    public async Task DeleteImageAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.AiKnowledgeImages.FirstOrDefaultAsync(i => i.Id == id);
        if (item is not null)
        {
            db.AiKnowledgeImages.Remove(item);
            await db.SaveChangesAsync();
        }
    }
}
