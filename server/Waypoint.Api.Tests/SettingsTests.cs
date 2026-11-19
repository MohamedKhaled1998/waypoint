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

/// <summary>Settings on one page: what each change does and when; has its own factory and clock.</summary>
public sealed class SettingsTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private DateOnly Today => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);

    private async Task<(HttpClient Client, Guid UserId, Guid AspirationId)> ApprovedPlanAsync()
    {
        var now = factory.Clock.GetUtcNow();
        var next = now.Date.AddHours(9);
        while (next < now.UtcDateTime || next.DayOfWeek != DayOfWeek.Monday) next = next.AddDays(1);
        factory.Clock.Advance(next - now.UtcDateTime);
        var shift = DateOnly.FromDateTime(next.AddDays(1)).DayNumber - new DateOnly(2026, 1, 6).DayNumber;
        await RunJobsAsync();
        var hand = HandOff(settleFor: "2027-01-01");
        hand["aspiration"]!["hoped_for_date"] = "2026-12-20";
        var (c, userId, _, aspirationId) = await ConfirmedAsync(hand);
        var plan = ShiftedPlan(shift);
        plan["tasks"]![1]!["minutes"] = 10;
        var versionId = await DraftedAsync(c, aspirationId, plan);
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        await RunJobsAsync();
        return (c, userId, aspirationId);
    }

    private static Task<JsonElement> SettingsAsync(HttpClient c) => c.GetFromJsonAsync<JsonElement>("/settings");
    private static async Task<JsonElement[]> ActionsAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!;

    [Fact]
    public async Task A_Time_budget_change_proposes_a_Re_plan_and_changes_nothing_until_approved()
    {
        var (c, _, aspirationId) = await ApprovedPlanAsync();
        var before = await SettingsAsync(c);
        Assert.Equal(5, before.GetProperty("timeBudgetHours").GetDouble());
        var minutesBefore = (await ActionsAsync(c)).Where(a => a.GetProperty("habitId").ValueKind != JsonValueKind.Null).Select(a => a.GetProperty("minutes").GetInt32()).Distinct().Single();

        var put = await c.PutAsJsonAsync("/settings/time-budget", new { hours = 3.0 });
        put.EnsureSuccessStatusCode();
        // Not a direct change: the budget, the split and the Actions stay as they were.
        var after = await SettingsAsync(c);
        Assert.Equal(5, after.GetProperty("timeBudgetHours").GetDouble());
        Assert.Equal(3, after.GetProperty("requestedTimeBudgetHours").GetDouble());
        Assert.Equal(3, after.GetProperty("split")[0].GetProperty("hours").GetDouble());
        Assert.Equal(JsonValueKind.Null, (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").ValueKind);

        await RunJobsAsync(); // the Coach proposes the Re-plan
        var proposed = (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").GetGuid();
        var view = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{proposed}");
        Assert.Equal("time_budget_change", view.GetProperty("reason").GetString());
        Assert.True(view.GetProperty("habits")[0].GetProperty("minutes").GetInt32() < minutesBefore);
        Assert.Equal(5, (await SettingsAsync(c)).GetProperty("timeBudgetHours").GetDouble());

        (await c.PostAsync($"/plan-versions/{proposed}/approve", null)).EnsureSuccessStatusCode();
        var approved = await SettingsAsync(c);
        Assert.Equal(3, approved.GetProperty("timeBudgetHours").GetDouble());
        Assert.Equal(JsonValueKind.Null, approved.GetProperty("requestedTimeBudgetHours").ValueKind);
        Assert.Equal(1.8, approved.GetProperty("split")[0].GetProperty("hours").GetDouble(), 2);
    }

    [Fact]
    public async Task Asking_for_the_current_budget_again_withdraws_the_request_and_its_waiting_proposals()
    {
        var (c, _, aspirationId) = await ApprovedPlanAsync();
        (await c.PutAsJsonAsync("/settings/time-budget", new { hours = 3.0 })).EnsureSuccessStatusCode();
        await RunJobsAsync();
        var proposed = (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").GetGuid();

        (await c.PutAsJsonAsync("/settings/time-budget", new { hours = 5.0 })).EnsureSuccessStatusCode();
        Assert.Equal(JsonValueKind.Null, (await SettingsAsync(c)).GetProperty("requestedTimeBudgetHours").ValueKind);
        Assert.Equal(JsonValueKind.Null, (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").ValueKind);

        // The old proposal can no longer move anything.
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/plan-versions/{proposed}/approve", null)).StatusCode);
        var after = await SettingsAsync(c);
        Assert.Equal(5, after.GetProperty("timeBudgetHours").GetDouble());
        Assert.Equal(3, after.GetProperty("split")[0].GetProperty("hours").GetDouble());
    }

    [Fact]
    public async Task A_request_whose_proposals_were_replaced_is_cleared_and_never_applied_later()
    {
        var (c, userId, aspirationId) = await ApprovedPlanAsync();
        (await c.PutAsJsonAsync("/settings/time-budget", new { hours = 3.0 })).EnsureSuccessStatusCode();
        await RunJobsAsync();
        Assert.NotEqual(JsonValueKind.Null, (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").ValueKind);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
            var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
            await Settings.ReplaceWaitingBudgetProposalsAsync(db, CancellationToken.None); // what a newer proposal does to them
            await Settings.ClearStaleBudgetRequestAsync(db, CancellationToken.None);
        }
        var view = await SettingsAsync(c);
        Assert.Equal(JsonValueKind.Null, view.GetProperty("requestedTimeBudgetHours").ValueKind);
        Assert.Equal(5, view.GetProperty("timeBudgetHours").GetDouble());
    }

    [Fact]
    public async Task A_new_Slot_waiting_for_the_refresh_can_be_replaced_and_taken_back()
    {
        var (c, _, _) = await ApprovedPlanAsync();
        (await c.PostAsJsonAsync("/settings/slots", new { label = "Saturday", days = new[] { "sat" }, start = "09:00", end = "10:00" })).EnsureSuccessStatusCode();
        var id = (await SettingsAsync(c)).GetProperty("newSlots")[0].GetProperty("id").GetGuid();

        (await c.PutAsJsonAsync($"/settings/slots/new/{id}", new { label = "Saturday", days = new[] { "sat", "sun" }, start = "09:15", end = "10:15" })).EnsureSuccessStatusCode();
        var news = (await SettingsAsync(c)).GetProperty("newSlots");
        Assert.Equal(1, news.GetArrayLength());
        Assert.Equal("09:15", news[0].GetProperty("start").GetString());
        Assert.Equal(2, news[0].GetProperty("days").GetArrayLength());

        (await c.DeleteAsync($"/settings/slots/new/{id}")).EnsureSuccessStatusCode();
        Assert.Equal(0, (await SettingsAsync(c)).GetProperty("newSlots").GetArrayLength());
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/settings/slots/new/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_Slot_edit_waits_for_the_next_weekly_refresh_and_a_new_Slot_joins_then_too()
    {
        var (c, userId, _) = await ApprovedPlanAsync();
        var slot = (await SettingsAsync(c)).GetProperty("slots")[0];
        var id = slot.GetProperty("id").GetGuid();

        (await c.PutAsJsonAsync($"/settings/slots/{id}", new { days = new[] { "mon", "wed" }, start = "08:15", end = "09:15" })).EnsureSuccessStatusCode();
        (await c.PostAsJsonAsync("/settings/slots", new { label = "Saturday", days = new[] { "sat" }, start = "09:00", end = "24:00" })).EnsureSuccessStatusCode();

        var waiting = await SettingsAsync(c);
        var same = waiting.GetProperty("slots").EnumerateArray().Single();
        Assert.Equal("07:00", same.GetProperty("start").GetString());
        Assert.Equal(3, same.GetProperty("days").GetArrayLength());
        Assert.Equal("08:15", same.GetProperty("pending").GetProperty("start").GetString());
        Assert.Equal(1, waiting.GetProperty("newSlots").GetArrayLength());

        // The refresh runs on its day and time; from then on the edits stand.
        var next = WeeklyRefreshes.NextRun(new User { GoogleSub = "x", RefreshDay = DayOfWeek.Sunday, RefreshTime = "18:00" }, factory.Clock.GetUtcNow().UtcDateTime);
        factory.Clock.Advance(next - factory.Clock.GetUtcNow().UtcDateTime + TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        var c2 = await ReauthAsync(userId);
        var applied = await SettingsAsync(c2);
        var slots = applied.GetProperty("slots").EnumerateArray().ToList();
        Assert.Equal(2, slots.Count);
        var edited = slots.Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal("08:15", edited.GetProperty("start").GetString());
        Assert.Equal(["mon", "wed"], edited.GetProperty("days").EnumerateArray().Select(d => d.GetString()));
        Assert.Equal(JsonValueKind.Null, edited.GetProperty("pending").ValueKind);
        Assert.Equal(0, applied.GetProperty("newSlots").GetArrayLength());
        Assert.Equal("23:59", slots.Single(x => x.GetProperty("id").GetGuid() != id).GetProperty("end").GetString());
    }

    [Theory]
    [InlineData("04:45", "08:00")] // before 05:00
    [InlineData("07:10", "08:00")] // off the 15-minute grid
    [InlineData("08:00", "08:00")] // empty
    [InlineData("09:00", "08:00")] // backwards
    [InlineData("08:00", "24:15")]
    public async Task A_Slot_off_the_track_is_refused(string start, string end)
    {
        var (c, _, _) = await ApprovedPlanAsync();
        var res = await c.PostAsJsonAsync("/settings/slots", new { label = "x", days = new[] { "mon" }, start, end });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, (await SettingsAsync(c)).GetProperty("newSlots").GetArrayLength());
    }

    [Fact]
    public async Task Contact_frequency_and_quiet_hours_set_in_Settings_are_what_the_gate_uses()
    {
        var (c, userId, aspirationId) = await ApprovedPlanAsync();
        (await c.PutAsJsonAsync("/settings/contact-frequency", new { value = "Chatty" })).EnsureSuccessStatusCode();
        (await c.PutAsJsonAsync("/settings/quiet-hours", new { start = "22:00", end = "07:00" })).EnsureSuccessStatusCode();
        var now = factory.Clock.GetUtcNow().UtcDateTime;
        var at = now.Date.AddHours(23); // inside the span, which runs over midnight
        if (at < now) at = at.AddDays(1);
        factory.Clock.Advance(at - now);

        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        var gate = scope.ServiceProvider.GetRequiredService<ContactGate>();
        var decision = await gate.DecideAsync(new UnpromptedRequest(UnpromptedKind.CoachCheckIn, aspirationId), CancellationToken.None);
        var hold = Assert.IsType<GateDecision.Hold>(decision);
        Assert.Equal(at.Date.AddDays(1).AddHours(7), hold.Until);

        var settings = await SettingsAsync(await ReauthAsync(userId));
        Assert.Equal("Chatty", settings.GetProperty("contactFrequency").GetString());
        Assert.Equal("22:00", settings.GetProperty("quietStart").GetString());
    }

    [Theory]
    [InlineData("Loud")]
    [InlineData("")]
    public async Task An_unknown_Contact_frequency_is_refused(string value)
    {
        var (c, _, _) = await ApprovedPlanAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/settings/contact-frequency", new { value })).StatusCode);
    }

    [Theory]
    [InlineData("22:10", "07:00")]
    [InlineData("07:00", "07:00")]
    public async Task Quiet_hours_off_the_grid_or_empty_are_refused(string start, string end)
    {
        var (c, _, _) = await ApprovedPlanAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/settings/quiet-hours", new { start, end })).StatusCode);
    }

    [Fact]
    public async Task Changing_the_weekly_refresh_day_and_time_moves_the_booked_refresh_and_exact_alarms_are_remembered()
    {
        var (c, userId, _) = await ApprovedPlanAsync();
        (await c.PutAsJsonAsync("/settings/weekly-refresh", new { day = "friday", time = "19:30" })).EnsureSuccessStatusCode();
        (await c.PutAsJsonAsync("/settings/exact-alarms", new { allowed = false })).EnsureSuccessStatusCode();
        var settings = await SettingsAsync(c);
        Assert.Equal("friday", settings.GetProperty("refreshDay").GetString());
        Assert.Equal("19:30", settings.GetProperty("refreshTime").GetString());
        Assert.False(settings.GetProperty("exactAlarmsAllowed").GetBoolean());

        // The next refresh opens on Friday evening, not the Sunday it was booked for.
        var friday = factory.Clock.GetUtcNow().UtcDateTime;
        while (friday.DayOfWeek != DayOfWeek.Friday || friday.TimeOfDay > new TimeSpan(19, 30, 0)) friday = friday.Date.AddDays(1).AddHours(0);
        factory.Clock.Advance(friday.Date.AddHours(19).AddMinutes(31) - factory.Clock.GetUtcNow().UtcDateTime);
        await RunJobsAsync();
        var refresh = await (await ReauthAsync(userId)).GetAsync("/weekly-refresh");
        Assert.NotEqual(HttpStatusCode.NotFound, refresh.StatusCode);
    }
}
