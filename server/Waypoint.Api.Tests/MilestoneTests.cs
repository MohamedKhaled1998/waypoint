using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>
/// Milestones reached, "nearly there" and extensions through the API. The fake clock sits at Monday 2026-01-05 09:00.
/// History is seeded straight into the database; each test uses a user of its own.
/// </summary>
public sealed partial class MilestoneTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private static readonly DateOnly Today = new(2026, 1, 5);

    public sealed record Seeded(HttpClient Client, Guid UserId, Guid AspirationId, Guid MilestoneId, Guid SlotId);

    /// <summary>
    /// One active Aspiration with a Plan planning 90 minutes a week (an Easy run Monday-ish each of the four weeks before today) and one
    /// Milestone of the given kind on <paramref name="date"/>, followed by a later effort Milestone. <paramref name="done"/> is the state
    /// of the four seeded runs.
    /// </summary>
    private async Task<Seeded> SeedAsync(string kind = "effort", DateOnly? date = null, string done = "done", string? measure = null, DateOnly? settleFor = null, DateOnly? secondDate = null, bool judged = true, bool noSettle = false, DateOnly? hopedFor = null)
    {
        var (client, userId) = await SignedInAsync();
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
        var slot = new Slot { Id = Guid.NewGuid(), UserId = userId, Label = "weekday mornings", Days = "mon,wed,fri", Start = "07:00", End = "08:00" };
        var aspiration = new Aspiration
        {
            Id = Guid.NewGuid(), UserId = userId, OwnWords = "run a half marathon", ShortName = "Half marathon", Hue = "coral", Status = "active",
            ShareHoursPerWeek = 3, HopedForDate = hopedFor ?? new DateOnly(2026, 6, 20), SettleForDate = noSettle ? null : settleFor ?? new DateOnly(2026, 7, 1), OnSkip = "gentle_check",
            CreatedAt = DateTime.UtcNow,
        };
        var version = new PlanVersion
        {
            Id = Guid.NewGuid(), UserId = userId, AspirationId = aspiration.Id, Number = 1, Rationale = "r", Reason = "first_plan", Ramp = "r",
            Status = PlanVersionStatus.Approved, AssumptionsJson = "[]", TwoWeeksJson = "[]", StartDate = Today.AddDays(-28),
            CreatedAt = DateTime.UtcNow, ApprovedAt = DateTime.UtcNow,
        };
        var m = new Milestone
        {
            Id = Guid.NewGuid(), UserId = userId, PlanVersionId = version.Id, Ref = "m1", Date = date ?? Today, Title = "Run 5 km",
            ReachedWhen = "you can run 5 km without stopping", Kind = kind, State = "upcoming",
        };
        var next = new Milestone
        {
            Id = Guid.NewGuid(), UserId = userId, PlanVersionId = version.Id, Ref = "m2", Date = secondDate ?? new DateOnly(2026, 4, 6), Title = "Run 10 km",
            ReachedWhen = "you have done 12 long runs", Kind = "effort", State = "upcoming",
        };
        if (measure is not null)
        {
            m.MeasureName = "Resting heart rate"; m.MeasureUnit = "bpm"; m.MeasureStart = 70; m.MeasureHopedFor = 60;
            next.MeasureName = "Resting heart rate"; next.MeasureUnit = "bpm"; next.MeasureStart = 70; next.MeasureHopedFor = 55;
        }
        db.Slots.Add(slot);
        db.Aspirations.Add(aspiration);
        db.PlanVersions.Add(version);
        db.Milestones.AddRange(m, next);
        await db.SaveChangesAsync();
        for (var week = 1; week <= 4; week++)
        {
            var action = new ScheduledAction
            {
                Id = Guid.NewGuid(), UserId = userId, AspirationId = aspiration.Id, PlanVersionId = version.Id, SlotId = slot.Id,
                Date = Today.AddDays(-7 * week + 1), Title = "Easy run", Minutes = 90,
            };
            db.ScheduledActions.Add(action);
            await db.SaveChangesAsync();
            if (done == "none") continue;
            db.CheckIns.Add(new CheckIn
            {
                Id = Guid.NewGuid(), UserId = userId, ScheduledActionId = action.Id, ClientId = "c-" + action.Id, State = done,
                MinutesSpent = done == "partly" ? 10 : null, TappedAt = action.Date.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc), EchoMessageId = Guid.NewGuid(),
            });
        }
        // Three refreshes have judged the Aspiration, so Projections are dated.
        for (var i = 0; judged && i < 3; i++)
            db.ProgressReadings.Add(new ProgressReading
            {
                Id = Guid.NewGuid(), UserId = userId, AspirationId = aspiration.Id, RefreshId = Guid.NewGuid(), PlanVersionId = version.Id,
                At = DateTime.UtcNow.AddDays(-21 + 7 * i), State = "on_track",
            });
        db.WeeklyRefreshes.Add(new WeeklyRefresh
        {
            Id = Guid.NewGuid(), UserId = userId, RunAt = DateTime.UtcNow, Status = RefreshStatus.Accepted, OpenedAt = Today.AddDays(-1).ToDateTime(new TimeOnly(18, 0), DateTimeKind.Utc),
        });
        await db.SaveChangesAsync();
        return new Seeded(client, userId, aspiration.Id, m.Id, slot.Id);
    }

    /// <summary>The job that fires on a Milestone's date, queued by hand because the seed skips Plan approval.</summary>
    private async Task QueueDueAsync(Seeded s, TimeSpan? delay = null)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = s.UserId;
        await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueAsync(JobKinds.MilestoneDue, s.UserId,
            new Dictionary<string, Guid> { ["milestoneId"] = s.MilestoneId }, s.AspirationId, delay);
    }

    /// <summary>Jobs queue more jobs (a Milestone's note, a Measure's check): step the runner until nothing is due.</summary>
    private async Task SettleAsync()
    {
        for (var i = 0; i < 6 && await RunJobsAsync() > 0; i++) { }
    }

    private static async Task<JsonElement> RouteAsync(HttpClient c, Guid aspirationId) =>
        await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}");

    private static JsonElement Milestone(JsonElement progress, string title) =>
        progress.GetProperty("milestones").EnumerateArray().Single(m => m.GetProperty("title").GetString() == title);

    private static async Task<List<JsonElement>> ThreadAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement>("/coach/messages")).EnumerateArray().ToList();

    [Fact]
    public async Task An_effort_Milestone_is_reached_from_Check_ins_and_the_Coach_celebrates_in_one_line()
    {
        var s = await SeedAsync("effort", done: "done");
        await QueueDueAsync(s);
        await SettleAsync();

        Assert.Equal("reached", Milestone(await RouteAsync(s.Client, s.AspirationId), "Run 5 km").GetProperty("state").GetString());
        var note = (await ThreadAsync(s.Client)).Single(m => m.GetProperty("card").GetString() == "milestone_reached");
        Assert.Equal("coach", note.GetProperty("role").GetString());
        Assert.False(string.IsNullOrWhiteSpace(note.GetProperty("text").GetString()));
    }

    [Fact]
    public async Task An_effort_Milestone_whose_work_was_skipped_is_asked_not_reached()
    {
        var s = await SeedAsync("effort", done: "skipped");
        await QueueDueAsync(s);
        await SettleAsync();

        Assert.Equal("upcoming", Milestone(await RouteAsync(s.Client, s.AspirationId), "Run 5 km").GetProperty("state").GetString());
        var ask = (await ThreadAsync(s.Client)).Single(m => m.GetProperty("card").GetString() == "milestone_ask");
        Assert.Equal(new[] { "yes", "no" }, ask.GetProperty("cardData").GetProperty("options").EnumerateArray().Select(o => o.GetString()));
    }

    [Fact]
    public async Task An_ability_Milestone_gets_a_test_task_in_its_last_week()
    {
        var date = new DateOnly(2026, 1, 21); // a Wednesday
        var s = await SeedAsync("ability", date, done: "done");
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = s.UserId;
            await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueAsync(JobKinds.MilestoneTestTask, s.UserId,
                new Dictionary<string, Guid> { ["milestoneId"] = s.MilestoneId }, s.AspirationId, TimeSpan.Zero);
        }
        await SettleAsync();

        var actions = await s.Client.GetFromJsonAsync<JsonElement>("/scheduled-actions");
        var test = actions.EnumerateArray().Single(a => a.GetProperty("title").GetString() == "Test: Run 5 km");
        var day = test.GetProperty("date").GetString()!;
        Assert.InRange(string.CompareOrdinal(day, "2026-01-15"), 0, int.MaxValue); // within the 7 days up to the date
        Assert.True(string.CompareOrdinal(day, "2026-01-21") <= 0);
        Assert.Contains(DateOnly.Parse(day).DayOfWeek, new[] { DayOfWeek.Monday, DayOfWeek.Wednesday, DayOfWeek.Friday });

    }

    [Fact]
    public async Task An_ability_Milestone_is_asked_at_its_date_even_when_the_work_is_done()
    {
        var s = await SeedAsync("ability", Today, done: "done");
        await QueueDueAsync(s);
        await SettleAsync();
        Assert.Equal("upcoming", Milestone(await RouteAsync(s.Client, s.AspirationId), "Run 5 km").GetProperty("state").GetString());
        Assert.Contains(await ThreadAsync(s.Client), m => m.GetProperty("card").GetString() == "milestone_ask");
    }

    [Fact]
    public async Task Approving_a_first_Plan_books_each_Milestone_date_and_the_ability_test_task()
    {
        var (client, user, _, aspirationId) = await ConfirmedAsync();
        var pending = await DraftedAsync(client, aspirationId);
        (await client.PostAsync($"/plan-versions/{pending}/approve", null)).EnsureSuccessStatusCode();

        using var scope = factory.Services.CreateScope();
        var jobs = await scope.ServiceProvider.GetRequiredService<WaypointDb>().Jobs.IgnoreQueryFilters()
            .Where(j => j.UserId == user && j.AspirationId == aspirationId && j.State == JobState.Pending).ToListAsync();
        Assert.Equal(2, jobs.Count(j => j.Kind == JobKinds.MilestoneDue)); // Plan(): an ability Milestone on 2 Feb and an effort one on 1 Jun
        Assert.Equal(1, jobs.Count(j => j.Kind == JobKinds.MilestoneTestTask));
        var test = jobs.Single(j => j.Kind == JobKinds.MilestoneTestTask);
        Assert.Equal(new DateTime(2026, 1, 26, 8, 0, 0), test.NextRunAt); // the week before 2 Feb
    }

    [Fact]
    public async Task Saying_I_have_got_it_early_reaches_the_Milestone_and_moves_You_are_here_on()
    {
        var s = await SeedAsync("ability", new DateOnly(2026, 3, 2), done: "done");

        var response = await s.Client.PostAsync($"/milestones/{s.MilestoneId}/reached", null);
        response.EnsureSuccessStatusCode();
        await SettleAsync();

        var route = await RouteAsync(s.Client, s.AspirationId);
        Assert.Equal("reached", Milestone(route, "Run 5 km").GetProperty("state").GetString());
        Assert.Equal("Run 10 km", route.GetProperty("next").GetProperty("title").GetString());
        // The Ahead rule is judged at the refresh, two in a row: one early Milestone alone says nothing.
        Assert.Equal("on_track", route.GetProperty("state").GetString());
        Assert.Contains(await ThreadAsync(s.Client), m => m.GetProperty("card").GetString() == "milestone_reached");
    }

    [Fact]
    public async Task A_no_after_the_work_is_nearly_there_and_offers_both_ways_out()
    {
        var s = await SeedAsync("effort", done: "partly"); // 10 of 90 minutes: not enough to count
        (await s.Client.PostAsync($"/milestones/{s.MilestoneId}/not-reached", null)).EnsureSuccessStatusCode();
        // Not enough work: no "nearly there", and no failed state either.
        Assert.Equal("upcoming", Milestone(await RouteAsync(s.Client, s.AspirationId), "Run 5 km").GetProperty("state").GetString());
        await SettleAsync();
        Assert.Contains(await ThreadAsync(s.Client), m => m.GetProperty("card").GetString() == "re_plan_offer");
        Assert.DoesNotContain(await ThreadAsync(s.Client), m => m.GetProperty("card").GetString() == "nearly_there");

        var s2 = await SeedAsync("effort", done: "done");
        (await s2.Client.PostAsync($"/milestones/{s2.MilestoneId}/not-reached", null)).EnsureSuccessStatusCode();
        await SettleAsync();
        Assert.Equal("nearly_there", Milestone(await RouteAsync(s2.Client, s2.AspirationId), "Run 5 km").GetProperty("state").GetString());
        var offer = (await ThreadAsync(s2.Client)).Single(m => m.GetProperty("card").GetString() == "nearly_there").GetProperty("cardData");
        Assert.Equal(new[] { "extend_1_week", "extend_2_weeks", "lower_bar" }, offer.GetProperty("options").EnumerateArray().Select(o => o.GetString()));
        Assert.Equal(new[] { 1, 2 }, offer.GetProperty("extensionWeeks").EnumerateArray().Select(o => o.GetInt32()));
    }

    [Fact]
    public async Task An_extension_adds_to_the_remaining_work_so_the_Projection_moves()
    {
        var s = await SeedAsync("effort", done: "done"); // Pace 1.0, Milestone due today
        (await s.Client.PostAsync($"/milestones/{s.MilestoneId}/not-reached", null)).EnsureSuccessStatusCode();
        var before = Milestone(await RouteAsync(s.Client, s.AspirationId), "Run 5 km").GetProperty("projection").GetProperty("middle").GetString();

        var extended = await s.Client.PostAsJsonAsync($"/milestones/{s.MilestoneId}/extend", new { weeks = 2 });
        extended.EnsureSuccessStatusCode();

        var after = Milestone(await RouteAsync(s.Client, s.AspirationId), "Run 5 km");
        Assert.Equal("upcoming", after.GetProperty("state").GetString());
        Assert.Equal("2026-01-19", after.GetProperty("date").GetString());
        Assert.Equal("2026-01-05", before);
        Assert.Equal("2026-01-19", after.GetProperty("projection").GetProperty("middle").GetString());
    }

    [Fact]
    public async Task An_extension_may_not_cross_the_Settle_for_date_nor_be_longer_than_two_weeks()
    {
        var s = await SeedAsync("effort", done: "done", settleFor: new DateOnly(2026, 1, 14));
        (await s.Client.PostAsync($"/milestones/{s.MilestoneId}/not-reached", null)).EnsureSuccessStatusCode();

        Assert.Equal(HttpStatusCode.BadRequest, (await s.Client.PostAsJsonAsync($"/milestones/{s.MilestoneId}/extend", new { weeks = 3 })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/milestones/{s.MilestoneId}/extend", new { weeks = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync($"/milestones/{s.MilestoneId}/extend", new { weeks = 1 })).StatusCode);
    }

    [Fact]
    public async Task Lowering_the_bar_rewrites_the_reached_when_line_and_counts_the_Milestone_reached()
    {
        var s = await SeedAsync("effort", done: "done");
        (await s.Client.PostAsync($"/milestones/{s.MilestoneId}/not-reached", null)).EnsureSuccessStatusCode();

        (await s.Client.PostAsJsonAsync($"/milestones/{s.MilestoneId}/lower-bar", new { reachedWhen = "you can run 4 km without stopping" })).EnsureSuccessStatusCode();

        var m = Milestone(await RouteAsync(s.Client, s.AspirationId), "Run 5 km");
        Assert.Equal("reached", m.GetProperty("state").GetString());
        Assert.Equal("you can run 4 km without stopping", m.GetProperty("reachedWhen").GetString());
    }

    [Fact]
    public async Task Another_user_cannot_answer_for_a_Milestone()
    {
        var s = await SeedAsync();
        var (other, _) = await SignedInAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/milestones/{s.MilestoneId}/reached", null)).StatusCode);
    }
}
