using Microsoft.EntityFrameworkCore;

namespace ChumChat.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<ChannelConnection> ChannelConnections => Set<ChannelConnection>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Order> Orders => Set<Order>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<QuickReply> QuickReplies => Set<QuickReply>();
    public DbSet<AiFaq> AiFaqs => Set<AiFaq>();
    public DbSet<AiKnowledgeImage> AiKnowledgeImages => Set<AiKnowledgeImage>();
    public DbSet<AutoReplyRule> AutoReplyRules => Set<AutoReplyRule>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelConnection>()
            .HasIndex(c => c.Channel)
            .IsUnique();

        modelBuilder.Entity<AppSetting>()
            .HasIndex(s => s.Key)
            .IsUnique();

        modelBuilder.Entity<Order>()
            .HasIndex(o => o.ConversationId);

        modelBuilder.Entity<Staff>()
            .HasIndex(s => s.Username)
            .IsUnique();

        modelBuilder.Entity<Conversation>()
            .HasIndex(c => new { c.Channel, c.ExternalId })
            .IsUnique();

        modelBuilder.Entity<Message>()
            .HasIndex(m => new { m.ConversationId, m.ExternalMessageId });

        modelBuilder.Entity<Message>()
            .HasOne(m => m.Conversation)
            .WithMany(c => c.Messages)
            .HasForeignKey(m => m.ConversationId);

        // Order → OrderItems (cascade delete khi xóa đơn)
        modelBuilder.Entity<OrderItem>()
            .HasOne(i => i.Order)
            .WithMany(o => o.Items)
            .HasForeignKey(i => i.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        modelBuilder.Entity<OrderItem>()
            .HasIndex(i => i.OrderId);

        modelBuilder.Entity<PushSubscription>()
            .HasIndex(s => s.StaffId);
    }
}
