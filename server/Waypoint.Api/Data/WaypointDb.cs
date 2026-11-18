using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Auth;

namespace Waypoint.Api.Data;

public sealed class WaypointDb(DbContextOptions<WaypointDb> options, CurrentUser currentUser) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

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
    }
}
