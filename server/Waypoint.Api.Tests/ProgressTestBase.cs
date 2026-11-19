using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>
/// Pace, Projection and states through GET /progress. The fake clock sits at Monday 2026-01-05 09:00, so the weeks Pace
/// looks back over end on Monday the 5th: week 1 is Dec 30 - Jan 5, week 2 Dec 23 - Dec 29, and so on.
/// History is seeded straight into the database; each test uses a user of its own.
/// </summary>
public abstract class ProgressTestBase(WaypointApiFactory factory) : PlanTestBase(factory)
{
    protected static readonly DateOnly Start = new(2026, 1, 6);

    /// <summary>One seeded week: "back:planned:state[:spent[:version]]", state done | partly | skipped | none (unreported).</summary>
    protected sealed record Week(int Back, int Planned, string State, int? Spent, int Version);

    protected static List<Week> Parse(string table) => table.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(w =>
    {
        var p = w.Split(':');
        return new Week(int.Parse(p[0]), int.Parse(p[1]), p[2], p.Length > 3 && p[3] != "" ? int.Parse(p[3]) : null, p.Length > 4 ? int.Parse(p[4]) : 1);
    }).ToList();

    /// <summary>
    /// A user with one active Aspiration and a current Plan version (number <paramref name="versions"/>) planning 90 minutes a week,
    /// with a Milestone on <paramref name="milestone"/>, and the given weeks of history. <paramref name="listPassed"/> says a refresh's
    /// "did it" list has passed, so unreported Actions up to it count as not done.
    /// </summary>
    protected async Task<(HttpClient Client, Guid UserId, Guid AspirationId)> SeedAsync(string weeks, DateOnly? milestone = null, int versions = 1,
        bool listPassed = true, DateOnly? settleFor = null, string readings = "on_track;on_track;on_track")
    {
        var (client, userId) = await SignedInAsync();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
        var slot = new Slot { Id = Guid.NewGuid(), UserId = userId, Label = "mornings", Days = "mon,tue,wed,thu,fri,sat,sun", Start = "07:00", End = "08:00" };
        var aspiration = new Aspiration
        {
            Id = Guid.NewGuid(), UserId = userId, OwnWords = "run a half marathon", ShortName = "Half marathon", Hue = "coral", Status = "active",
            ShareHoursPerWeek = 3, HopedForDate = new DateOnly(2026, 12, 20), SettleForDate = settleFor ?? new DateOnly(2027, 1, 1), OnSkip = "gentle_check",
            CreatedAt = DateTime.UtcNow,
        };
        db.Slots.Add(slot);
        db.Aspirations.Add(aspiration);
        var ids = new Dictionary<int, Guid>();
        for (var v = 1; v <= versions; v++)
        {
            var id = ids[v] = Guid.NewGuid();
            db.PlanVersions.Add(new PlanVersion
            {
                Id = id, UserId = userId, AspirationId = aspiration.Id, Number = v, Rationale = "r", Reason = v == 1 ? "first_plan" : "weekly_refresh", Ramp = "r",
                Status = v == versions ? PlanVersionStatus.Approved : PlanVersionStatus.Superseded, AssumptionsJson = "[]", TwoWeeksJson = "[]",
                StartDate = Start.AddDays(-35), CreatedAt = DateTime.UtcNow, ApprovedAt = DateTime.UtcNow,
            });
        }
        var current = ids[versions];
        db.Habits.Add(new Habit { Id = Guid.NewGuid(), UserId = userId, PlanVersionId = current, Title = "Easy run", TimesPerWeek = 3, Minutes = 30, SlotIds = slot.Id.ToString() });
        db.Milestones.Add(new Milestone
        {
            Id = Guid.NewGuid(), UserId = userId, PlanVersionId = current, Ref = "m1", Date = milestone ?? new DateOnly(2026, 3, 2),
            Title = "Run 10 km", ReachedWhen = "you have done 12 long runs", Kind = "effort", State = "upcoming",
        });
        await db.SaveChangesAsync();

        foreach (var w in Parse(weeks))
        {
            var action = new ScheduledAction
            {
                Id = Guid.NewGuid(), UserId = userId, AspirationId = aspiration.Id, PlanVersionId = ids[w.Version], SlotId = slot.Id,
                Date = Start.AddDays(-7 * w.Back + 3), Title = "Easy run", Minutes = w.Planned,
            };
            db.ScheduledActions.Add(action);
            await db.SaveChangesAsync(); // the Check-in's foreign key needs the Action first
            if (w.State == "none") continue;
            db.CheckIns.Add(new CheckIn
            {
                Id = Guid.NewGuid(), UserId = userId, ScheduledActionId = action.Id, ClientId = "c-" + action.Id, State = w.State, MinutesSpent = w.Spent,
                TappedAt = action.Date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc), EchoMessageId = Guid.NewGuid(),
            });
        }
        // Earlier refreshes' readings, oldest first: "state" plus "+over" (middle past the date) and "+early" (late end a week early).
        var at = factory.Clock.GetUtcNow().UtcDateTime.AddDays(-70);
        foreach (var r in readings.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var bits = r.Split('+');
            db.ProgressReadings.Add(new Waypoint.Api.Coach.ProgressReading
            {
                Id = Guid.NewGuid(), UserId = userId, AspirationId = aspiration.Id, RefreshId = Guid.NewGuid(), PlanVersionId = current, At = at += TimeSpan.FromDays(7),
                State = bits[0], MiddleOver = bits.Contains("over"), LateEarly = bits.Contains("early"),
            });
        }
        if (listPassed)
            db.WeeklyRefreshes.Add(new WeeklyRefresh
            {
                Id = Guid.NewGuid(), UserId = userId, RunAt = DateTime.UtcNow, Status = RefreshStatus.Accepted,
                OpenedAt = Start.AddDays(-1).ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc),
            });
        await db.SaveChangesAsync();
        return (client, userId, aspiration.Id);
    }

    protected static async Task<JsonElement> ProgressOf(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/progress")).GetProperty("aspirations")[0];

    protected static string D(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    /// <summary>Books a Weekly refresh for now and lets the job runner run it, as the clock would.</summary>
    protected async Task RefreshAsync(Guid userId)
    {
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
            var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
            var now = factory.Clock.GetUtcNow().UtcDateTime;
            var refresh = new WeeklyRefresh { Id = Guid.NewGuid(), UserId = userId, RunAt = now, Status = RefreshStatus.Scheduled };
            db.WeeklyRefreshes.Add(refresh);
            scope.ServiceProvider.GetRequiredService<Waypoint.Api.Coach.JobQueue>().Add(Waypoint.Api.Coach.JobKinds.WeeklyRefresh, userId,
                new Dictionary<string, Guid> { ["refreshId"] = refresh.Id });
            await db.SaveChangesAsync();
        }
        await RunJobsAsync();
    }

    protected const string Half = "1:100:partly:50;2:100:partly:50;3:100:partly:50;4:100:partly:50";
    protected const string Fast = "1:100:partly:150;2:100:partly:150;3:100:partly:150;4:100:partly:150";
    protected const string All = "1:100:done;2:100:done;3:100:done;4:100:done";

    protected List<string> CoachLines(Guid userId, Guid aspirationId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        return scope.ServiceProvider.GetRequiredService<WaypointDb>().ChatMessages
            .Where(m => m.AspirationId == aspirationId && m.Role == "coach").Select(m => m.Text).ToList();
    }

}
