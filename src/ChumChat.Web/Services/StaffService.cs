using ChumChat.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace ChumChat.Web.Services;

public class StaffService(IDbContextFactory<AppDbContext> dbFactory)
{
    // Kiểm tra đăng nhập; trả về Staff nếu đúng, null nếu sai
    public async Task<Staff?> ValidateLoginAsync(string username, string password)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var staff = await db.Staff.FirstOrDefaultAsync(s => s.Username == username && s.IsActive);
        if (staff is null || !PasswordHasher.Verify(password, staff.PasswordHash))
            return null;
        return staff;
    }

    public async Task<List<Staff>> GetAllAsync()
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Staff.AsNoTracking().OrderBy(s => s.DisplayName).ToListAsync();
    }

    public async Task<Staff?> GetAsync(int id)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        return await db.Staff.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id);
    }

    public async Task<(bool Ok, string? Error)> CreateAsync(string username, string displayName, string password, bool isAdmin)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        username = username.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
            return (false, "Tên đăng nhập và mật khẩu không được để trống");
        if (await db.Staff.AnyAsync(s => s.Username == username))
            return (false, "Tên đăng nhập đã tồn tại");

        db.Staff.Add(new Staff
        {
            Username = username,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? username : displayName.Trim(),
            PasswordHash = PasswordHasher.Hash(password),
            IsAdmin = isAdmin,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();
        return (true, null);
    }

    public async Task ChangePasswordAsync(int staffId, string newPassword)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var staff = await db.Staff.FirstAsync(s => s.Id == staffId);
        staff.PasswordHash = PasswordHasher.Hash(newPassword);
        await db.SaveChangesAsync();
    }

    public async Task SetActiveAsync(int staffId, bool active)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        var staff = await db.Staff.FirstOrDefaultAsync(s => s.Id == staffId);
        if (staff is not null)
        {
            staff.IsActive = active;
            await db.SaveChangesAsync();
        }
    }
}
