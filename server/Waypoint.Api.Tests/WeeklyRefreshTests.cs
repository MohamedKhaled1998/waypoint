using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Coach;
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
        while (next < now.UtcDateTime || next.DayOfWeek != DayOfWeek.Monday) next = next.AddDays(1);
        factory.Clock.Advance(next - now.UtcDateTime);
        return (DateOnly.FromDateTime(next.AddDays(1)).DayNumber - new DateOnly(2026, 1, 6).DayNumber);
    }

    private void AdvanceTo(DayOfWeek day, int hour, int minute = 0)
    {
        var now = factory.Clock.GetUtcNow();
        var at = now.Date.AddHours(hour).AddMinutes(minute);
        while (at <= now.UtcDateTime || at.DayOfWeek != day) at = at.AddDays(1);
        factory.Clock.Advance(at - now.UtcDateTime);
    }

    /// <summary>A signed-in user with an approved first Plan starting tomorrow (Tuesday); share in hours a week.</summary>
    private async Task<(HttpClient Client, Guid UserId, Guid AspirationId, int Shift)> ApprovedPlanAsync(double shareHours = 3, object? guides = null)
    {
        var shift = AlignToMonday();
        await RunJobsAsync(); // earlier tests' Check-in replies and refreshes come due as the clock moves; they must not take this Plan's scripted reply
        if (guides is not null) { factory.Coach.GuideScript.Clear(); factory.Coach.EnqueueGuides(guides); } // after the flush, so nothing else takes it
        var hand = HandOff(settleFor: "2027-01-01");
        hand["aspiration"]!["hoped_for_date"] = "2026-12-20";
        hand["aspiration"]!["share_hours_per_week"] = shareHours;
        var (c, userId, _, aspirationId) = await ConfirmedAsync(hand);
        var plan = ShiftedPlan(shift);
        plan["tasks"]![1]!["minutes"] = 10; // small enough to fit beside a 30-minute run in a 45-minute Slot
        plan["tasks"]![0]!["minutes"] = 10;
        plan["first_two_weeks"]![4]!["minutes"] = 10;
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

        // A week skipped is not mostly done, and with the week before it is under 70% over two weeks: one notch back.
        await AcceptAsync(userId, second);
        var third = await RefreshAfterAsync(userId, "skipped");
        var v4 = await VersionAsync(userId, ProposalOf(third));
        Assert.Equal(30, v4.GetProperty("habits")[0].GetProperty("minutes").GetInt32());
        Assert.Equal("ease_back", v4.GetProperty("changes").GetProperty("changes")[0].GetProperty("code").GetString());
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

    [Fact]
    public async Task Tolerance_bands_sit_at_seventy_and_thirty_percent()
    {
        Assert.Equal(Tolerance.Band.Absorb, Tolerance.Measure([(7, 10)]));
        Assert.Equal(Tolerance.Band.EaseBack, Tolerance.Measure([(6, 10)]));
        Assert.Equal(Tolerance.Band.Absorb, Tolerance.Measure([(0, 0), (0, 0)])); // nothing planned: no evidence
        Assert.Equal(Tolerance.Band.EaseBack, Tolerance.Measure([(3, 10), (3, 10)])); // 30% is not under 30%
        Assert.Equal(Tolerance.Band.Rethink, Tolerance.Measure([(2, 10), (2, 10)]));
        Assert.Equal(Tolerance.Band.EaseBack, Tolerance.Measure([(0, 10), (10, 10)])); // one bad week alone is not a re-think
        Assert.Equal(Tolerance.Band.EaseBack, Tolerance.Measure([(2, 10), (5, 10)]));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Re_think_opens_a_short_conversation_and_leaves_the_Plan_as_it_was()
    {
        var (_, userId, aspirationId, _) = await ApprovedPlanAsync();
        var first = await RefreshAfterAsync(userId, "skipped");
        var before = (await VersionAsync(userId, ProposalOf(first))).GetProperty("habits")[0].GetProperty("minutes").GetInt32(); // one bad week eased back already
        await AcceptAsync(userId, first);
        factory.Coach.Enqueue("A lot has been going on lately. What changed for you?");
        var second = await RefreshAfterAsync(userId, "skipped");
        await RunJobsAsync();

        var v3 = await VersionAsync(userId, ProposalOf(second));
        Assert.Equal(before, v3.GetProperty("habits")[0].GetProperty("minutes").GetInt32());
        Assert.DoesNotContain(Codes(v3, "changes"), c => c.GetProperty("what").GetString() == "habit");
        var thread = (await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/coach/messages"))!;
        var opener = Assert.Single(thread, m => m.TryGetProperty("card", out var c) && c.GetString() == "rethink");
        Assert.Equal("coach", opener.GetProperty("role").GetString());
        Assert.Equal(aspirationId, opener.GetProperty("aspirationId").GetGuid());
    }

    private async Task CheckInTitleAsync(Guid userId, string title, string state, int? minutes = null)
    {
        var c = await ReauthAsync(userId);
        var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);
        foreach (var a in (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Where(a =>
            a.GetProperty("title").GetString() == title && a.GetProperty("checkIn").ValueKind == JsonValueKind.Null && DateOnly.Parse(a.GetProperty("date").GetString()!) <= today))
            (await c.PutAsJsonAsync("/check-ins", new
            {
                scheduledActionId = a.GetProperty("id").GetGuid(), clientId = "ci-" + Guid.NewGuid().ToString("N"), state, minutes,
                tappedAt = factory.Clock.GetUtcNow().UtcDateTime,
            })).EnsureSuccessStatusCode();
    }

    /// <summary>A week where everything is done except one titled Action, which gets <paramref name="state"/>; then the refresh.</summary>
    private async Task<JsonElement> RefreshWithOneAsync(Guid userId, string title, string state, int? minutes = null, string rest = "done", int? restMinutes = null)
    {
        AdvanceTo(DayOfWeek.Sunday, 17, 59);
        await CheckInTitleAsync(userId, title, state, minutes);
        var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);
        await CheckInAsync(userId, rest, d => d <= today, restMinutes);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        return (await CurrentRefreshAsync(userId))!.Value;
    }

    private static IEnumerable<JsonElement> Codes(JsonElement version, string list) =>
        version.GetProperty("changes").GetProperty(list).EnumerateArray();

    [Fact]
    public async Task A_skipped_task_moves_to_the_next_free_Slot_with_a_mention_and_slipping_twice_offers_to_split_it()
    {
        var (_, userId, _, _) = await ApprovedPlanAsync();
        // The shoes join the window at the first refresh, so the first week they can be missed is the second.
        await AcceptAsync(userId, await RefreshAfterAsync(userId, "done"));
        var first = await RefreshWithOneAsync(userId, "Buy running shoes", "skipped", rest: "partly", restMinutes: 20); // partly counts as done, but is no ramp
        var v2 = await VersionAsync(userId, ProposalOf(first));
        var moved = Assert.Single(Codes(v2, "changes"), c => c.GetProperty("code").GetString() == "task_moved");
        Assert.Equal("Buy running shoes", moved.GetProperty("title").GetString());
        Assert.False(string.IsNullOrWhiteSpace(moved.GetProperty("reason").GetString()));
        var again = Assert.Single(v2.GetProperty("twoWeeks").EnumerateArray(), a => a.GetProperty("action").GetString() == "Buy running shoes");
        Assert.True(DateOnly.Parse(again.GetProperty("date").GetString()!) >= DateOnly.Parse(v2.GetProperty("startDate").GetString()!));
        Assert.Empty(Codes(v2, "offers")); // one slip is only a move
        await AcceptAsync(userId, first);

        var second = await RefreshWithOneAsync(userId, "Buy running shoes", "skipped", rest: "partly", restMinutes: 20); // partly counts as done, but is no ramp
        var v3 = await VersionAsync(userId, ProposalOf(second));
        Assert.Equal("split_task", Assert.Single(Codes(v3, "offers")).GetProperty("code").GetString());
        Assert.Contains(v3.GetProperty("twoWeeks").EnumerateArray(), a => a.GetProperty("action").GetString() == "Buy running shoes");
    }

    [Fact]
    public async Task Three_Partly_in_a_row_on_a_habit_offer_a_shorter_version_at_the_next_refresh()
    {
        var (_, userId, _, _) = await ApprovedPlanAsync();
        var first = await RefreshWithOneAsync(userId, "Easy run", "partly", minutes: 20);
        Assert.DoesNotContain(Codes(await VersionAsync(userId, ProposalOf(first)), "changes"), c => c.GetProperty("code").GetString() == "shorter_version"); // two so far
        await AcceptAsync(userId, first);

        var second = await RefreshWithOneAsync(userId, "Easy run", "partly", minutes: 20);
        var v3 = await VersionAsync(userId, ProposalOf(second));
        var shorter = Assert.Single(Codes(v3, "changes"), c => c.GetProperty("code").GetString() == "shorter_version");
        Assert.Equal(30, shorter.GetProperty("before").GetInt32());
        Assert.Equal(20, shorter.GetProperty("after").GetInt32());
        Assert.Equal(20, v3.GetProperty("habits")[0].GetProperty("minutes").GetInt32());
    }

    /// <summary>The week goes by with the first <paramref name="skips"/> Easy runs skipped and everything else done; then the refresh.</summary>
    private async Task<JsonElement> RefreshSkippingRunsAsync(Guid userId, int skips)
    {
        AdvanceTo(DayOfWeek.Sunday, 17, 59);
        var c = await ReauthAsync(userId);
        var today = DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);
        var runs = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!
            .Where(a => a.GetProperty("title").GetString() == "Easy run" && a.GetProperty("checkIn").ValueKind == JsonValueKind.Null
                && DateOnly.Parse(a.GetProperty("date").GetString()!) <= today).OrderBy(a => a.GetProperty("date").GetString()).Take(skips);
        foreach (var run in runs)
            (await c.PutAsJsonAsync("/check-ins", new
            {
                scheduledActionId = run.GetProperty("id").GetGuid(), clientId = "ci-" + Guid.NewGuid().ToString("N"), state = "skipped",
                tappedAt = factory.Clock.GetUtcNow().UtcDateTime,
            })).EnsureSuccessStatusCode();
        await CheckInAsync(userId, "done", d => d <= today);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        return (await CurrentRefreshAsync(userId))!.Value;
    }

    private static double MinutesIn(JsonElement version, DateOnly from, DateOnly to, Func<JsonElement, bool>? only = null) =>
        version.GetProperty("twoWeeks").EnumerateArray()
            .Where(a => DateOnly.Parse(a.GetProperty("date").GetString()!) >= from && DateOnly.Parse(a.GetProperty("date").GetString()!) < to && (only?.Invoke(a) ?? true))
            .Sum(a => a.GetProperty("minutes").GetInt32());

    [Fact]
    public async Task While_keeping_up_a_shortfall_is_folded_into_the_next_weeks_as_a_proposal_that_can_be_declined()
    {
        var (_, userId, _, _) = await ApprovedPlanAsync();
        await AcceptAsync(userId, await RefreshAfterAsync(userId, "done"));
        var refresh = await RefreshSkippingRunsAsync(userId, skips: 1);
        var v = await VersionAsync(userId, ProposalOf(refresh));

        var change = Assert.Single(Codes(v, "changes"), c => c.GetProperty("code").GetString() == "catch_up");
        Assert.Equal(30, change.GetProperty("minutes").GetInt32());
        Assert.Equal("shift_dates", change.GetProperty("alternative").GetString());
        var extra = v.GetProperty("twoWeeks").EnumerateArray().Where(a => a.GetProperty("kind").GetString() == "catch_up").ToList();
        Assert.Equal(30, extra.Sum(a => a.GetProperty("minutes").GetInt32()));

        // "Just shift the dates": accepted without the Catch-up, which then waits as an offer.
        var res = await (await ReauthAsync(userId)).PostAsJsonAsync($"/weekly-refresh/{refresh.GetProperty("id").GetGuid()}/accept", new { catchUp = false });
        res.EnsureSuccessStatusCode();
        var applied = await VersionAsync(userId, ProposalOf(refresh));
        Assert.DoesNotContain(applied.GetProperty("twoWeeks").EnumerateArray(), a => a.GetProperty("kind").GetString() == "catch_up");
        Assert.Contains(Codes(applied, "offers"), o => o.GetProperty("code").GetString() == "shift_dates");
    }

    [Fact]
    public async Task Catch_up_stays_within_three_weeks_twenty_percent_over_the_share_and_a_days_Slot()
    {
        // 1.6 h is 96 minutes a week: three 30-minute runs leave 25 minutes of the 20% over, a week.
        var (_, userId, _, _) = await ApprovedPlanAsync(shareHours: 1.6);
        await AcceptAsync(userId, await RefreshAfterAsync(userId, "done"));
        var refresh = await RefreshSkippingRunsAsync(userId, skips: 2); // 60 minutes behind
        var v = await VersionAsync(userId, ProposalOf(refresh));

        var start = DateOnly.Parse(v.GetProperty("startDate").GetString()!);
        var catchUp = v.GetProperty("twoWeeks").EnumerateArray().Where(a => a.GetProperty("kind").GetString() == "catch_up").ToList();
        Assert.NotEmpty(catchUp);
        Assert.All(catchUp, a => Assert.True(DateOnly.Parse(a.GetProperty("date").GetString()!) < start.AddDays(21)));
        for (var w = 0; w < 3; w++)
            Assert.True(MinutesIn(v, start.AddDays(7 * w), start.AddDays(7 * w + 7)) <= 96 * 1.2 + 0.001, $"week {w} is over 120% of the share");
        foreach (var day in v.GetProperty("twoWeeks").EnumerateArray().GroupBy(a => a.GetProperty("date").GetString()))
            Assert.True(day.Sum(a => a.GetProperty("minutes").GetInt32()) <= 45, $"{day.Key} is past the Slot");
        var change = Assert.Single(Codes(v, "changes"), c => c.GetProperty("code").GetString() == "catch_up");
        Assert.True(change.GetProperty("minutes").GetInt32() < 60); // what did not fit is left to shifting the dates
        Assert.Equal(60, change.GetProperty("minutes").GetInt32() + change.GetProperty("leftover").GetInt32());
    }

    [Fact]
    public async Task While_easing_back_a_shortfall_moves_dates_instead_of_adding_Catch_up()
    {
        var (_, userId, _, _) = await ApprovedPlanAsync();
        await AcceptAsync(userId, await RefreshAfterAsync(userId, "done"));
        var refresh = await RefreshAfterAsync(userId, "skipped");
        var v = await VersionAsync(userId, ProposalOf(refresh));

        Assert.Contains(Codes(v, "changes"), c => c.GetProperty("code").GetString() == "ease_back");
        Assert.DoesNotContain(v.GetProperty("twoWeeks").EnumerateArray(), a => a.GetProperty("kind").GetString() == "catch_up");
        // The dates move by a proposal of their own, not by an offer inside the refresh's version.
        Assert.DoesNotContain(Codes(v, "offers"), o => o.GetProperty("code").GetString() == "shift_dates");
        var cards = (await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement>("/coach/messages")).EnumerateArray()
            .Where(m => m.GetProperty("card").GetString() == "proposal" && m.GetProperty("cardData").TryGetProperty("kind", out var k) && k.GetString() == "dates_later").ToList();
        var dates = await VersionAsync(userId, Assert.Single(cards).GetProperty("cardData").GetProperty("planVersionId").GetGuid());
        Assert.Equal("proposed", dates.GetProperty("status").GetString());
    }

    [Fact]
    public async Task Guide_changes_are_part_of_the_refresh_proposal_and_kept_with_its_version()
    {
        string Body(string extra) => string.Join(" ", Enumerable.Repeat("Warm up, run easy, walk when you need to.", 9)) + extra;
        object Guide(string extra, int high) => new { guides = new[] { new { action = "Easy run", body = Body(extra), targets = new[] { new { what = "Run time", low = 20, high, unit = "min", why = "a small step" } },
            withheld = Array.Empty<object>(), referral = "", reason = "You kept up, so five more minutes." } } };
        var (_, userId, _, _) = await ApprovedPlanAsync(guides: Guide(" Marker birch.", 25));
        factory.Coach.EnqueueRefreshGuides("Marker birch.", Guide(" Marker birch. Add five minutes.", 30));

        var refresh = await RefreshAfterAsync(userId, "done");
        var proposal = await VersionAsync(userId, ProposalOf(refresh));

        var change = proposal.GetProperty("changes").GetProperty("changes").EnumerateArray().Single(c => c.GetProperty("what").GetString() == "guide");
        Assert.Equal("Easy run", change.GetProperty("title").GetString());
        Assert.Equal("You kept up, so five more minutes.", change.GetProperty("reason").GetString());
        var run = proposal.GetProperty("habits").EnumerateArray().Single(h => h.GetProperty("title").GetString() == "Easy run");
        Assert.Equal(30, run.GetProperty("guide").GetProperty("targets")[0].GetProperty("high").GetDouble());

        // Until it is approved the Action keeps the Guide it had; accepting makes the new one current.
        var before = (await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.First(a => a.GetProperty("title").GetString() == "Easy run");
        Assert.Equal(25, before.GetProperty("guide").GetProperty("targets")[0].GetProperty("high").GetDouble());
        await AcceptAsync(userId, refresh);
        var after = (await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Last(a => a.GetProperty("title").GetString() == "Easy run");
        Assert.Equal(30, after.GetProperty("guide").GetProperty("targets")[0].GetProperty("high").GetDouble());
    }

    [Fact]
    public async Task A_referral_that_a_refresh_proposes_shows_as_a_card_only_once_the_proposal_is_accepted()
    {
        string Body(string extra) => string.Join(" ", Enumerable.Repeat("Warm up, run easy, walk when you need to.", 9)) + extra;
        object Guide(string extra, string referral, object[] withheld) => new { guides = new[] { new { action = "Easy run", body = Body(extra), targets = new[] { new { what = "Run time", low = 20, high = 25, unit = "min", why = "a small step" } },
            withheld, referral, reason = "Your knee hurt this week." } } };
        var (_, userId, _, _) = await ApprovedPlanAsync(guides: Guide(" Marker ember.", "", []));
        factory.Coach.EnqueueRefreshGuides("Marker ember.", Guide(" Marker ember. Skip hills.", "A sore knee is worth a word with your physio.", [new { area = "leg load", reason = "your knee hurts" }]));

        var refresh = await RefreshAfterAsync(userId, "done");
        async Task<int> CardsAsync() => (await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/coach/messages"))!.Count(m => m.GetProperty("card").GetString() == "referral");

        Assert.Equal(0, await CardsAsync()); // proposed, not approved: nothing has changed for the user yet
        await AcceptAsync(userId, refresh);
        Assert.Equal(1, await CardsAsync());
    }
}
