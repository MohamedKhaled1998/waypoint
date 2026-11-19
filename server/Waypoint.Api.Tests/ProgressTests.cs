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
public sealed class ProgressTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private static readonly DateOnly Start = new(2026, 1, 6);

    /// <summary>One seeded week: "back:planned:state[:spent[:version]]", state done | partly | skipped | none (unreported).</summary>
    private sealed record Week(int Back, int Planned, string State, int? Spent, int Version);

    private static List<Week> Parse(string table) => table.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(w =>
    {
        var p = w.Split(':');
        return new Week(int.Parse(p[0]), int.Parse(p[1]), p[2], p.Length > 3 && p[3] != "" ? int.Parse(p[3]) : null, p.Length > 4 ? int.Parse(p[4]) : 1);
    }).ToList();

    /// <summary>
    /// A user with one active Aspiration and a current Plan version (number <paramref name="versions"/>) planning 90 minutes a week,
    /// with a Milestone on <paramref name="milestone"/>, and the given weeks of history. <paramref name="listPassed"/> says a refresh's
    /// "did it" list has passed, so unreported Actions up to it count as not done.
    /// </summary>
    private async Task<(HttpClient Client, Guid UserId, Guid AspirationId)> SeedAsync(string weeks, DateOnly? milestone = null, int versions = 1,
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

    private static async Task<JsonElement> ProgressOf(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/progress")).GetProperty("aspirations")[0];

    [Theory]
    [InlineData("1:100:done;2:100:skipped", 400.0 / 700)] // recent weeks weigh more: 4, 3, 2, 1
    [InlineData("1:100:done;4:100:skipped", 400.0 / 500)]
    [InlineData("1:100:skipped;4:100:done", 100.0 / 500)]
    [InlineData("1:100:partly:50", 0.5)] // partly counts the minutes actually spent
    [InlineData("1:100:none", 0.0)] // unreported counts as not done once the "did it" list has passed
    // Each week is scored against the Plan version it ran under: two weeks at 90 minutes (version 2), two at 60 (version 1).
    [InlineData("1:90:partly:45:2;2:90:partly:45:2;3:60:done::1;4:60:done::1", 495.0 / 810)]
    [InlineData("5:100:skipped;1:100:done", 1.0)] // older than four weeks is out of the window
    public async Task Pace_is_minutes_done_over_minutes_planned_weighted_toward_recent_weeks(string weeks, double expected)
    {
        var (c, _, _) = await SeedAsync(weeks, versions: weeks.Contains(":2;") ? 2 : 1);
        var pace = (await ProgressOf(c)).GetProperty("pace").GetDouble();
        Assert.Equal(expected, pace, 3);
    }

    [Fact]
    public async Task Unreported_Actions_are_not_counted_until_the_did_it_list_has_passed()
    {
        var (c, _, _) = await SeedAsync("1:100:done;2:100:none", listPassed: false);
        Assert.Equal(1.0, (await ProgressOf(c)).GetProperty("pace").GetDouble(), 3);
    }

    [Fact]
    public async Task With_nothing_planned_yet_there_is_no_Pace()
    {
        var (c, _, _) = await SeedAsync("");
        Assert.Equal(JsonValueKind.Null, (await ProgressOf(c)).GetProperty("pace").ValueKind);
    }

    private static string D(JsonElement e, string name) => e.GetProperty(name).GetString()!;

    [Theory]
    // Milestone on 2026-03-02, eight weeks out. Weeks are "back:planned:state:spent".
    // Everything done: the middle is the Milestone's date, and under ~6 weeks of history the range is never narrower than a week either side.
    [InlineData("1:100:done;2:100:done;3:100:done;4:100:done", "2026-03-02", "2026-02-23", "2026-03-09", "around")]
    // Half the time given: twice as long, again at the +/-1 week floor.
    [InlineData("1:100:partly:50;2:100:partly:50;3:100:partly:50;4:100:partly:50", "2026-04-27", "2026-04-20", "2026-05-04", "around")]
    // The ends come from the fastest (1.0) and slowest (0.5) weekly Pace; the middle from the weighted one (0.7).
    [InlineData("1:100:done;2:100:partly:50;3:100:partly:50;4:100:partly:50", "2026-03-26", "2026-03-02", "2026-04-27", "between")]
    public async Task Projection_is_the_remaining_planned_weeks_over_Pace_as_a_range(string weeks, string middle, string early, string late, string kind)
    {
        var (c, _, _) = await SeedAsync(weeks);
        var next = (await ProgressOf(c)).GetProperty("next");
        var projection = next.GetProperty("projection");
        Assert.Equal(kind, D(projection, "kind"));
        Assert.Equal(middle, D(projection, "middle"));
        Assert.Equal(early, D(projection, "early"));
        Assert.Equal(late, D(projection, "late"));
    }

    [Fact]
    public async Task A_Projection_is_said_as_a_part_of_a_month_never_a_percentage()
    {
        var (c, _, _) = await SeedAsync("1:100:done;2:100:partly:50;3:100:partly:50;4:100:partly:50");
        var projection = (await ProgressOf(c)).GetProperty("next").GetProperty("projection");
        Assert.Equal("early", D(projection.GetProperty("from"), "part"));
        Assert.Equal(3, projection.GetProperty("from").GetProperty("month").GetInt32());
        Assert.Equal("late", D(projection.GetProperty("to"), "part"));
        Assert.Equal(4, projection.GetProperty("to").GetProperty("month").GetInt32());
    }

    [Theory]
    [InlineData("")] // the Plan's first refresh has not happened
    [InlineData("too_early")]
    [InlineData("too_early;too_early")]
    public async Task The_first_two_refreshes_of_a_Plan_give_no_date_only_too_early(string readings)
    {
        var (c, _, _) = await SeedAsync("1:100:done;2:100:done", readings: readings);
        var a = await ProgressOf(c);
        Assert.Equal("too_early", D(a, "state"));
        Assert.Equal("too_early", D(a.GetProperty("next").GetProperty("projection"), "kind"));
        Assert.Equal("too_early", D(a.GetProperty("next"), "pill"));
    }

    /// <summary>Books a Weekly refresh for now and lets the job runner run it, as the clock would.</summary>
    private async Task RefreshAsync(Guid userId)
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

    private const string Half = "1:100:partly:50;2:100:partly:50;3:100:partly:50;4:100:partly:50";
    private const string Fast = "1:100:partly:150;2:100:partly:150;3:100:partly:150;4:100:partly:150";
    private const string All = "1:100:done;2:100:done;3:100:done;4:100:done";

    [Theory]
    // Slipping: the middle past the date at two refreshes in a row; one is not enough.
    [InlineData(Half, "on_track;on_track;on_track+over", "2027-01-01", "slipping")]
    [InlineData(Half, "on_track;on_track;on_track", "2027-01-01", "on_track")]
    [InlineData(Half, "on_track;on_track;slipping+over", "2027-01-01", "slipping")]
    [InlineData(All, "on_track;on_track;on_track+over", "2027-01-01", "on_track")] // back on the date: no longer slipping
    // Ahead: the late end a week or more before the date at two refreshes in a row.
    [InlineData(Fast, "on_track;on_track;on_track+early", "2027-01-01", "ahead")]
    [InlineData(Fast, "on_track;on_track;on_track", "2027-01-01", "on_track")]
    [InlineData(All, "on_track;on_track;on_track+early", "2027-01-01", "on_track")]
    // Past Settle-for: the optimistic end of the final Milestone past the date, one refresh is enough.
    [InlineData(Half, "on_track;on_track;on_track", "2026-03-03", "past_settle_for")]
    [InlineData(Half, "on_track;on_track;on_track", "2026-05-01", "on_track")] // optimistic end (Apr 20) is inside Settle-for
    // The first two refreshes are judged too early whatever the numbers say.
    [InlineData(Half, "", "2026-03-03", "too_early")]
    [InlineData(Half, "too_early", "2026-03-03", "too_early")]
    [InlineData(Half, "too_early;too_early", "2027-01-01", "on_track")] // the third refresh is the first with a date
    public async Task States_are_judged_at_the_refresh_by_the_two_in_a_row_rules(string weeks, string readings, string settleFor, string expected)
    {
        var (c, userId, _) = await SeedAsync(weeks, settleFor: DateOnly.Parse(settleFor), readings: readings);
        await RefreshAsync(userId);
        var after = await ProgressOf(c);
        Assert.Equal(expected, D(after, "state"));
    }

    [Fact]
    public async Task The_state_only_changes_at_a_refresh_not_as_Check_ins_come_in()
    {
        var (c, userId, aspirationId) = await SeedAsync(Half, readings: "on_track;on_track;on_track+over");
        Assert.Equal("on_track", D(await ProgressOf(c), "state")); // stored from the last refresh, though today's numbers say otherwise
        await RefreshAsync(userId);
        Assert.Equal("slipping", D(await ProgressOf(c), "state"));
        Assert.Equal(aspirationId.ToString(), D(await ProgressOf(c), "aspirationId"));
    }

    private List<string> CoachLines(Guid userId, Guid aspirationId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        return scope.ServiceProvider.GetRequiredService<WaypointDb>().ChatMessages
            .Where(m => m.AspirationId == aspirationId && m.Role == "coach").Select(m => m.Text).ToList();
    }

    [Fact]
    public async Task The_Coach_says_one_line_when_the_state_changes_phrasing_what_the_server_decided()
    {
        var (_, userId, aspirationId) = await SeedAsync(Half, readings: "on_track;on_track;on_track+over");
        await RefreshAsync(userId);
        Assert.Equal(["Where you stand: slipping"], CoachLines(userId, aspirationId));
        var brief = JsonDocument.Parse(factory.Coach.Requests.Last(r => r.Kind == Waypoint.Api.Coach.CoachCallKind.WeeklyRefresh).Turns[0].Text).RootElement.GetProperty("progress");
        Assert.Equal("slipping", D(brief, "state"));
        Assert.Equal("state_change", D(brief, "say"));
        Assert.DoesNotContain("%", brief.ToString());
    }

    [Fact]
    public async Task The_Coach_stays_quiet_when_nothing_changed_and_says_where_you_stand_about_monthly()
    {
        var (_, userId, aspirationId) = await SeedAsync(All, readings: "on_track;on_track;on_track");
        // A line a month ago is not yet due again... the seeded readings are 70 days old and none spoke, so the monthly line is due.
        await RefreshAsync(userId);
        Assert.Single(CoachLines(userId, aspirationId));
        Assert.Equal("where_you_stand", D(JsonDocument.Parse(factory.Coach.Requests.Last(r => r.Kind == Waypoint.Api.Coach.CoachCallKind.WeeklyRefresh).Turns[0].Text).RootElement.GetProperty("progress"), "say"));

        // A week later: nothing changed and the monthly line was said, so the Coach says nothing.
        factory.Clock.Advance(TimeSpan.FromDays(7));
        await RefreshAsync(userId);
        Assert.Single(CoachLines(userId, aspirationId));

        // After four weeks it is due again.
        factory.Clock.Advance(TimeSpan.FromDays(21));
        await RefreshAsync(userId);
        Assert.Equal(2, CoachLines(userId, aspirationId).Count);
    }

    [Fact]
    public async Task The_first_refreshes_say_nothing_about_progress_and_a_failed_model_call_stores_nothing()
    {
        var (c, userId, aspirationId) = await SeedAsync(All, readings: "");
        factory.Coach.Fail = true;
        try { await RefreshAsync(userId); } finally { factory.Coach.Fail = false; }
        Assert.Equal("too_early", D(await ProgressOf(c), "state"));
        Assert.Empty(CoachLines(userId, aspirationId)); // late, never faked

        factory.Clock.Advance(TimeSpan.FromMinutes(2)); // the job's backoff
        await RunJobsAsync();
        Assert.Empty(CoachLines(userId, aspirationId)); // too early: nothing to say yet
    }

    [Fact]
    public async Task Progress_gives_last_weeks_hours_the_Route_and_where_the_user_is_on_it()
    {
        var (c, _, aspirationId) = await SeedAsync("1:60:done;2:100:partly:50", readings: "on_track;on_track;on_track");
        var all = await c.GetFromJsonAsync<JsonElement>("/progress");
        var last = all.GetProperty("lastWeek");
        Assert.Equal(60, last.GetProperty("plannedMinutes").GetInt32());
        Assert.Equal(60, last.GetProperty("doneMinutes").GetInt32());
        Assert.Equal("kept_up", D(last, "line"));
        Assert.Equal("Half marathon", D(last.GetProperty("aspirations")[0], "shortName"));

        var one = await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}");
        Assert.Equal(0, one.GetProperty("milestonesReached").GetInt32());
        Assert.Equal(1, one.GetProperty("milestonesTotal").GetInt32());
        var m = one.GetProperty("milestones")[0];
        Assert.True(m.GetProperty("youAreHere").GetBoolean());
        Assert.Equal("you have done 12 long runs", D(m, "reachedWhen"));
        Assert.Equal("2026-03-02", D(m, "date"));
        Assert.Equal("on_track", D(m, "pill"));
        Assert.Equal(HttpStatusCode.NotFound, (await c.GetAsync($"/progress/{Guid.NewGuid()}")).StatusCode);
    }
}
