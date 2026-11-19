using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>How Anchor habits grow at the Weekly refresh: one at a time, by count. Steps the fake clock, so it has its own factory.</summary>
public sealed class AnchorRefreshTests(WaypointApiFactory factory) : AnchorTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private int AlignToMonday()
    {
        var now = factory.Clock.GetUtcNow();
        var next = now.Date.AddHours(9);
        while (next < now || next.DayOfWeek != DayOfWeek.Monday) next = next.AddDays(1);
        factory.Clock.Advance(next - now.UtcDateTime);
        return DateOnly.FromDateTime(next.AddDays(1)).DayNumber - new DateOnly(2026, 1, 6).DayNumber;
    }

    private void AdvanceTo(DayOfWeek day, int hour, int minute = 0)
    {
        var now = factory.Clock.GetUtcNow();
        var at = now.Date.AddHours(hour).AddMinutes(minute);
        while (at <= now || at.DayOfWeek != day) at = at.AddDays(1);
        factory.Clock.Advance(at - now.UtcDateTime);
    }

    private static string D(int shift, int day) => new DateOnly(2026, 1, 6).AddDays(shift + day).ToString("yyyy-MM-dd");

    /// <summary>An approved first Plan on a full share (3 × 30 min of 1.5 h), with one Anchor habit at 2 a week and a second waiting its turn.</summary>
    private async Task<Guid> ApprovedAsync()
    {
        var shift = AlignToMonday();
        await RunJobsAsync();
        var hand = HandOff(settleFor: "2027-01-01");
        hand["aspiration"]!["hoped_for_date"] = "2026-12-20";
        hand["aspiration"]!["share_hours_per_week"] = 1.5;
        var (c, userId, _, aspirationId) = await ConfirmedAsync(hand);
        var plan = ShiftedPlan(shift);
        plan["tasks"]![1]!["minutes"] = 10;
        var walk = AnchorHabit(times: 2);
        walk["full_times_per_week"] = 3;
        plan["habits"]!.AsArray().Add(walk);
        foreach (var day in new[] { 0, 2, 7, 9 }) plan["first_two_weeks"]!.AsArray().Add(AnchorAct(D(shift, day), "Walk after dinner"));
        var later = AnchorHabit("Eat at home", 3, "weekday dinners");
        later["anchor"]!["days"] = new JsonArray("mon", "tue", "wed", "thu", "fri");
        later["full_times_per_week"] = 5;
        plan["anchor_later"] = new JsonArray(later);
        var v = await DraftedAsync(c, aspirationId, plan);
        (await c.PostAsync($"/plan-versions/{v}/approve", null)).EnsureSuccessStatusCode();
        _ = userId;
        return userId;
    }

    /// <summary>The week goes by with every Action reported as done, then Sunday 18:00 comes and the refresh is accepted; returns the new Plan version.</summary>
    private async Task<JsonElement> RefreshAndAcceptAsync(Guid userId, string state = "done")
    {
        AdvanceTo(DayOfWeek.Sunday, 17, 59);
        var c = await ReauthAsync(userId);
        var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);
        foreach (var a in (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!
                     .Where(a => a.GetProperty("checkIn").ValueKind == JsonValueKind.Null && DateOnly.Parse(a.GetProperty("date").GetString()!) <= today))
            (await c.PutAsJsonAsync("/check-ins", new
            {
                scheduledActionId = a.GetProperty("id").GetGuid(), clientId = "ci-" + Guid.NewGuid().ToString("N"), state,
                tappedAt = factory.Clock.GetUtcNow().UtcDateTime,
            })).EnsureSuccessStatusCode();
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        c = await ReauthAsync(userId);
        var refresh = await c.GetFromJsonAsync<JsonElement>("/weekly-refresh");
        var versionId = refresh.GetProperty("proposals")[0].GetProperty("versionId").GetGuid();
        var version = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{versionId}");
        (await c.PostAsync($"/weekly-refresh/{refresh.GetProperty("id").GetGuid()}/accept", null)).EnsureSuccessStatusCode();
        return version;
    }

    private static string[] Habits(JsonElement v) => v.GetProperty("habits").EnumerateArray().Select(h => h.GetProperty("title").GetString()!).Order().ToArray();
    private static JsonElement[] Codes(JsonElement v) => v.GetProperty("changes").GetProperty("changes").EnumerateArray().ToArray();

    [Fact]
    public async Task A_second_Anchor_habit_joins_one_at_a_time_after_the_first_has_held_two_weeks_and_then_habits_ramp_by_count()
    {
        var userId = await ApprovedAsync();

        var first = await RefreshAndAcceptAsync(userId);
        Assert.Equal(["Easy run", "Walk after dinner"], Habits(first)); // about five days in: not yet held
        Assert.Empty(Codes(first));

        var second = await RefreshAndAcceptAsync(userId);
        Assert.Equal(["Easy run", "Eat at home", "Walk after dinner"], Habits(second));
        var added = Assert.Single(Codes(second));
        Assert.Equal("add_anchor_habit", added.GetProperty("code").GetString());
        Assert.Equal("weekday dinners", second.GetProperty("habits").EnumerateArray().Single(h => h.GetProperty("title").GetString() == "Eat at home").GetProperty("anchor").GetProperty("words").GetString());
        // The new habit is on the timeline at its Anchor's time, not in a Slot, and the Time budget is untouched.
        var c = await ReauthAsync(userId);
        var eat = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Where(a => a.GetProperty("title").GetString() == "Eat at home").ToList();
        Assert.NotEmpty(eat);
        Assert.All(eat, a => Assert.Equal(JsonValueKind.Null, a.GetProperty("slot").ValueKind));

        // Both Anchors are held now (two is the limit) and the queue is empty: the next refresh ramps the walk by one session.
        var third = await RefreshAndAcceptAsync(userId);
        Assert.Equal(["Easy run", "Eat at home", "Walk after dinner"], Habits(third));
        var ramp = Assert.Single(Codes(third));
        Assert.Equal("ramp_session", ramp.GetProperty("code").GetString());
        Assert.Equal("Walk after dinner", ramp.GetProperty("title").GetString());
        Assert.Equal(2, ramp.GetProperty("before").GetInt32());
        Assert.Equal(3, ramp.GetProperty("after").GetInt32());
        Assert.Equal(90, third.GetProperty("habits").EnumerateArray().Where(h => h.GetProperty("anchor").ValueKind == JsonValueKind.Null).Sum(h => h.GetProperty("timesPerWeek").GetInt32() * h.GetProperty("minutes").GetInt32()));
    }

    private static string[] CodesOf(JsonElement v) => Codes(v).Select(c => c.GetProperty("code").GetString()! + ":" + c.GetProperty("title").GetString()).ToArray();

    private async Task<JsonElement> WalkAsync(Guid userId, bool future = true)
    {
        var c = await ReauthAsync(userId);
        var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);
        return (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!
            .First(a => a.GetProperty("title").GetString() == "Walk after dinner" && DateOnly.Parse(a.GetProperty("date").GetString()!) > today);
    }

    [Fact]
    public async Task A_queued_Anchor_habit_waits_when_the_Anchor_habits_across_all_Aspirations_are_already_four()
    {
        var userId = await ApprovedAsync();
        await SeedOtherAnchorHabitsAsync(userId, 3); // with the walk, four are held
        await RefreshAndAcceptAsync(userId);
        var second = await RefreshAndAcceptAsync(userId);
        Assert.Equal(["Easy run", "Walk after dinner"], Habits(second));
        Assert.DoesNotContain("add_anchor_habit:Eat at home", CodesOf(second));
    }

    [Fact]
    public async Task A_queued_Anchor_habit_still_joins_when_the_total_is_one_below_the_limit()
    {
        var userId = await ApprovedAsync();
        await SeedOtherAnchorHabitsAsync(userId, 2); // with the walk, three are held
        await RefreshAndAcceptAsync(userId);
        Assert.Contains("Eat at home", Habits(await RefreshAndAcceptAsync(userId)));
    }

    [Fact]
    public async Task A_third_Anchor_habit_never_joins_an_Aspiration_that_already_holds_two()
    {
        var userId = await ApprovedAsync();
        await RefreshAndAcceptAsync(userId);
        Assert.Contains("Eat at home", Habits(await RefreshAndAcceptAsync(userId)));
        await RefreshAndAcceptAsync(userId);

        // A third waits in the queue; the first two have long since held, so only the per-Aspiration limit stands in its way.
        var third = AnchorHabit("Stretch", 3, "before bed");
        third["full_times_per_week"] = 5;
        await SetQueueAsync(userId, new JsonArray(third));
        var next = await RefreshAndAcceptAsync(userId);
        Assert.Equal(["Easy run", "Eat at home", "Walk after dinner"], Habits(next));
        Assert.DoesNotContain("add_anchor_habit:Stretch", CodesOf(next));
    }

    private async Task SetQueueAsync(Guid userId, JsonArray queue)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        var db = scope.ServiceProvider.GetRequiredService<Waypoint.Api.Data.WaypointDb>();
        (await db.Aspirations.SingleAsync(a => a.ShortName == "Half marathon")).AnchorQueueJson = queue.ToJsonString();
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task After_about_three_weeks_on_target_the_Coach_offers_once_to_stop_the_reminder_and_it_returns_if_the_habit_slips()
    {
        var userId = await ApprovedAsync();
        for (var i = 0; i < 3; i++) Assert.DoesNotContain("offer_stop_reminder:Walk after dinner", CodesOf(await RefreshAndAcceptAsync(userId)));

        var fourth = await RefreshAndAcceptAsync(userId);
        Assert.Contains("offer_stop_reminder:Walk after dinner", CodesOf(fourth));
        Assert.True((await WalkAsync(userId)).GetProperty("reminderOn").GetBoolean()); // an offer changes nothing by itself

        // The user takes the offer.
        var c = await ReauthAsync(userId);
        var habitId = (await WalkAsync(userId)).GetProperty("habitId").GetGuid();
        (await c.PutAsJsonAsync($"/habits/{habitId}/reminder", new { on = false, fromOffer = true })).EnsureSuccessStatusCode();
        Assert.False((await WalkAsync(userId)).GetProperty("reminderOn").GetBoolean());

        // Offered once: the next refresh does not ask again, and the switch survives it.
        var fifth = await RefreshAndAcceptAsync(userId);
        Assert.DoesNotContain("offer_stop_reminder:Walk after dinner", CodesOf(fifth));
        Assert.DoesNotContain("reminder_returns:Walk after dinner", CodesOf(fifth));
        Assert.False((await WalkAsync(userId)).GetProperty("reminderOn").GetBoolean());

        // A week of skipped walks: the habit slipped, so the reminder is back.
        var sixth = await RefreshAndAcceptAsync(userId, state: "skipped");
        Assert.Contains("reminder_returns:Walk after dinner", CodesOf(sixth));
        Assert.True((await WalkAsync(userId)).GetProperty("reminderOn").GetBoolean());
    }

    [Fact]
    public async Task A_reminder_the_user_switched_off_themselves_never_comes_back_on_its_own()
    {
        var userId = await ApprovedAsync();
        await RefreshAndAcceptAsync(userId);
        var c = await ReauthAsync(userId);
        (await c.PutAsJsonAsync($"/habits/{(await WalkAsync(userId)).GetProperty("habitId").GetGuid()}/reminder", new { on = false })).EnsureSuccessStatusCode();
        await RefreshAndAcceptAsync(userId, state: "skipped");
        var again = await RefreshAndAcceptAsync(userId, state: "skipped");
        Assert.DoesNotContain("reminder_returns:Walk after dinner", CodesOf(again));
        Assert.False((await WalkAsync(userId)).GetProperty("reminderOn").GetBoolean());
    }
}
