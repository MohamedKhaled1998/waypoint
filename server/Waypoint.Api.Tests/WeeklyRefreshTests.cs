using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>Steps the fake clock through weekly refreshes, so it has its own factory and clock.</summary>
public sealed class WeeklyRefreshTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    /// <summary>Moves the clock to the next Monday 09:00 and returns how many days that puts the plan's start (Tuesday) from 2026-01-06.</summary>
    private int AlignToMonday()
    {
        var now = factory.Clock.GetUtcNow();
        var next = now.Date.AddHours(9);
        while (next < now || next.DayOfWeek != DayOfWeek.Monday) next = next.AddDays(1);
        factory.Clock.Advance(next - now.UtcDateTime);
        return (DateOnly.FromDateTime(next.AddDays(1)).DayNumber - new DateOnly(2026, 1, 6).DayNumber);
    }

    private void AdvanceTo(DayOfWeek day, int hour, int minute = 0)
    {
        var now = factory.Clock.GetUtcNow();
        var at = now.Date.AddHours(hour).AddMinutes(minute);
        while (at <= now || at.DayOfWeek != day) at = at.AddDays(1);
        factory.Clock.Advance(at - now.UtcDateTime);
    }

    /// <summary>A signed-in user with an approved first Plan starting tomorrow (Tuesday); share in hours a week.</summary>
    private async Task<(HttpClient Client, Guid UserId, Guid AspirationId, int Shift)> ApprovedPlanAsync(double shareHours = 3)
    {
        var shift = AlignToMonday();
        await RunJobsAsync(); // earlier tests' Check-in replies and refreshes come due as the clock moves; they must not take this Plan's scripted reply
        var hand = HandOff(settleFor: "2027-01-01");
        hand["aspiration"]!["hoped_for_date"] = "2026-12-20";
        hand["aspiration"]!["share_hours_per_week"] = shareHours;
        var (c, userId, _, aspirationId) = await ConfirmedAsync(hand);
        var plan = ShiftedPlan(shift);
        plan["tasks"]![1]!["minutes"] = 10; // small enough to fit beside a 30-minute run in a 45-minute Slot
        var versionId = await DraftedAsync(c, aspirationId, plan);
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        return (c, userId, aspirationId, shift);
    }

    private async Task<JsonElement?> CurrentRefreshAsync(Guid userId)
    {
        var c = await ReauthAsync(userId);
        var res = await c.GetAsync("/weekly-refresh");
        return res.StatusCode == System.Net.HttpStatusCode.NotFound ? null : await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task The_refresh_runs_at_the_users_day_and_time_and_lists_unreported_Actions_first()
    {
        var (_, userId, _, _) = await ApprovedPlanAsync();

        AdvanceTo(DayOfWeek.Sunday, 17, 59);
        await RunJobsAsync();
        Assert.Null(await CurrentRefreshAsync(userId));

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        var refresh = (await CurrentRefreshAsync(userId))!.Value;

        Assert.Equal("unreported", refresh.EnumerateObject().First().Name);
        Assert.Equal(2, refresh.GetProperty("unreported").GetArrayLength());
        Assert.Equal(1, refresh.GetProperty("proposals").GetArrayLength());
    }

    private async Task CheckInAsync(Guid userId, string state, Func<DateOnly, bool> inRange, int? minutes = null)
    {
        var c = await ReauthAsync(userId);
        var actions = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!;
        foreach (var a in actions.Where(a => a.GetProperty("checkIn").ValueKind == JsonValueKind.Null && inRange(DateOnly.Parse(a.GetProperty("date").GetString()!))))
        {
            var res = await c.PutAsJsonAsync("/check-ins", new
            {
                scheduledActionId = a.GetProperty("id").GetGuid(), clientId = "ci-" + Guid.NewGuid().ToString("N"), state, minutes,
                tappedAt = factory.Clock.GetUtcNow().UtcDateTime,
            });
            res.EnsureSuccessStatusCode();
        }
    }

    private async Task<JsonElement> VersionAsync(Guid userId, Guid id) =>
        await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement>($"/plan-versions/{id}");

    /// <summary>The week goes by with every Action reported as <paramref name="state"/> (none when null), then Sunday 18:00 comes.</summary>
    private async Task<JsonElement> RefreshAfterAsync(Guid userId, string? state)
    {
        AdvanceTo(DayOfWeek.Sunday, 17, 59);
        if (state is not null)
        {
            var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);
            await CheckInAsync(userId, state, d => d <= today);
        }
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        return (await CurrentRefreshAsync(userId))!.Value;
    }

    private static Guid ProposalOf(JsonElement refresh) => refresh.GetProperty("proposals")[0].GetProperty("versionId").GetGuid();

    private async Task<JsonElement> AcceptAsync(Guid userId, JsonElement refresh)
    {
        var res = await (await ReauthAsync(userId)).PostAsync($"/weekly-refresh/{refresh.GetProperty("id").GetGuid()}/accept", null);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    [Fact]
    public async Task Two_mostly_done_weeks_ramp_one_notch_and_otherwise_the_Plan_holds()
    {
        var (_, userId, _, _) = await ApprovedPlanAsync();

        // One week of history is not two mostly-done weeks.
        var first = await RefreshAfterAsync(userId, "done");
        var v2 = await VersionAsync(userId, ProposalOf(first));
        Assert.Equal("weekly_refresh", v2.GetProperty("reason").GetString());
        Assert.Equal(1, v2.GetProperty("replacesNumber").GetInt32()); // version 1 stays in the Plan's history
        Assert.Equal(30, v2.GetProperty("habits")[0].GetProperty("minutes").GetInt32());
        Assert.Equal(0, v2.GetProperty("changes").GetProperty("changes").GetArrayLength());
        // The window rolled: a task whose date the window reached is planned now, and the Milestones stay as they were.
        Assert.Contains(v2.GetProperty("twoWeeks").EnumerateArray(), a => a.GetProperty("action").GetString() == "Book a physio visit");
        Assert.Equal(2, v2.GetProperty("changes").GetProperty("stays").EnumerateArray().Count(s => s.GetProperty("what").GetString() == "milestone"));
        await AcceptAsync(userId, first);

        var second = await RefreshAfterAsync(userId, "done");
        var v3 = await VersionAsync(userId, ProposalOf(second));
        // Three sessions already fill the three Slot days, so the notch is a few more minutes.
        Assert.Equal(35, v3.GetProperty("habits")[0].GetProperty("minutes").GetInt32());
        var change = v3.GetProperty("changes").GetProperty("changes")[0];
        Assert.Equal(30, change.GetProperty("before").GetInt32());
        Assert.Equal(35, change.GetProperty("after").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(change.GetProperty("reason").GetString()));
        Assert.All(v3.GetProperty("twoWeeks").EnumerateArray().Where(a => a.GetProperty("kind").GetString() == "habit"),
            a => Assert.Equal(35, a.GetProperty("minutes").GetInt32()));

        // A week skipped is not mostly done: the next one holds at 35.
        await AcceptAsync(userId, second);
        var third = await RefreshAfterAsync(userId, "skipped");
        var v4 = await VersionAsync(userId, ProposalOf(third));
        Assert.Equal(35, v4.GetProperty("habits")[0].GetProperty("minutes").GetInt32());
        Assert.Equal(0, v4.GetProperty("changes").GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task The_ramp_stops_at_the_full_share()
    {
        // 1.6 h is 96 minutes a week: three 30-minute sessions leave room for two more minutes each, and then it is full.
        var (_, userId, _, _) = await ApprovedPlanAsync(shareHours: 1.6);
        await CheckInAsync(userId, "done", _ => true);
        await AcceptAsync(userId, await RefreshAfterAsync(userId, "done"));
        var second = await RefreshAfterAsync(userId, "done");
        var v3 = await VersionAsync(userId, ProposalOf(second));
        Assert.Equal(32, v3.GetProperty("habits")[0].GetProperty("minutes").GetInt32());
        await AcceptAsync(userId, second);

        var third = await RefreshAfterAsync(userId, "done");
        var v4 = await VersionAsync(userId, ProposalOf(third));
        Assert.Equal(32, v4.GetProperty("habits")[0].GetProperty("minutes").GetInt32());
        Assert.Equal(0, v4.GetProperty("changes").GetProperty("changes").GetArrayLength());
    }

    [Fact]
    public async Task An_unanswered_proposal_applies_itself_after_a_day_as_a_new_version()
    {
        var (c, userId, aspirationId, _) = await ApprovedPlanAsync();
        var refresh = await RefreshAfterAsync(userId, null);
        var proposed = ProposalOf(refresh);
        Assert.Equal("proposed", (await VersionAsync(userId, proposed)).GetProperty("status").GetString());

        factory.Clock.Advance(TimeSpan.FromHours(23));
        await RunJobsAsync();
        Assert.Equal("proposed", (await VersionAsync(userId, proposed)).GetProperty("status").GetString());

        factory.Clock.Advance(TimeSpan.FromHours(1));
        await RunJobsAsync();
        var applied = await VersionAsync(userId, proposed);
        Assert.Equal("approved", applied.GetProperty("status").GetString());
        Assert.Equal(2, applied.GetProperty("number").GetInt32());
        Assert.True(applied.GetProperty("autoApplied").GetBoolean());
        Assert.Equal("auto_applied", (await CurrentRefreshAsync(userId))!.Value.GetProperty("status").GetString());
        var plans = await PlansAsync(await ReauthAsync(userId), aspirationId);
        Assert.Equal(proposed, plans.GetProperty("currentVersionId").GetGuid());
    }

    [Fact]
    public async Task Looks_good_accepts_the_proposal_and_one_push_goes_out_at_the_chosen_time()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        var token = "tok-" + Guid.NewGuid();
        (await c.PutAsJsonAsync("/me/push-token", new { token })).EnsureSuccessStatusCode();
        var refresh = await RefreshAfterAsync(userId, null);
        await RunJobsAsync();

        var push = Assert.Single(factory.Push.Sent, m => m.Token == token && m.Data["route"] == "weekly_refresh");
        Assert.Equal("weekly_refresh", push.Data["route"]);
        Assert.Equal(refresh.GetProperty("id").GetString(), push.Data["refreshId"]);
        Assert.Equal(ProposalOf(refresh).ToString(), push.Data["planVersionId"]);

        var accepted = await AcceptAsync(userId, refresh);
        Assert.Equal("accepted", accepted.GetProperty("status").GetString());
        Assert.Equal("approved", (await VersionAsync(userId, ProposalOf(refresh))).GetProperty("status").GetString());

        // Ignored or answered, it does not ask again, and the day-later job finds nothing to apply.
        factory.Clock.Advance(TimeSpan.FromDays(1));
        await RunJobsAsync();
        Assert.Single(factory.Push.Sent, m => m.Token == token && m.Data["route"] == "weekly_refresh");
        Assert.False((await VersionAsync(userId, ProposalOf(refresh))).GetProperty("autoApplied").GetBoolean());
    }

    [Fact]
    public async Task Did_it_from_the_list_records_a_late_Check_in_in_the_week_the_Action_belongs_to()
    {
        var (_, userId, _, _) = await ApprovedPlanAsync();
        AdvanceTo(DayOfWeek.Sunday, 17, 58);
        var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);
        await CheckInAsync(userId, "done", d => d <= today); // everything done...
        var c = await ReauthAsync(userId);
        // ...but undo the Wednesday run, so it is reported only from the refresh list.
        var actions = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!;
        var wednesday = actions.First(a => a.GetProperty("title").GetString() == "Easy run" && DateOnly.Parse(a.GetProperty("date").GetString()!).DayOfWeek == DayOfWeek.Wednesday);
        (await c.DeleteAsync($"/scheduled-actions/{wednesday.GetProperty("id").GetGuid()}/check-in")).EnsureSuccessStatusCode();

        var refresh = await RefreshAfterAsync(userId, null);
        var listed = Assert.Single(refresh.GetProperty("unreported").EnumerateArray());
        Assert.Equal(wednesday.GetProperty("id").GetGuid(), listed.GetProperty("id").GetGuid());

        // A day late, from the list.
        factory.Clock.Advance(TimeSpan.FromHours(3));
        c = await ReauthAsync(userId);
        (await c.PutAsJsonAsync("/check-ins", new
        {
            scheduledActionId = listed.GetProperty("id").GetGuid(), clientId = "late-1", state = "done", tappedAt = factory.Clock.GetUtcNow().UtcDateTime,
        })).EnsureSuccessStatusCode();
        Assert.Empty((await CurrentRefreshAsync(userId))!.Value.GetProperty("unreported").EnumerateArray());
        var after = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Single(a => a.GetProperty("id").GetGuid() == listed.GetProperty("id").GetGuid());
        Assert.Equal("done", after.GetProperty("checkIn").GetProperty("state").GetString());
        Assert.Equal(wednesday.GetProperty("date").GetString(), after.GetProperty("date").GetString());
    }

    [Fact]
    public async Task When_the_Coach_cannot_answer_the_refresh_is_late_and_nothing_is_faked()
    {
        var (_, userId, _, _) = await ApprovedPlanAsync();
        factory.Coach.Fail = true;
        try
        {
            var late = await RefreshAfterAsync(userId, null);
            Assert.Fail("a refresh opened without the Coach: " + late);
        }
        catch (InvalidOperationException) { } // no refresh to read: the current one is still none
        catch (Xunit.Sdk.XunitException) { throw; }
        finally { factory.Coach.Fail = false; }
        Assert.Null(await CurrentRefreshAsync(userId));

        factory.Clock.Advance(TimeSpan.FromMinutes(2)); // backoff of the first retry is a minute
        await RunJobsAsync();
        Assert.NotNull(await CurrentRefreshAsync(userId));
    }
}
