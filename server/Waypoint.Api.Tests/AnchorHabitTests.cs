using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Coach;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed class AnchorHabitTests(WaypointApiFactory factory) : AnchorTestBase(factory), IClassFixture<WaypointApiFactory>
{
    [Fact]
    public async Task An_Anchor_habit_lands_on_the_timeline_at_its_usual_time_with_no_Slot()
    {
        var (c, _, _, _) = await ApprovedAsync(PlanWithAnchor());
        var walks = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Where(a => a.GetProperty("title").GetString() == "Walk after dinner").ToList();
        Assert.Equal(2, walks.Count);
        var w = walks[0];
        Assert.Equal(JsonValueKind.Null, w.GetProperty("slot").ValueKind);
        Assert.Equal("19:30", w.GetProperty("slotStart").GetString());
        Assert.Equal("after dinner", w.GetProperty("anchor").GetProperty("words").GetString());
        Assert.Equal(20, w.GetProperty("targetMinutes").GetInt32());
    }

    [Fact]
    public async Task A_first_Plan_starts_with_one_Anchor_habit_so_a_second_is_sent_back_and_never_stored()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var two = PlanWithAnchor(p => p["habits"]!.AsArray().Add(AnchorHabit("Eat at home", 5, "weekday dinners")));
        Script(two);
        Script(two);
        await RunJobsAsync();
        var plans = await PlansAsync(c, aspirationId);
        Assert.Equal(JsonValueKind.Null, plans.GetProperty("pendingVersionId").ValueKind);
        Assert.Contains("anchor_habits_over_limit", string.Join(" ", factory.Coach.Requests.Last().Turns.Select(t => t.Text)));

        await CloseRetryJobsAsync();
    }

    [Fact]
    public async Task A_Plan_draft_is_sent_back_when_it_would_take_the_Anchor_habits_across_all_Aspirations_past_four()
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        await SeedOtherAnchorHabitsAsync(userId, 4);
        var plan = PlanWithAnchor();
        Script(plan);
        Script(plan);
        await RunJobsAsync();
        var plans = await PlansAsync(c, aspirationId);
        Assert.Equal(JsonValueKind.Null, plans.GetProperty("pendingVersionId").ValueKind);
        Assert.Contains("anchor_habits_over_total", string.Join(" ", factory.Coach.Requests.Last().Turns.Select(t => t.Text)));
        await CloseRetryJobsAsync();
    }

    [Fact]
    public async Task A_Plan_draft_still_lands_when_the_other_Aspirations_hold_three_Anchor_habits()
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        await SeedOtherAnchorHabitsAsync(userId, 3);
        var v = await DraftedAsync(c, aspirationId, PlanWithAnchor());
        Assert.NotEqual(Guid.Empty, v);
    }

    [Fact]
    public async Task A_skipped_prep_flags_the_linked_nights_and_tells_the_Coach_without_blame()
    {
        var (c, _, _, _) = await ApprovedAsync(PlanWithPrep());
        var actions = await ActionsAsync(c);
        var prep = actions.Single(a => a.GetProperty("title").GetString() == "Prep dinner");
        var tuesday = actions.Single(a => a.GetProperty("date").GetString() == "2026-01-06" && a.GetProperty("title").GetString() == "Walk after dinner");
        var thursday = actions.Single(a => a.GetProperty("date").GetString() == "2026-01-08" && a.GetProperty("title").GetString() == "Walk after dinner");
        var now = factory.Clock.GetUtcNow().UtcDateTime;

        (await CheckIn(c, prep, "skipped", now)).EnsureSuccessStatusCode();
        var flagged = (await ActionsAsync(c)).Where(a => a.GetProperty("atRisk").GetBoolean()).Select(a => a.GetProperty("id").GetGuid()).ToList();
        Assert.Equal([thursday.GetProperty("id").GetGuid()], flagged); // only the nights after the prep; Tuesday came before it
        Assert.NotEqual(tuesday.GetProperty("id").GetGuid(), flagged[0]);

        await RunJobsAfterAsync(TimeSpan.FromSeconds(6));
        var told = string.Join("\n", factory.Coach.Requests.Where(r => r.Kind == CoachCallKind.CheckInReplyNoNote).SelectMany(r => r.Turns).Select(t => t.Text));
        Assert.Contains("at risk", told);
        Assert.Contains("move the prep", told);

        // Reporting it after all clears the flag.
        (await CheckIn(c, prep, "done", now.AddMinutes(5))).EnsureSuccessStatusCode();
        Assert.DoesNotContain(await ActionsAsync(c), a => a.GetProperty("atRisk").GetBoolean());
    }

    /// <summary>A failed draft would retry later and take other tests' scripted replies: close it.</summary>
    private async Task CloseRetryJobsAsync()
    {
        using var scope = factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<Waypoint.Api.Data.WaypointDb>().Jobs.IgnoreQueryFilters()
            .Where(j => j.Kind == JobKinds.Plan && j.State == Waypoint.Api.Data.JobState.Pending)
            .ExecuteUpdateAsync(u => u.SetProperty(j => j.State, Waypoint.Api.Data.JobState.Failed));
    }

    private async Task RunJobsAfterAsync(TimeSpan by)
    {
        factory.Clock.Advance(by);
        await RunJobsAsync();
    }
}
