using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>What the user tells the Coach can bend the Plan, always through one tap of approval; has its own factory and clock.</summary>
public sealed class MessageChangesTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private DateOnly Today => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);

    private int AlignToMonday()
    {
        var now = factory.Clock.GetUtcNow();
        var next = now.Date.AddHours(9);
        while (next < now || next.DayOfWeek != DayOfWeek.Monday) next = next.AddDays(1);
        factory.Clock.Advance(next - now.UtcDateTime);
        return DateOnly.FromDateTime(next.AddDays(1)).DayNumber - new DateOnly(2026, 1, 6).DayNumber;
    }

    private async Task<(HttpClient Client, Guid UserId, Guid AspirationId, int Shift)> ApprovedPlanAsync()
    {
        var shift = AlignToMonday();
        await RunJobsAsync();
        var hand = HandOff(settleFor: "2027-01-01");
        hand["aspiration"]!["hoped_for_date"] = "2026-12-20";
        var (c, userId, _, aspirationId) = await ConfirmedAsync(hand);
        var plan = ShiftedPlan(shift);
        plan["tasks"]![1]!["minutes"] = 10;
        var versionId = await DraftedAsync(c, aspirationId, plan);
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        return (c, userId, aspirationId, shift);
    }

    private async Task SayAsync(HttpClient c, string text, string intent, string clientId)
    {
        factory.Coach.PauseIntent = intent;
        try
        {
            var res = await c.PostAsJsonAsync("/coach/messages", new { text, clientId });
            res.EnsureSuccessStatusCode();
            await res.Content.ReadAsStringAsync();
        }
        finally { factory.Coach.PauseIntent = null; }
    }

    private static async Task<JsonElement[]> ThreadAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement[]>("/coach/messages"))!;
    private static async Task<JsonElement[]> ActionsAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!;
    private static async Task<JsonElement[]> PausesAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement[]>("/pauses"))!;
    private static async Task<double> BudgetAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement>("/me/setup")).GetProperty("timeBudgetHours").GetDouble();

    private static JsonElement Card(JsonElement[] thread, string kind) =>
        thread.Last(m => m.GetProperty("card").GetString() == kind);

    private static string Day(DateOnly d) => d.ToString("yyyy-MM-dd");

    [Fact]
    public async Task Sick_this_week_suggests_a_Pause_that_starts_only_when_the_user_taps_it()
    {
        var (c, userId, aspirationId, _) = await ApprovedPlanAsync();
        var until = Today.AddDays(3);

        await SayAsync(c, "I'm sick this week",
            "{\"pause\": {\"aspirationId\": null, \"span\": \"until\", \"until\": \"" + Day(until) + "\", \"suggest\": true}}", "sick-1");

        // Suggested, not started.
        Assert.Empty(await PausesAsync(c));
        Assert.All(await ActionsAsync(c), a => Assert.False(a.GetProperty("setAside").GetBoolean()));
        var card = Card(await ThreadAsync(c), "pause_suggestion").GetProperty("cardData");
        Assert.Equal(Day(until), card.GetProperty("endsOn").GetString());
        Assert.Equal(JsonValueKind.Null, card.GetProperty("aspirationId").ValueKind);

        // One tap: the phone starts the Pause from the card.
        var res = await c.PostAsJsonAsync("/pauses", new { clientId = card.GetProperty("clientId").GetString(), aspirationId = (Guid?)null, endsOn = card.GetProperty("endsOn").GetString() });
        res.EnsureSuccessStatusCode();
        var pause = Assert.Single(await PausesAsync(await ReauthAsync(userId)));
        Assert.Equal(until.AddDays(1), DateOnly.FromDateTime(pause.GetProperty("endsAt").GetDateTime()));
        Assert.Contains(await ActionsAsync(c), a => a.GetProperty("setAside").GetBoolean());
        Assert.NotEqual(Guid.Empty, aspirationId);
    }

    [Fact]
    public async Task A_Pause_suggestion_can_name_one_Aspiration()
    {
        var (c, _, aspirationId, _) = await ApprovedPlanAsync();
        await SayAsync(c, "Sick, and the half marathon can wait",
            "{\"pause\": {\"aspirationId\": \"" + aspirationId + "\", \"span\": \"open\", \"suggest\": true}}", "sick-2");

        var card = Card(await ThreadAsync(c), "pause_suggestion").GetProperty("cardData");
        Assert.Equal(aspirationId, card.GetProperty("aspirationId").GetGuid());
        Assert.True(card.GetProperty("open").GetBoolean());
        Assert.Empty(await PausesAsync(c));
    }

    [Fact]
    public async Task A_message_that_changes_nothing_gets_no_card_and_queues_no_job()
    {
        var (c, _, _, _) = await ApprovedPlanAsync();
        await RunJobsAsync();
        var before = (await ThreadAsync(c)).Length;

        await SayAsync(c, "Thanks, that helps", "{\"pause\": null}", "chat-plain");

        var thread = await ThreadAsync(c);
        Assert.Equal(before + 2, thread.Length); // the message and the reply, no cards
        Assert.All(thread.Skip(before), m => Assert.Equal(JsonValueKind.Null, m.GetProperty("card").ValueKind));
        Assert.Equal(0, await RunJobsAsync());
    }

    [Fact]
    public async Task Work_is_crazy_this_month_proposes_a_temporary_Time_budget_cut_that_applies_on_approval_and_reverts_at_its_end()
    {
        var (c, userId, aspirationId, _) = await ApprovedPlanAsync();
        await RunJobsAsync();
        var until = Today.AddDays(2); // Wednesday: the cut ends before the first weekly refresh
        var budget = await BudgetAsync(c);
        var minutesBefore = (await ActionsAsync(c)).Where(a => a.GetProperty("habitId").ValueKind != JsonValueKind.Null).Select(a => a.GetProperty("minutes").GetInt32()).Distinct().Single();

        await SayAsync(c, "Work is crazy this month", "{\"budget_cut\": {\"until\": \"" + Day(until) + "\"}}", "crazy-1");

        // Nothing changes before approval: no proposal yet, same Plan, same budget.
        Assert.Equal(JsonValueKind.Null, (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").ValueKind);
        await RunJobsAsync(); // the Coach phrases the proposal
        var proposed = (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").GetGuid();
        var card = Card(await ThreadAsync(c), "proposal").GetProperty("cardData");
        Assert.Equal(proposed, card.GetProperty("planVersionId").GetGuid());
        var view = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{proposed}");
        Assert.Equal("time_budget_cut", view.GetProperty("reason").GetString());
        Assert.True(view.GetProperty("habits")[0].GetProperty("minutes").GetInt32() < minutesBefore);
        Assert.Equal(budget, await BudgetAsync(c));
        Assert.All(await ActionsAsync(c), a => Assert.Equal(a.GetProperty("habitId").ValueKind != JsonValueKind.Null ? minutesBefore : a.GetProperty("minutes").GetInt32(), a.GetProperty("minutes").GetInt32()));

        // One tap: approved as a new Plan version, with the budget cut.
        var approved = await c.PostAsync($"/plan-versions/{proposed}/approve", null);
        approved.EnsureSuccessStatusCode();
        Assert.Equal(2, (await approved.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("number").GetInt32());
        Assert.True(await BudgetAsync(c) < budget);
        Assert.Contains(await ActionsAsync(c), a => a.GetProperty("habitId").ValueKind != JsonValueKind.Null && a.GetProperty("minutes").GetInt32() < minutesBefore);
        await RunJobsAsync();

        // At its end date it reverts, as a new version, with no tap needed.
        factory.Clock.Advance(until.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - factory.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        await RunJobsAsync();
        var c2 = await ReauthAsync(userId);
        var current = await c2.GetFromJsonAsync<JsonElement>($"/plan-versions/{(await PlansAsync(c2, aspirationId)).GetProperty("currentVersionId").GetGuid()}");
        Assert.Equal(3, current.GetProperty("number").GetInt32());
        Assert.Equal("time_budget_restore", current.GetProperty("reason").GetString());
        Assert.Equal(minutesBefore, current.GetProperty("habits")[0].GetProperty("minutes").GetInt32());
        Assert.Equal(budget, await BudgetAsync(c2));
        Assert.Contains((await ActionsAsync(c2)).Where(a => DateOnly.Parse(a.GetProperty("date").GetString()!) > Today && a.GetProperty("habitId").ValueKind != JsonValueKind.Null),
            a => a.GetProperty("minutes").GetInt32() == minutesBefore);
    }

    [Fact]
    public async Task A_retried_restore_gives_the_share_and_the_Time_budget_back_once()
    {
        var (c, userId, aspirationId, _) = await ApprovedPlanAsync();
        await RunJobsAsync();
        var until = Today.AddDays(2);
        var budget = await BudgetAsync(c);
        await SayAsync(c, "Work is crazy this month", "{\"budget_cut\": {\"until\": \"" + Day(until) + "\"}}", "crazy-retry");
        await RunJobsAsync();
        var proposed = (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").GetGuid();
        (await c.PostAsync($"/plan-versions/{proposed}/approve", null)).EnsureSuccessStatusCode();
        await RunJobsAsync();
        factory.Clock.Advance(until.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc) - factory.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        await RunJobsAsync();

        // The job runs again (a retry after a failure past the first save).
        using (var scope = factory.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<WeeklyRefreshRunner>().RestoreAsync(proposed, CancellationToken.None);
        }

        var c2 = await ReauthAsync(userId);
        Assert.Equal(budget, await BudgetAsync(c2));
        using var check = factory.Services.CreateScope();
        var db = check.ServiceProvider.GetRequiredService<WaypointDb>();
        Assert.Equal(1, await db.PlanVersions.IgnoreQueryFilters().CountAsync(p => p.AspirationId == aspirationId && p.Reason == BudgetCuts.RestoreReason));
    }

    [Fact]
    public async Task A_budget_cut_ending_too_soon_or_too_far_off_proposes_nothing()
    {
        var (c, _, aspirationId, _) = await ApprovedPlanAsync();
        await SayAsync(c, "Work is crazy", "{\"budget_cut\": {\"until\": \"" + Day(Today.AddDays(400)) + "\"}}", "crazy-far");
        await SayAsync(c, "Work is crazy today", "{\"budget_cut\": {\"until\": \"" + Day(Today) + "\"}}", "crazy-now");
        await RunJobsAsync();
        Assert.Equal(JsonValueKind.Null, (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").ValueKind);
    }

    [Fact]
    public async Task Resending_a_budget_cut_message_queues_one_proposal()
    {
        var (c, _, aspirationId, _) = await ApprovedPlanAsync();
        await RunJobsAsync();
        var until = Today.AddDays(2);
        var intent = "{\"budget_cut\": {\"until\": \"" + Day(until) + "\"}}";
        await SayAsync(c, "Work is crazy this month", intent, "crazy-2");
        await SayAsync(c, "Work is crazy this month", intent, "crazy-2");

        Assert.True(await RunJobsAsync() >= 1);
        var thread = await ThreadAsync(c);
        Assert.Single(thread, m => m.GetProperty("card").GetString() == "proposal" && m.GetProperty("cardData").GetProperty("reason").GetString() == "time_budget_cut");
        Assert.NotEqual(JsonValueKind.Null, (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").ValueKind);
    }

    [Fact]
    public async Task Make_mornings_shorter_becomes_a_Re_plan_proposal_that_changes_nothing_until_approved()
    {
        var (c, _, aspirationId, shift) = await ApprovedPlanAsync();
        await RunJobsAsync();
        var actionsBefore = (await ActionsAsync(c)).Select(a => (a.GetProperty("id").GetGuid(), a.GetProperty("minutes").GetInt32())).ToList();

        await SayAsync(c, "Can you make mornings shorter?", "{\"replan\": {\"aspirationId\": null}}", "replan-1");
        var shorter = ShiftedPlan(shift);
        foreach (var a in shorter["first_two_weeks"]!.AsArray().Where(a => a!["kind"]!.GetValue<string>() == "habit")) a!["minutes"] = 20;
        shorter["habits"]![0]!["minutes"] = 20;
        shorter["tasks"]![1]!["minutes"] = 10;
        Script(shorter);
        Assert.True(await RunJobsAsync() >= 1);
        await RunJobsAsync();

        var ask = factory.Coach.Requests.Last(r => r.Kind == CoachCallKind.RePlan);
        Assert.Contains("mornings shorter", ask.Turns[0].Text);
        var proposed = (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").GetGuid();
        Assert.Equal(proposed, Card(await ThreadAsync(c), "proposal").GetProperty("cardData").GetProperty("planVersionId").GetGuid());
        var view = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{proposed}");
        Assert.Equal("re_plan", view.GetProperty("reason").GetString());
        Assert.Equal(actionsBefore, (await ActionsAsync(c)).Select(a => (a.GetProperty("id").GetGuid(), a.GetProperty("minutes").GetInt32())).ToList());

        (await c.PostAsync($"/plan-versions/{proposed}/approve", null)).EnsureSuccessStatusCode();
        Assert.Contains(await ActionsAsync(c), a => a.GetProperty("habitId").ValueKind != JsonValueKind.Null && a.GetProperty("minutes").GetInt32() == 20);
    }

    [Fact]
    public async Task The_request_text_never_goes_into_the_job_payload()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        await RunJobsAsync();
        await SayAsync(c, "make mornings shorter please", "{\"replan\": {\"aspirationId\": null}}", "replan-2");

        using var scope = factory.Services.CreateScope();
        var jobs = await scope.ServiceProvider.GetRequiredService<WaypointDb>().Jobs.IgnoreQueryFilters()
            .Where(j => j.UserId == userId && j.Kind == JobKinds.RePlan).ToListAsync();
        Assert.NotEmpty(jobs);
        Assert.All(jobs, j => Assert.DoesNotContain("mornings", j.PayloadJson));
        Assert.All(JsonNode.Parse(jobs[0].PayloadJson)!.AsObject(), p => Assert.True(Guid.TryParse(p.Value!.GetValue<string>(), out _)));
    }
}
