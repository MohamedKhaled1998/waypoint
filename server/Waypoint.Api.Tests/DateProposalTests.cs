using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>Moving Milestone dates by proposal. The class has its own fake clock: it stays at Monday 2026-01-05 09:00 here.</summary>
public sealed class DateProposalTests(WaypointApiFactory factory) : ProgressTestBase(factory), IClassFixture<WaypointApiFactory>
{
    // ---- Moving dates: by proposal only, never past Settle-for ----

    private async Task<JsonElement[]> CoachCardsAsync(HttpClient c, string card) =>
        (await c.GetFromJsonAsync<JsonElement>("/coach/messages")).EnumerateArray().Where(m => m.GetProperty("card").GetString() == card).ToArray();

    private async Task<JsonElement> DateVersionAsync(HttpClient c, string reason)
    {
        foreach (var card in await CoachCardsAsync(c, "proposal"))
        {
            var v = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{card.GetProperty("cardData").GetProperty("planVersionId").GetGuid()}");
            if (D(v, "reason") == reason) return v;
        }
        throw new Xunit.Sdk.XunitException($"no {reason} proposal in the chat");
    }

    private async Task<T> InDbAsync<T>(Guid userId, Func<WaypointDb, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        return await work(scope.ServiceProvider.GetRequiredService<WaypointDb>());
    }

    private async Task AddMilestonesAsync(Guid userId, Guid aspirationId, params (string Ref, DateOnly Date)[] more) =>
        await InDbAsync(userId, async db =>
        {
            var current = await db.PlanVersions.FirstAsync(p => p.AspirationId == aspirationId && p.Status == PlanVersionStatus.Approved);
            foreach (var (r, date) in more)
                db.Milestones.Add(new Milestone
                {
                    Id = Guid.NewGuid(), UserId = userId, PlanVersionId = current.Id, Ref = r, Date = date, Title = "Step " + r,
                    ReachedWhen = "you have done it", Kind = "effort", State = "upcoming",
                });
            await db.SaveChangesAsync();
            return 0;
        });

    private async Task<List<(string Ref, DateOnly Date)>> MilestonesOfAsync(Guid userId, Guid versionId) =>
        await InDbAsync(userId, async db => (await db.Milestones.Where(m => m.PlanVersionId == versionId).OrderBy(m => m.Date).ToListAsync())
            .Select(m => (m.Ref, m.Date)).ToList());

    private static string MilestoneDate(JsonElement version) => D(version.GetProperty("milestones")[0], "date");

    [Fact]
    public async Task Slipping_proposes_later_dates_capped_at_Settle_for_and_nothing_moves_until_approved()
    {
        // The Pace puts the middle at Apr 27 and the optimistic end at Apr 20: Settle-for on Apr 22 is inside the range, so the state is Slipping.
        var (c, userId, aspirationId) = await SeedAsync(Half, settleFor: new DateOnly(2026, 4, 22), readings: "on_track;on_track;on_track+over");
        await RefreshAsync(userId);
        Assert.Equal("slipping", D(await ProgressOf(c), "state"));

        var proposal = await DateVersionAsync(c, "dates_later");
        Assert.Equal("proposed", D(proposal, "status"));
        Assert.Equal("2026-04-22", MilestoneDate(proposal)); // not the 27th
        var dates = proposal.GetProperty("milestones").EnumerateArray().Select(m => DateOnly.Parse(D(m, "date"))).ToList();
        Assert.NotEmpty(dates);
        Assert.All(dates, d => Assert.True(d <= new DateOnly(2026, 4, 22), $"{d} is past Settle-for"));
        Assert.Equal(dates.OrderBy(d => d).ToList(), dates); // the order is kept
        Assert.Equal("2026-03-02", D((await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("milestones")[0], "date"));

        var approved = await c.PostAsync($"/plan-versions/{proposal.GetProperty("id").GetGuid()}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        Assert.Equal("2026-04-22", D((await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("milestones")[0], "date"));
    }

    [Fact]
    public void Slipping_caps_every_open_Milestone_at_Settle_for_and_keeps_their_order()
    {
        Milestone M(string r, DateOnly d, string state = "upcoming") => new()
            { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), PlanVersionId = Guid.NewGuid(), Ref = r, Date = d, Title = r, ReachedWhen = "x", Kind = "effort", State = state };
        var settle = new DateOnly(2026, 4, 22);
        var shifts = Waypoint.Api.Coach.DateProposals.Slip(
            [M("m0", new DateOnly(2026, 2, 2), "reached"), M("m1", new DateOnly(2026, 3, 2)), M("m2", new DateOnly(2026, 3, 30)), M("m3", new DateOnly(2026, 4, 20))],
            projectedMiddle: new DateOnly(2026, 4, 27), settle);
        var after = shifts.Select(s => s.After).ToList();
        Assert.Equal(["m1", "m2", "m3"], shifts.Select(s => s.Ref));
        Assert.All(after, d => Assert.True(d <= settle, $"{d} is past Settle-for"));
        Assert.Equal(after.OrderBy(d => d).ToList(), after);
        Assert.Equal(settle, after[^1]);
    }

    [Fact]
    public async Task A_Slipping_proposal_keeps_the_habits_and_is_never_applied_by_the_refresh_auto_apply()
    {
        var (c, userId, _) = await SeedAsync(Half, readings: "on_track;on_track;on_track+over");
        await RefreshAsync(userId);
        var proposal = await DateVersionAsync(c, "dates_later");
        Assert.Equal("2026-04-27", MilestoneDate(proposal)); // inside Settle-for: the Projection's middle
        Assert.Equal("Easy run", D(proposal.GetProperty("habits")[0], "title"));

        // The weekly refresh's own proposal auto-applies after a day; a dates proposal is never silently applied.
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        var applier = scope.ServiceProvider.GetRequiredService<Waypoint.Api.Coach.RefreshApplier>();
        var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
        foreach (var r in db.WeeklyRefreshes.Where(r => r.Status == RefreshStatus.Open).ToList()) await applier.ApplyAsync(r.Id, default);
        Assert.Equal("proposed", D(await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{proposal.GetProperty("id").GetGuid()}"), "status"));
        Assert.Equal("2026-03-02", D((await c.GetFromJsonAsync<JsonElement>("/progress")).GetProperty("aspirations")[0].GetProperty("milestones")[0], "date"));
    }

    [Fact]
    public async Task Past_Settle_for_proposes_no_dates_and_puts_the_three_option_choice()
    {
        var (c, userId, aspirationId) = await SeedAsync(Half, settleFor: new DateOnly(2026, 3, 3), readings: "on_track;on_track;on_track");
        await RefreshAsync(userId);
        Assert.Equal("past_settle_for", D(await ProgressOf(c), "state"));

        var choice = Assert.Single(await CoachCardsAsync(c, "settle_choice"));
        var options = choice.GetProperty("cardData").GetProperty("options").EnumerateArray().Select(o => o.GetString()).ToArray();
        Assert.Equal(["move_date", "keep_date_smaller_picture", "make_more_room"], options);
        Assert.Equal(aspirationId, choice.GetProperty("aspirationId").GetGuid());
        Assert.DoesNotContain(await CoachCardsAsync(c, "proposal"), m => m.GetProperty("cardData").TryGetProperty("kind", out var k) && k.GetString()!.StartsWith("dates_"));
        Assert.Equal("2026-03-02", D((await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("milestones")[0], "date"));

        var brief = JsonDocument.Parse(factory.Coach.Requests.Last(r => r.Kind == Waypoint.Api.Coach.CoachCallKind.WeeklyRefresh).Turns[0].Text).RootElement.GetProperty("progress");
        Assert.Equal("past_settle_for", D(brief, "state"));
        // The Progress view carries the choice for the calm card and the muted line.
        Assert.Equal(3, (await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("settleChoice").GetProperty("options").GetArrayLength());
    }

    [Fact]
    public async Task Ahead_offers_earlier_dates_once_per_Milestone_and_never_moves_them_on_its_own()
    {
        var (c, userId, aspirationId) = await SeedAsync(Fast, readings: "on_track;on_track;on_track+early");
        await RefreshAsync(userId);
        Assert.Equal("ahead", D(await ProgressOf(c), "state"));
        var offer = await DateVersionAsync(c, "dates_earlier");
        Assert.Equal("2026-02-23", MilestoneDate(offer)); // a whole week earlier
        Assert.Equal("2026-03-02", D((await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("milestones")[0], "date"));

        await RefreshAsync(userId); // still ahead: the offer was made, so it is not made again for this Milestone
        await RefreshAsync(userId);
        var offers = (await CoachCardsAsync(c, "proposal")).Count(m => m.GetProperty("cardData").TryGetProperty("kind", out var k) && k.GetString() == "dates_earlier");
        Assert.Equal(1, offers);
    }

    // ---- Shortfall while easing back or re-thinking: the same later-dates proposal ----

    /// <summary>
    /// A user whose seeded Actions belong to the Easy run habit (90 planned minutes a week), so the refresh measures the week's shortfall.
    /// No earlier readings: the state is too early, so only the shortfall can propose dates.
    /// </summary>
    private async Task<(HttpClient C, Guid UserId, Guid AspirationId)> ShortfallAsync(string weeks, DateOnly settleFor, string readings = "")
    {
        var (c, userId, aspirationId) = await SeedAsync(weeks, settleFor: settleFor, readings: readings);
        await InDbAsync(userId, async db =>
        {
            var habit = await db.Habits.FirstAsync(h => h.Title == "Easy run");
            await db.ScheduledActions.Where(a => a.AspirationId == aspirationId).ExecuteUpdateAsync(s => s.SetProperty(a => a.HabitId, habit.Id));
            return 0;
        });
        return (c, userId, aspirationId);
    }

    private async Task ApplyOpenRefreshesAsync(Guid userId)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        var applier = scope.ServiceProvider.GetRequiredService<Waypoint.Api.Coach.RefreshApplier>();
        var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
        foreach (var r in db.WeeklyRefreshes.Where(r => r.Status == RefreshStatus.Open).ToList()) await applier.ApplyAsync(r.Id, default);
    }

    private async Task<string> RefreshVersionChangesAsync(Guid userId) =>
        await InDbAsync(userId, db => db.PlanVersions.Where(p => p.Reason == "weekly_refresh").OrderByDescending(p => p.CreatedAt).Select(p => p.ChangesJson!).FirstAsync());

    [Theory]
    [InlineData("easing_back", "1:90:skipped;2:90:done;3:90:done;4:90:done")]
    [InlineData("re_thinking", "1:90:skipped;2:90:skipped;3:90:done;4:90:done")]
    public async Task A_shortfall_while_easing_back_or_re_thinking_proposes_later_dates_capped_at_Settle_for_and_nothing_moves_until_approved(string band, string weeks)
    {
        // 90 minutes short at 90 planned a week is one week later: March 9, past Settle-for on March 4, so capped there.
        var (c, userId, aspirationId) = await ShortfallAsync(weeks, settleFor: new DateOnly(2026, 3, 4));
        if (band == "re_thinking") factory.Coach.Enqueue("A lot has been going on lately. What changed for you?");
        await RefreshAsync(userId);

        var proposal = await DateVersionAsync(c, "dates_later");
        Assert.Equal("proposed", D(proposal, "status"));
        Assert.Equal("2026-03-04", MilestoneDate(proposal));
        var card = Assert.Single(await CoachCardsAsync(c, "proposal"));
        Assert.Equal(aspirationId, card.GetProperty("aspirationId").GetGuid());

        // One dates proposal, not an offer inside the refresh's own version; the Coach was told why the dates move.
        Assert.DoesNotContain("shift_dates", await RefreshVersionChangesAsync(userId));
        var brief = JsonDocument.Parse(factory.Coach.Requests.Last(r => r.Kind == Waypoint.Api.Coach.CoachCallKind.WeeklyRefresh
            && r.Turns[0].Text.Contains("Half marathon")).Turns[0].Text).RootElement;
        Assert.Equal(band == "easing_back" ? "EaseBack" : "Rethink", D(brief, "tolerance"));
        var dates = brief.GetProperty("progress").GetProperty("dates");
        Assert.Equal("shortfall", D(dates, "because"));
        Assert.True(dates.GetProperty("capped_at_settle_for").GetBoolean());

        // The refresh's auto-apply leaves it waiting, and the date stays until the user approves.
        await ApplyOpenRefreshesAsync(userId);
        Assert.Equal("proposed", D(await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{proposal.GetProperty("id").GetGuid()}"), "status"));
        Assert.Equal("2026-03-02", D((await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("milestones")[0], "date"));

        var approved = await c.PostAsync($"/plan-versions/{proposal.GetProperty("id").GetGuid()}/approve", null);
        Assert.True(approved.StatusCode == HttpStatusCode.OK, await approved.Content.ReadAsStringAsync());
        Assert.Equal("2026-03-04", D((await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("milestones")[0], "date"));
    }

    [Fact]
    public void A_shortfall_moves_open_Milestones_by_whole_weeks_of_the_planned_minutes_at_least_one()
    {
        Milestone M(string r, DateOnly d, string state = "upcoming") => new()
            { Id = Guid.NewGuid(), UserId = Guid.NewGuid(), PlanVersionId = Guid.NewGuid(), Ref = r, Date = d, Title = r, ReachedWhen = "x", Kind = "effort", State = state };
        List<Milestone> ms = [M("m0", new DateOnly(2026, 2, 2), "reached"), M("m1", new DateOnly(2026, 3, 2)), M("m2", new DateOnly(2026, 4, 6))];

        var small = Waypoint.Api.Coach.DateProposals.ForShortfall(ms, 20, 90, null);
        Assert.Equal(["m1", "m2"], small.Select(s => s.Ref));
        Assert.Equal(new DateOnly(2026, 3, 9), small[0].After); // a little short is still one whole week

        var big = Waypoint.Api.Coach.DateProposals.ForShortfall(ms, 100, 90, new DateOnly(2026, 4, 10));
        Assert.Equal(new DateOnly(2026, 3, 16), big[0].After); // 100 of 90 rounds up to two weeks
        Assert.Equal(new DateOnly(2026, 4, 10), big[1].After); // capped at Settle-for

        Assert.Empty(Waypoint.Api.Coach.DateProposals.ForShortfall(ms, 0, 90, null));
    }

    [Fact]
    public async Task Slipping_and_a_shortfall_in_the_same_refresh_make_one_dates_proposal()
    {
        // Slipping with the week just ended skipped (easing back): the Projection's proposal already counts that week.
        var (c, userId, _) = await ShortfallAsync("1:100:skipped;2:100:partly:50;3:100:partly:50;4:100:partly:50",
            settleFor: new DateOnly(2027, 1, 1), readings: "on_track;on_track;on_track+over");
        await RefreshAsync(userId);
        Assert.Equal("slipping", D(await ProgressOf(c), "state"));

        Assert.Single(await CoachCardsAsync(c, "proposal"));
        var brief = JsonDocument.Parse(factory.Coach.Requests.Last(r => r.Kind == Waypoint.Api.Coach.CoachCallKind.WeeklyRefresh
            && r.Turns[0].Text.Contains("Half marathon")).Turns[0].Text).RootElement;
        Assert.Equal("EaseBack", D(brief, "tolerance"));
        Assert.False(brief.GetProperty("progress").GetProperty("dates").TryGetProperty("because", out _));
        Assert.DoesNotContain("shift_dates", await RefreshVersionChangesAsync(userId));
    }

    // ---- Approving rebases the proposal on the Plan as it is now ----

    private async Task<(HttpClient C, Guid UserId, Guid AspirationId, JsonElement Proposal)> SlippingProposalAsync()
    {
        var (c, userId, aspirationId) = await SeedAsync(Half, readings: "on_track;on_track;on_track+over");
        await RefreshAsync(userId);
        return (c, userId, aspirationId, await DateVersionAsync(c, "dates_later"));
    }

    [Fact]
    public async Task Approving_rebases_the_dates_on_the_Plan_as_it_is_now()
    {
        var (c, userId, aspirationId, proposal) = await SlippingProposalAsync();
        // A newer Plan was approved after the proposal was made: its Habit changed, the Milestone dates did not.
        await InDbAsync(userId, async db =>
        {
            await db.Habits.Where(h => h.Title == "Easy run" && h.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(h => h.Title, "Long run"));
            return 0;
        });

        var id = proposal.GetProperty("id").GetGuid();
        var res = await c.PostAsync($"/plan-versions/{id}/approve", null);
        Assert.True(res.StatusCode == HttpStatusCode.OK, await res.Content.ReadAsStringAsync());
        var approved = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{id}");
        Assert.Equal("Long run", D(approved.GetProperty("habits")[0], "title"));
        Assert.Equal(1, approved.GetProperty("milestones").GetArrayLength());
        Assert.Equal("2026-04-27", MilestoneDate(approved));
    }

    [Fact]
    public async Task A_proposal_made_from_dates_that_have_since_moved_is_stale_and_changes_nothing()
    {
        var (c, userId, aspirationId, proposal) = await SlippingProposalAsync();
        await InDbAsync(userId, async db =>
        {
            await db.Milestones.Where(m => m.Ref == "m1" && m.UserId == userId).ExecuteUpdateAsync(s => s.SetProperty(m => m.Date, new DateOnly(2026, 3, 9)));
            return 0;
        });

        var id = proposal.GetProperty("id").GetGuid();
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/plan-versions/{id}/approve", null)).StatusCode);
        Assert.NotEqual("approved", D(await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{id}"), "status"));
        Assert.Equal("2026-03-09", D((await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("milestones")[0], "date"));
    }

    [Fact]
    public async Task Two_approvals_at_once_leave_one_complete_copy_of_the_Plan()
    {
        var (c, userId, _, proposal) = await SlippingProposalAsync();
        var id = proposal.GetProperty("id").GetGuid();
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => c.PostAsync($"/plan-versions/{id}/approve", null)));
        Assert.Contains(results, r => r.StatusCode == HttpStatusCode.OK);
        var rows = await MilestonesOfAsync(userId, id);
        Assert.Single(rows);
        Assert.Equal(1, await InDbAsync(userId, db => db.Habits.CountAsync(h => h.PlanVersionId == id)));
    }

    [Fact]
    public async Task An_earlier_date_is_never_rebased_to_before_today_or_before_the_Milestone_ahead_of_it()
    {
        var (c, userId, aspirationId) = await SeedAsync(Fast, readings: "on_track;on_track;on_track+early");
        await AddMilestonesAsync(userId, aspirationId, ("m2", new DateOnly(2026, 4, 6)));
        await RefreshAsync(userId);
        var offer = await DateVersionAsync(c, "dates_earlier");
        var id = offer.GetProperty("id").GetGuid();
        // The proposal's stored dates are old: both before today.
        await InDbAsync(userId, async db =>
        {
            var v = await db.PlanVersions.FirstAsync(p => p.Id == id);
            var json = System.Text.Json.Nodes.JsonNode.Parse(v.ChangesJson!)!;
            foreach (var ch in json["changes"]!.AsArray()) ch!["after"] = "2025-06-01";
            v.ChangesJson = json.ToJsonString();
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/plan-versions/{id}/approve", null)).StatusCode);
        var rows = await MilestonesOfAsync(userId, id);
        Assert.All(rows, r => Assert.True(r.Date >= new DateOnly(2026, 1, 5), $"{r.Date} is before today"));
        Assert.Equal(rows.OrderBy(r => r.Date).ToList(), rows);
    }

    // ---- "Change dates" and "Move the date" in the Coach chat end in a proposed Plan version ----

    [Theory]
    [InlineData("change_dates")]
    [InlineData("move_date")]
    public async Task Opening_the_chat_from_an_Aspiration_to_change_dates_ends_in_a_proposed_Plan_version(string intent)
    {
        var (c, _, aspirationId) = await SeedAsync(Half, readings: "on_track;on_track;on_track+over");
        var res = await c.PostAsJsonAsync("/coach/messages", new { text = "I would like to change the dates.", clientId = "chat-" + intent, aspirationId, intent });
        res.EnsureSuccessStatusCode();
        await res.Content.ReadAsStringAsync();

        var offer = await DateVersionAsync(c, "dates_later");
        Assert.Equal("proposed", D(offer, "status"));
        Assert.Equal(aspirationId, offer.GetProperty("aspirationId").GetGuid());
        var card = Assert.Single(await CoachCardsAsync(c, "proposal"));
        Assert.Equal(aspirationId, card.GetProperty("aspirationId").GetGuid());
        // Nothing moved until approved.
        Assert.Equal("2026-03-02", D((await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("milestones")[0], "date"));
        // The model saw what the server proposed.
        Assert.Contains("2026-04-27", factory.Coach.Requests.Last().Turns.Last().Text);
    }

    [Fact]
    public async Task Move_the_date_may_go_past_Settle_for_because_the_user_asked_and_stays_there_on_approval()
    {
        var (c, _, aspirationId) = await SeedAsync(Half, settleFor: new DateOnly(2026, 3, 3), readings: "on_track;on_track;on_track");
        (await c.PostAsJsonAsync("/coach/messages", new { text = "Let us move the date.", clientId = "chat-move", aspirationId, intent = "move_date" })).EnsureSuccessStatusCode();
        var offer = await DateVersionAsync(c, "dates_later");
        Assert.Equal("2026-04-27", MilestoneDate(offer));
        var res = await c.PostAsync($"/plan-versions/{offer.GetProperty("id").GetGuid()}/approve", null);
        Assert.True(res.StatusCode == HttpStatusCode.OK, offer.ToString() + await res.Content.ReadAsStringAsync());
        Assert.Equal("2026-04-27", D((await c.GetFromJsonAsync<JsonElement>($"/progress/{aspirationId}")).GetProperty("milestones")[0], "date"));
    }

    [Fact]
    public async Task Move_the_date_replaces_a_pending_capped_proposal_with_one_that_may_go_past_Settle_for()
    {
        var (c, _, aspirationId) = await SeedAsync(Half, settleFor: new DateOnly(2026, 3, 3), readings: "on_track;on_track;on_track");
        (await c.PostAsJsonAsync("/coach/messages", new { text = "Change the dates.", clientId = "chat-capped", aspirationId, intent = "change_dates" })).EnsureSuccessStatusCode();
        var capped = await DateVersionAsync(c, "dates_later");
        Assert.Equal("2026-03-03", MilestoneDate(capped));

        (await c.PostAsJsonAsync("/coach/messages", new { text = "Actually, move the date.", clientId = "chat-uncapped", aspirationId, intent = "move_date" })).EnsureSuccessStatusCode();
        var cards = await CoachCardsAsync(c, "proposal");
        var last = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{cards[^1].GetProperty("cardData").GetProperty("planVersionId").GetGuid()}");
        Assert.Equal("proposed", D(last, "status"));
        Assert.Equal("2026-04-27", MilestoneDate(last));
        Assert.NotEqual(capped.GetProperty("id").GetGuid(), last.GetProperty("id").GetGuid());
        // Only one proposal waits.
        var old = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{capped.GetProperty("id").GetGuid()}");
        Assert.NotEqual("proposed", D(old, "status"));
    }
}
