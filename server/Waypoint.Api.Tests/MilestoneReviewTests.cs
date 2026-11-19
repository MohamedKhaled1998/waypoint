using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;

namespace Waypoint.Api.Tests;

public sealed partial class MilestoneTests
{
    private async Task<T> WithDbAsync<T>(Seeded s, Func<WaypointDb, Task<T>> body)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = s.UserId;
        return await body(scope.ServiceProvider.GetRequiredService<WaypointDb>());
    }

    private async Task AskForReadingAsync(Seeded s)
    {
        await WithDbAsync(s, async db =>
        {
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(), UserId = s.UserId, Role = "coach", Text = "What is your resting heart rate now?", AspirationId = s.AspirationId,
                CreatedAt = DateTime.UtcNow, Card = "measure_ask", CardJson = "{}",
            });
            await db.SaveChangesAsync();
            return 0;
        });
    }

    private async Task ReplyAsync(Seeded s, string text) =>
        (await s.Client.PostAsJsonAsync("/coach/messages", new { text, clientId = "reply-" + Guid.NewGuid().ToString("N") })).EnsureSuccessStatusCode();

    private Task<List<MeasureReading>> ReadingsAsync(Seeded s) =>
        WithDbAsync(s, db => db.MeasureReadings.Where(r => r.AspirationId == s.AspirationId).ToListAsync());

    // 1
    [Fact]
    public async Task Approving_a_Re_plan_books_the_Milestone_dates_again_and_drops_the_old_jobs()
    {
        var s = await SeedAsync("ability", new DateOnly(2026, 1, 21));
        await QueueDueAsync(s, TimeSpan.FromDays(10)); // the first Plan's job for the old Milestone
        var proposed = await WithDbAsync(s, async db =>
        {
            var old = await db.PlanVersions.SingleAsync(p => p.AspirationId == s.AspirationId);
            var v2 = new PlanVersion
            {
                Id = Guid.NewGuid(), UserId = s.UserId, AspirationId = s.AspirationId, Number = null, Rationale = "r", Reason = "re_plan", Ramp = "r",
                Status = PlanVersionStatus.Proposed, AssumptionsJson = "[]", TwoWeeksJson = "[]", StartDate = Today.AddDays(1), CreatedAt = DateTime.UtcNow,
            };
            db.PlanVersions.Add(v2);
            foreach (var m in await db.Milestones.Where(m => m.PlanVersionId == old.Id).ToListAsync())
                db.Milestones.Add(new Milestone
                {
                    Id = Guid.NewGuid(), UserId = s.UserId, PlanVersionId = v2.Id, Ref = m.Ref, Date = m.Date, Title = m.Title, ReachedWhen = m.ReachedWhen,
                    Kind = m.Kind, State = m.State,
                });
            await db.SaveChangesAsync();
            return v2.Id;
        });
        (await s.Client.PostAsync($"/plan-versions/{proposed}/approve", null)).EnsureSuccessStatusCode();

        var jobs = await WithDbAsync(s, db => db.Jobs.Where(j => j.AspirationId == s.AspirationId && j.State == JobState.Pending
            && (j.Kind == JobKinds.MilestoneDue || j.Kind == JobKinds.MilestoneTestTask)).ToListAsync());
        // m1 (ability) and m2 (effort): one date job each, plus the one test task; nothing left over from the first Plan.
        Assert.Equal(2, jobs.Count(j => j.Kind == JobKinds.MilestoneDue));
        Assert.Equal(1, jobs.Count(j => j.Kind == JobKinds.MilestoneTestTask));
    }

    // 2 and 3
    [Fact]
    public async Task A_reply_that_is_a_number_is_saved_as_a_reading_and_checked()
    {
        var s = await SeedAsync(measure: "hr", date: new DateOnly(2026, 3, 2));
        (await ReadAsync(s, 70, "2025-12-08")).EnsureSuccessStatusCode();
        await SettleAsync(); // the first reading's own check has nothing to say
        await AskForReadingAsync(s);
        await ReplyAsync(s, "68 bpm");
        var added = (await ReadingsAsync(s)).Single(r => r.Source == MeasureSources.ChatReply);
        Assert.Equal(68, added.Value);

        await SettleAsync(); // the check runs: slower than hoped, so the Coach proposes a reset
        Assert.Single(await CardsAsync(s, "measure_reset"));
    }

    [Theory]
    [InlineData("ask me in 2 days")]
    [InlineData("2 days")]
    [InlineData("between 60 and 65")]
    [InlineData("I weighed 70 and 1 more")]
    public async Task A_reply_that_is_not_just_a_number_is_not_a_reading(string text)
    {
        var s = await SeedAsync(measure: "hr", date: new DateOnly(2026, 3, 2));
        await AskForReadingAsync(s);
        await ReplyAsync(s, text);
        Assert.Empty(await ReadingsAsync(s));
    }

    // 4
    [Fact]
    public async Task A_Milestone_not_yet_due_and_not_asked_cannot_be_answered_no()
    {
        var s = await SeedAsync("effort", new DateOnly(2026, 1, 21), done: "done");
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/milestones/{s.MilestoneId}/not-reached", null)).StatusCode);
        Assert.Equal("upcoming", Milestone(await RouteAsync(s.Client, s.AspirationId), "Run 5 km").GetProperty("state").GetString());
    }

    // 5
    [Fact]
    public async Task An_extension_keeps_the_Milestones_in_order()
    {
        var s = await SeedAsync("effort", done: "done", secondDate: Today.AddDays(10));
        (await s.Client.PostAsync($"/milestones/{s.MilestoneId}/not-reached", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/milestones/{s.MilestoneId}/extend", new { weeks = 2 })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await s.Client.PostAsJsonAsync($"/milestones/{s.MilestoneId}/extend", new { weeks = 1 })).StatusCode);
    }

    [Fact]
    public async Task An_extension_may_not_pass_the_hoped_for_date_when_there_is_no_Settle_for_date()
    {
        var s = await SeedAsync("effort", done: "done", noSettle: true, hopedFor: Today.AddDays(10));
        (await s.Client.PostAsync($"/milestones/{s.MilestoneId}/not-reached", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsJsonAsync($"/milestones/{s.MilestoneId}/extend", new { weeks = 2 })).StatusCode);
    }

    [Fact]
    public async Task An_extension_clears_an_earlier_not_reached()
    {
        var s = await SeedAsync("effort", done: "done");
        (await s.Client.PostAsync($"/milestones/{s.MilestoneId}/not-reached", null)).EnsureSuccessStatusCode();
        await WithDbAsync(s, async db =>
        {
            (await db.Milestones.SingleAsync(m => m.Id == s.MilestoneId)).NotReachedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return 0;
        });
        (await s.Client.PostAsJsonAsync($"/milestones/{s.MilestoneId}/extend", new { weeks = 1 })).EnsureSuccessStatusCode();
        Assert.Null(await WithDbAsync(s, async db => (await db.Milestones.SingleAsync(m => m.Id == s.MilestoneId)).NotReachedAt));
    }

    // 6
    [Fact]
    public async Task A_skipped_test_task_and_a_day_not_yet_over_do_not_count_against_the_work()
    {
        var s = await SeedAsync("effort", done: "done");
        await WithDbAsync(s, async db =>
        {
            foreach (var (date, title, test) in new[] { (Today.AddDays(-1), "Test: Other", "m0"), (Today, "Easy run", null) })
                db.ScheduledActions.Add(new ScheduledAction
                {
                    Id = Guid.NewGuid(), UserId = s.UserId, AspirationId = s.AspirationId, PlanVersionId = db.PlanVersions.Single(p => p.AspirationId == s.AspirationId).Id,
                    SlotId = s.SlotId, Date = date, Title = title, Minutes = 90, TestForMilestoneRef = test,
                });
            await db.SaveChangesAsync();
            return 0;
        });
        await QueueDueAsync(s);
        await SettleAsync();
        Assert.Equal("reached", Milestone(await RouteAsync(s.Client, s.AspirationId), "Run 5 km").GetProperty("state").GetString());
    }

    // 7
    [Fact]
    public async Task A_lagging_Measure_with_nothing_to_reset_sends_no_proposal_and_starts_no_cooldown()
    {
        var s = await SeedAsync(measure: "hr", done: "done"); // m1 is due today
        await WithDbAsync(s, async db =>
        {
            (await db.Milestones.SingleAsync(m => m.Id == s.MilestoneId)).State = MilestoneService.NearlyThere;
            (await db.Milestones.SingleAsync(m => m.Ref == "m2")).State = MilestoneService.Reached;
            await db.SaveChangesAsync();
            return 0;
        });
        (await ReadAsync(s, 70, "2025-12-08")).EnsureSuccessStatusCode();
        (await ReadAsync(s, 68, "2026-01-05")).EnsureSuccessStatusCode();
        await SettleAsync();
        Assert.Empty(await CardsAsync(s, "measure_reset"));
    }

    [Fact]
    public async Task Accepting_a_reset_needs_an_outstanding_proposal_and_works_once()
    {
        var s = await SeedAsync(measure: "hr", date: new DateOnly(2026, 3, 2));
        (await ReadAsync(s, 70, "2025-12-08")).EnsureSuccessStatusCode();
        (await ReadAsync(s, 68, "2026-01-05")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/aspirations/{s.AspirationId}/measure/accept-reset", null)).StatusCode);
        await SettleAsync();
        (await s.Client.PostAsync($"/aspirations/{s.AspirationId}/measure/accept-reset", null)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await s.Client.PostAsync($"/aspirations/{s.AspirationId}/measure/accept-reset", null)).StatusCode);
    }
}
