using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Auth;

namespace Waypoint.Api.Data;

public sealed class WaypointDb(DbContextOptions<WaypointDb> options, CurrentUser currentUser) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<Slot> Slots => Set<Slot>();
    public DbSet<Aspiration> Aspirations => Set<Aspiration>();
    public DbSet<Interview> Interviews => Set<Interview>();
    public DbSet<InterviewMessage> InterviewMessages => Set<InterviewMessage>();

    // Read per query by the global filters. Null (no signed-in user) matches nothing.
    private Guid? CurrentUserId => currentUser.Id;

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasIndex(u => u.GoogleSub).IsUnique();
            e.Property(u => u.GoogleSub).HasMaxLength(255);
            e.HasQueryFilter(u => u.Id == CurrentUserId);
        });
        b.Entity<RefreshToken>(e =>
        {
            e.HasIndex(t => t.TokenHash).IsUnique();
            e.Property(t => t.TokenHash).HasMaxLength(64);
            e.HasIndex(t => t.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId);
            e.HasQueryFilter(t => t.UserId == CurrentUserId);
        });
    
        b.Entity<ChatMessage>(e =>
        {
            // Seq is the key so the database orders the thread; Id is what the API shows.
            e.HasKey(m => m.Seq);
            e.Property(m => m.Seq).ValueGeneratedOnAdd();
            e.HasIndex(m => m.Id).IsUnique();
            e.Property(m => m.Role).HasMaxLength(8);
            e.Property(m => m.Text).HasColumnType("longtext");
            e.Property(m => m.ClientId).HasMaxLength(64);
            e.Property(m => m.Card).HasMaxLength(32);
            e.Property(m => m.CardJson).HasColumnType("longtext");
            e.HasIndex(m => new { m.UserId, m.ClientId }).IsUnique();
            e.HasIndex(m => m.InReplyToId);
            e.HasOne<User>().WithMany().HasForeignKey(m => m.UserId);
            e.HasQueryFilter(m => m.UserId == CurrentUserId);
        });
        b.Entity<User>().Property(u => u.ContactFrequency).HasMaxLength(16);
        b.Entity<Slot>(e =>
        {
            e.Property(s => s.Label).HasMaxLength(64);
            e.Property(s => s.Days).HasMaxLength(32);
            e.Property(s => s.Start).HasMaxLength(5);
            e.Property(s => s.End).HasMaxLength(5);
            e.HasIndex(s => s.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(s => s.UserId);
            e.HasQueryFilter(s => s.UserId == CurrentUserId);
        });
        b.Entity<Aspiration>(e =>
        {
            e.Property(a => a.OwnWords).HasMaxLength(500);
            e.Property(a => a.ShortName).HasMaxLength(16);
            e.Property(a => a.Hue).HasMaxLength(16);
            e.Property(a => a.Status).HasMaxLength(16);
            e.Property(a => a.OnSkip).HasMaxLength(16);
            e.Property(a => a.SettleForPicture).HasColumnType("longtext");
            e.HasIndex(a => a.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId);
            e.HasQueryFilter(a => a.UserId == CurrentUserId);
        });
        b.Entity<Interview>(e =>
        {
            e.Property(i => i.OwnWords).HasMaxLength(500);
            e.Property(i => i.Status).HasMaxLength(24);
            e.Property(i => i.HandOffJson).HasColumnType("longtext");
            e.HasIndex(i => i.UserId);
            e.HasOne<User>().WithMany().HasForeignKey(i => i.UserId);
            e.HasQueryFilter(i => i.UserId == CurrentUserId);
        });
        b.Entity<InterviewMessage>(e =>
        {
            e.HasKey(m => m.Seq);
            e.Property(m => m.Seq).ValueGeneratedOnAdd();
            e.HasIndex(m => m.Id).IsUnique();
            e.Property(m => m.Role).HasMaxLength(8);
            e.Property(m => m.Text).HasColumnType("longtext");
            e.Property(m => m.ClientId).HasMaxLength(64);
            e.HasIndex(m => new { m.UserId, m.ClientId }).IsUnique();
            e.HasIndex(m => m.InterviewId);
            e.HasOne<User>().WithMany().HasForeignKey(m => m.UserId);
            e.HasQueryFilter(m => m.UserId == CurrentUserId);
        });
    }
}
