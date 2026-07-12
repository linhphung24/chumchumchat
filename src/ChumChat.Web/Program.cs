using ChumChat.Web.Channels;
using ChumChat.Web.Components;
using ChumChat.Web.Data;
using ChumChat.Web.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();
builder.Services.AddControllers();
builder.Services.AddHttpClient();
builder.Services.AddHttpContextAccessor();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.LogoutPath = "/account/logout";
        options.ExpireTimeSpan = TimeSpan.FromDays(14);
        options.SlidingExpiration = true;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

// Lưu khóa mã hóa cookie ra thư mục cố định để không bị đăng xuất mỗi lần restart/deploy
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "dataprotection-keys")));

builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("Default") ?? "Data Source=chumchat.db"));

builder.Services.AddSingleton<ChannelSettingsStore>();
builder.Services.AddSingleton<OAuthStateCache>();
builder.Services.AddSingleton<IChannelAdapter, ZaloAdapter>();
builder.Services.AddSingleton<IChannelAdapter, MessengerAdapter>();
builder.Services.AddSingleton<IChannelAdapter, ShopeeAdapter>();
builder.Services.AddSingleton<IChannelAdapter, TikTokShopAdapter>();
builder.Services.AddSingleton<IChannelAdapter, ZaloPersonalAdapter>();
builder.Services.AddSingleton<InboxEvents>();
builder.Services.AddSingleton<InboxService>();
builder.Services.AddSingleton<TrelloService>();
builder.Services.AddSingleton<AiKnowledgeService>();
builder.Services.AddSingleton<AiSuggestionService>();
builder.Services.AddSingleton<AutoReplyService>();
builder.Services.AddSingleton<WebhookLogService>();
builder.Services.AddScoped<StaffService>();
builder.Services.AddScoped<QuickReplyService>();
builder.Services.AddHostedService<TokenRefreshService>();

var app = builder.Build();

// Chạy sau reverse proxy (nginx): đọc X-Forwarded-For / X-Forwarded-Proto
// để Request.Scheme là https thật — chữ ký webhook Shopee phụ thuộc URL đầy đủ
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Tạo database SQLite nếu chưa có, rồi nạp cấu hình kênh từ DB vào cache
using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.EnsureCreated();

    // EnsureCreated không cập nhật DB đã tồn tại từ phiên bản cũ —
    // tự thêm bảng/cột mới ở đây để không phải xóa dữ liệu khi nâng cấp
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "AppSettings" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_AppSettings" PRIMARY KEY AUTOINCREMENT,
            "Key" TEXT NOT NULL,
            "Json" TEXT NOT NULL);
        """);
    db.Database.ExecuteSqlRaw("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_AppSettings_Key" ON "AppSettings" ("Key");""");
    var hasTagColumn = db.Database
        .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Conversations') WHERE name='Tag'")
        .AsEnumerable().First() > 0;
    if (!hasTagColumn)
        db.Database.ExecuteSqlRaw("""ALTER TABLE "Conversations" ADD COLUMN "Tag" TEXT NOT NULL DEFAULT ''""");

    var hasAttachmentColumn = db.Database
        .SqlQueryRaw<int>("SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Messages') WHERE name='AttachmentUrl'")
        .AsEnumerable().First() > 0;
    if (!hasAttachmentColumn)
        db.Database.ExecuteSqlRaw("""ALTER TABLE "Messages" ADD COLUMN "AttachmentUrl" TEXT NULL""");

    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Orders" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Orders" PRIMARY KEY AUTOINCREMENT,
            "ConversationId" INTEGER NOT NULL,
            "Title" TEXT NOT NULL,
            "Amount" INTEGER NOT NULL,
            "Note" TEXT NOT NULL,
            "TrelloCardUrl" TEXT NULL,
            "CreatedAt" TEXT NOT NULL);
        """);
    db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_Orders_ConversationId" ON "Orders" ("ConversationId");""");

    foreach (var (col, ddl) in new[]
    {
        ("AvatarUrl", """ALTER TABLE "Conversations" ADD COLUMN "AvatarUrl" TEXT NULL"""),
        ("AssignedStaffId", """ALTER TABLE "Conversations" ADD COLUMN "AssignedStaffId" INTEGER NULL"""),
    })
    {
        var has = db.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Conversations') WHERE name='{col}'")
            .AsEnumerable().First() > 0;
        if (!has)
            db.Database.ExecuteSqlRaw(ddl);
    }

    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Staff" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Staff" PRIMARY KEY AUTOINCREMENT,
            "Username" TEXT NOT NULL,
            "DisplayName" TEXT NOT NULL,
            "PasswordHash" TEXT NOT NULL,
            "IsAdmin" INTEGER NOT NULL,
            "IsActive" INTEGER NOT NULL,
            "CreatedAt" TEXT NOT NULL);
        """);
    db.Database.ExecuteSqlRaw("""CREATE UNIQUE INDEX IF NOT EXISTS "IX_Staff_Username" ON "Staff" ("Username");""");
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "QuickReplies" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_QuickReplies" PRIMARY KEY AUTOINCREMENT,
            "Title" TEXT NOT NULL,
            "Content" TEXT NOT NULL,
            "SortOrder" INTEGER NOT NULL);
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "AiFaqs" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_AiFaqs" PRIMARY KEY AUTOINCREMENT,
            "Question" TEXT NOT NULL,
            "Answer" TEXT NOT NULL,
            "SortOrder" INTEGER NOT NULL);
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "AiKnowledgeImages" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_AiKnowledgeImages" PRIMARY KEY AUTOINCREMENT,
            "Url" TEXT NOT NULL,
            "Caption" TEXT NOT NULL,
            "CreatedAt" TEXT NOT NULL);
        """);
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "AutoReplyRules" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_AutoReplyRules" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "Keywords" TEXT NOT NULL,
            "MatchDescription" TEXT NOT NULL,
            "ReplyText" TEXT NOT NULL,
            "FileUrl" TEXT NOT NULL,
            "FileName" TEXT NOT NULL,
            "FileMime" TEXT NOT NULL,
            "Enabled" INTEGER NOT NULL,
            "SortOrder" INTEGER NOT NULL);
        """);

    // Bảng sản phẩm
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "Products" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_Products" PRIMARY KEY AUTOINCREMENT,
            "Name" TEXT NOT NULL,
            "Sku" TEXT NOT NULL,
            "Price" INTEGER NOT NULL,
            "IsActive" INTEGER NOT NULL);
        """);

    // Bảng dòng sản phẩm trong đơn hàng
    db.Database.ExecuteSqlRaw("""
        CREATE TABLE IF NOT EXISTS "OrderItems" (
            "Id" INTEGER NOT NULL CONSTRAINT "PK_OrderItems" PRIMARY KEY AUTOINCREMENT,
            "OrderId" INTEGER NOT NULL,
            "ProductName" TEXT NOT NULL,
            "Quantity" INTEGER NOT NULL,
            "UnitPrice" INTEGER NOT NULL,
            CONSTRAINT "FK_OrderItems_Orders_OrderId" FOREIGN KEY ("OrderId") REFERENCES "Orders" ("Id") ON DELETE CASCADE);
        """);
    db.Database.ExecuteSqlRaw("""CREATE INDEX IF NOT EXISTS "IX_OrderItems_OrderId" ON "OrderItems" ("OrderId");""");

    // Cột mới trên bảng Orders (thông tin khách + thanh toán)
    foreach (var (col, ddl) in new[]
    {
        ("CustomerPhone", """ALTER TABLE "Orders" ADD COLUMN "CustomerPhone" TEXT NOT NULL DEFAULT ''"""),
        ("CustomerAddress", """ALTER TABLE "Orders" ADD COLUMN "CustomerAddress" TEXT NOT NULL DEFAULT ''"""),
        ("PaymentMethod", """ALTER TABLE "Orders" ADD COLUMN "PaymentMethod" TEXT NOT NULL DEFAULT ''"""),
        ("ShippingFee", """ALTER TABLE "Orders" ADD COLUMN "ShippingFee" INTEGER NOT NULL DEFAULT 0"""),
        ("Discount", """ALTER TABLE "Orders" ADD COLUMN "Discount" INTEGER NOT NULL DEFAULT 0"""),
    })
    {
        var has = db.Database
            .SqlQueryRaw<int>($"SELECT COUNT(*) AS \"Value\" FROM pragma_table_info('Orders') WHERE name='{col}'")
            .AsEnumerable().First() > 0;
        if (!has)
            db.Database.ExecuteSqlRaw(ddl);
    }

    // Tạo tài khoản admin mặc định lần chạy đầu (admin / admin — đổi mật khẩu ngay sau khi đăng nhập)
    if (!db.Staff.Any())
    {
        db.Staff.Add(new Staff
        {
            Username = "admin",
            DisplayName = "Quản trị viên",
            PasswordHash = ChumChat.Web.Services.PasswordHasher.Hash("admin"),
            IsAdmin = true,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        });
        db.SaveChanges();
    }
}

// Tạo sẵn các thư mục upload (ảnh chat, ảnh tư liệu AI, file kịch bản tự động)
var uploadsRoot = Path.Combine(app.Environment.WebRootPath ?? "wwwroot", "uploads");
foreach (var sub in new[] { "", "ai", "auto" })
    Directory.CreateDirectory(Path.Combine(uploadsRoot, sub));
await app.Services.GetRequiredService<ChannelSettingsStore>().InitializeAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapControllers();
app.UseStaticFiles(); // phục vụ file upload động (/uploads/...) — MapStaticAssets chỉ lo asset lúc build
app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
