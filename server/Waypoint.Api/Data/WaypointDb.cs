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

    public DbSet<Job> Jobs => Set<Job>();
    public DbSet<Waypoint.Api.Push.DeviceToken> DeviceTokens => Set<Waypoint.Api.Push.DeviceToken>();
    public DbSet<PlanVersion> PlanVersions => Set<PlanVersion>();
    public DbSet<Milestone> Milestones => Set<Milestone>();
    public DbSet<Habit> Habits => Set<Habit>();
    public DbSet<PlanTask> PlanTasks => Set<PlanTask>();
    public DbSet<ScheduledAction> ScheduledActions => Set<ScheduledAction>();
    public DbSet<CheckIn> CheckIns => Set<CheckIn>();
    public DbSet<Change> Changes => Set<Change>();

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        LogChanges();
        return base.SaveChangesAsync(ct);
    }

    public override int SaveChanges() { LogChanges(); return base.SaveChanges(); }

    /// <summary>Notes what the phone must pull, in the same save. Rows changed through ExecuteUpdate call <see cref="Record"/> themselves.</summary>
    private void LogChanges()
    {
        ChangeTracker.DetectChanges();
        var seen = new HashSet<(string, Guid)>();
        foreach (var e in ChangeTracker.Entries().Where(e => e.State is EntityState.Added or EntityState.Modified or EntityState.Deleted).ToList())
        {
            var deleted = e.State == EntityState.Deleted;
            switch (e.Entity)
            {
                case ScheduledAction a: Add("scheduled_action", a.UserId, a.Id, deleted); break;
                case CheckIn c: Add("scheduled_action", c.UserId, c.ScheduledActionId, false); break;
                case Milestone m: Add("milestone", m.UserId, m.Id, deleted); break;
                case PlanVersion p: Add("plan_version", p.UserId, p.Id, deleted); break;
                case ChatMessage m:
                    Add("chat_message", m.UserId, m.Id, deleted);
                    // A reply changes whether the message it answers is still waiting.
                    if (m.InReplyToId is { } answered) Add("chat_message", m.UserId, answered, false);
                    break;
            }
        }
        void Add(string kind, Guid userId, Guid id, bool deleted)
        {
            if (seen.Add((kind, id)) || deleted) Changes.Add(new Change { UserId = userId, Kind = kind, EntityId = id, Deleted = deleted });
        }
    }

    /// <summary>For a change made outside the tracker (ExecuteUpdate): saved with the next SaveChanges.</summary>
    public void Record(string kind, Guid userId, Guid id) => Changes.Add(new Change { UserId = userId, Kind = kind, EntityId = id });

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

        b.Entity<Waypoint.Api.Push.DeviceToken>(e =>
        {
            e.Property(t => t.Token).HasMaxLength(512);
            e.HasIndex(t => new { t.UserId, t.Token }).IsUnique();
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(t => t.UserId == CurrentUserId);
        });

        b.Entity<Job>(e =>
        {
            e.Property(j => j.Kind).HasMaxLength(32);
            e.Property(j => j.State).HasMaxLength(16);
            e.Property(j => j.PayloadJson).HasMaxLength(512);
            e.HasIndex(j => new { j.State, j.NextRunAt });
            e.HasIndex(j => new { j.UserId, j.AspirationId });
            e.HasOne<User>().WithMany().HasForeignKey(j => j.UserId).OnDelete(DeleteBehavior.Cascade);
            // Only the job runner reads across users, and it says so with IgnoreQueryFilters.
            e.HasQueryFilter(j => j.UserId == CurrentUserId);
        });
        b.Entity<PlanVersion>(e =>
        {
            e.Property(p => p.Status).HasMaxLength(16);
            e.Property(p => p.Reason).HasMaxLength(32);
            e.Property(p => p.Rationale).HasColumnType("longtext");
            e.Property(p => p.Ramp).HasColumnType("longtext");
            e.Property(p => p.AssumptionsJson).HasColumnType("longtext");
            e.Property(p => p.TwoWeeksJson).HasColumnType("longtext");
            e.HasIndex(p => new { p.AspirationId, p.Status });
            e.HasOne<User>().WithMany().HasForeignKey(p => p.UserId);
            e.HasOne<Aspiration>().WithMany().HasForeignKey(p => p.AspirationId).OnDelete(DeleteBehavior.Cascade);
            e.HasQueryFilter(p => p.UserId == CurrentUserId);
        });
        b.Entity<Milestone>(e =>
        {
            e.Property(m => m.Ref).HasMaxLength(32);
            e.Property(m => m.Title).HasMaxLength(200);
            e.Property(m => m.ReachedWhen).HasMaxLength(500);
            e.Property(m => m.Kind).HasMaxLength(8);
            e.Property(m => m.State).HasMaxLength(16);
            e.HasIndex(m => m.PlanVersionId);
            e.HasOne<PlanVersion>().WithMany().HasForeignKey(m => m.PlanVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(m => m.UserId);
            e.HasQueryFilter(m => m.UserId == CurrentUserId);
        });
        b.Entity<Habit>(e =>
        {
            e.Property(h => h.Title).HasMaxLength(200);
            e.Property(h => h.SlotIds).HasMaxLength(512);
            e.HasIndex(h => h.PlanVersionId);
            e.HasOne<PlanVersion>().WithMany().HasForeignKey(h => h.PlanVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(h => h.UserId);
            e.HasQueryFilter(h => h.UserId == CurrentUserId);
        });
        b.Entity<PlanTask>(e =>
        {
            e.Property(t => t.Title).HasMaxLength(200);
            e.HasIndex(t => t.PlanVersionId);
            e.HasOne<PlanVersion>().WithMany().HasForeignKey(t => t.PlanVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Milestone>().WithMany().HasForeignKey(t => t.MilestoneId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(t => t.UserId);
            e.HasQueryFilter(t => t.UserId == CurrentUserId);
        });
        b.Entity<ScheduledAction>(e =>
        {
            e.Property(a => a.Title).HasMaxLength(200);
            e.HasIndex(a => new { a.UserId, a.Date });
            e.HasOne<PlanVersion>().WithMany().HasForeignKey(a => a.PlanVersionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Aspiration>().WithMany().HasForeignKey(a => a.AspirationId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Slot>().WithMany().HasForeignKey(a => a.SlotId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(a => a.UserId);
            e.HasQueryFilter(a => a.UserId == CurrentUserId);
        });
        b.Entity<Change>(e =>
        {
            e.HasKey(c => c.Seq);
            e.Property(c => c.Seq).ValueGeneratedOnAdd();
            e.Property(c => c.Kind).HasMaxLength(24);
            e.HasIndex(c => new { c.UserId, c.Seq });
            e.HasOne<User>().WithMany().HasForeignKey(c => c.UserId);
            e.HasQueryFilter(c => c.UserId == CurrentUserId);
        });
        b.Entity<ScheduledAction>().Property(a => a.MoveClientIds).HasMaxLength(1024);
        b.Entity<ScheduledAction>().Property(a => a.ReminderKind).HasMaxLength(16);
        b.Entity<ScheduledAction>().Property(a => a.ReminderText).HasMaxLength(300);
        b.Entity<ScheduledAction>().Property(a => a.ReminderOn).HasDefaultValue(true);
        b.Entity<CheckIn>(e =>
        {
            e.Property(c => c.ClientId).HasMaxLength(64);
            e.Property(c => c.State).HasMaxLength(8);
            e.Property(c => c.Reasons).HasMaxLength(256);
            e.Property(c => c.Note).HasColumnType("longtext");
            e.HasIndex(c => new { c.UserId, c.ClientId }).IsUnique();
            e.HasIndex(c => new { c.UserId, c.ScheduledActionId }).IsUnique();
            e.HasOne<ScheduledAction>().WithMany().HasForeignKey(c => c.ScheduledActionId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<User>().WithMany().HasForeignKey(c => c.UserId);
            e.HasQueryFilter(c => c.UserId == CurrentUserId);
        });
    }
}
