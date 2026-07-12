using ChumChat.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace ChumChat.Web.Services;

public class QuickReplyService(IDbContextFactory<AppDbContext> dbFactory)
{
    public async Task<List<QuickReply>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.QuickReplies.AsNoTracking()
            .OrderBy(q => q.SortOrder).ThenBy(q => q.Id)
            .ToListAsync();
    }

    public async Task AddAsync(string title, string content)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var maxOrder = await db.QuickReplies.Select(q => (int?)q.SortOrder).MaxAsync() ?? 0;
        db.QuickReplies.Add(new QuickReply
        {
            Title = title.Trim(),
            Content = content.Trim(),
            SortOrder = maxOrder + 1
        });
        await db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var item = await db.QuickReplies.FirstOrDefaultAsync(q => q.Id == id);
        if (item is not null)
        {
            db.QuickReplies.Remove(item);
            await db.SaveChangesAsync();
        }
    }
}
