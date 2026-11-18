using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Auth;

namespace Waypoint.Api.Data;

public sealed class WaypointDb(DbContextOptions<WaypointDb> options, CurrentUser currentUser) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();

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
            e.HasIndex(m => new { m.UserId, m.ClientId }).IsUnique();
            e.HasIndex(m => m.InReplyToId);
            e.HasOne<User>().WithMany().HasForeignKey(m => m.UserId);
            e.HasQueryFilter(m => m.UserId == CurrentUserId);
        });
    }
}
