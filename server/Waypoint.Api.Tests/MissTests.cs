using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waypoint.Api.Coach;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>The shared setup: an approved Plan with one habit session on Wednesday the 7th and a task.</summary>
public abstract class MissTestBase(WaypointApiFactory factory) : PlanTestBase(factory)
{
    protected static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>An approved Plan starting 2026-01-06 with one Easy run on Wednesday the 7th, so Friday is free; <paramref name="withFriday"/> adds the Friday run too.</summary>
    protected async Task<(HttpClient Client, Guid Wednesday, Guid Shoes)> PlanAsync(string onSkip = "gentle_check", bool withFriday = false)
    {
        var hand = HandOff();
        hand["aspiration"]!["on_skip"] = onSkip;
        var (c, _, _, aspirationId) = await ConfirmedAsync(hand);
        var plan = Plan(p =>
        {
            var days = p["first_two_weeks"]!.AsArray();
            if (!withFriday) days.RemoveAt(1); // 2026-01-09
        });
        var versionId = await DraftedAsync(c, aspirationId, plan);
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        var actions = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!;
        return (c, actions.First(a => a.GetProperty("title").GetString() == "Easy run").GetProperty("id").GetGuid(),
            actions.First(a => a.GetProperty("title").GetString() == "Buy running shoes").GetProperty("id").GetGuid());
    }

    protected async Task SkipAsync(HttpClient c, Guid actionId, string reply)
    {
        factory.Coach.Fail = false;
        factory.Coach.Enqueue(reply);
        (await c.PutAsJsonAsync("/check-ins", new { scheduledActionId = actionId, clientId = "ci-" + Guid.NewGuid().ToString("N"), state = "skipped", tappedAt = T0 })).EnsureSuccessStatusCode();
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();
    }

    protected static async Task<JsonElement> LastCoachMessageAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement[]>("/coach/messages"))!.Last(m => m.GetProperty("role").GetString() == "coach");
}

/// <summary>How the Coach answers a skip: the Aspiration's reaction, the make-up offer for a habit, and a tone with no blame.</summary>
public sealed class MissTests(WaypointApiFactory factory) : MissTestBase(factory), IClassFixture<WaypointApiFactory>
{
    [Theory]
    [InlineData("quiet_replan")]
    [InlineData("ask_why")]
    public async Task A_scheduled_Action_carries_its_Aspirations_skip_tone_to_the_phone(string onSkip)
    {
        var (c, wednesday, _) = await PlanAsync(onSkip);
        var view = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.First(a => a.GetProperty("id").GetGuid() == wednesday);
        Assert.Equal(onSkip, view.GetProperty("onSkip").GetString());
    }

    [Theory]
    [InlineData("gentle_check", "gently check in")]
    [InlineData("quiet_replan", "quietly")]
    [InlineData("ask_why", "ask what got in the way")]
    public async Task A_skip_is_answered_with_the_Aspirations_reaction_to_a_skip_first(string onSkip, string expected)
    {
        var (c, wednesday, _) = await PlanAsync(onSkip);
        await SkipAsync(c, wednesday, "That's okay. Friday could work if you like.");

        var request = factory.Coach.Requests.Last(r => r.Kind is CoachCallKind.CheckInReplyNoNote or CoachCallKind.CheckInReplyWithNote);
        Assert.Contains(expected, request.System);
    }

    [Fact]
    public async Task A_reply_that_blames_is_never_posted_and_the_Coach_is_late_instead()
    {
        var (c, wednesday, _) = await PlanAsync();
        await SkipAsync(c, wednesday, "You missed your run, and that breaks your streak.");
        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.DoesNotContain(thread!, m => m.GetProperty("role").GetString() == "coach" && m.GetProperty("card").ValueKind == JsonValueKind.Null);

        factory.Coach.Enqueue("No worries, today was a lot.");
        factory.Clock.Advance(TimeSpan.FromMinutes(2)); // the retry's backoff
        await RunJobsAsync();
        var reply = await LastCoachMessageAsync(c);
        Assert.Equal("No worries, today was a lot.", reply.GetProperty("text").GetString());
    }

    [Fact]
    public void Every_prompt_that_answers_a_miss_asks_for_one_line_and_one_next_step_without_blame_words()
    {
        foreach (var prompt in new[] { CheckInReplies.SystemPrompt, RethinkPrompt.System, RefreshPrompt.System })
        {
            Assert.Contains("one next step", prompt);
            Assert.Contains("no guilt", prompt, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var sample in File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Fixtures", "miss-responses.txt")).Where(l => l.Length > 0))
            Assert.False(MissTone.Violates(sample), sample);
        Assert.True(MissTone.Violates("You missed it"));
        Assert.True(MissTone.Violates("Don't break the Streak"));
        Assert.True(MissTone.Violates("That failed"));
        Assert.False(MissTone.Violates("I dismissed that thought, no worries"));
    }

    [Fact]
    public async Task A_skipped_habit_session_offers_a_free_Slot_later_that_week_and_accepting_moves_it()
    {
        var (c, wednesday, _) = await PlanAsync();
        await SkipAsync(c, wednesday, "That's okay. Friday morning is free if you'd like to run then.");

        var offer = await LastCoachMessageAsync(c);
        Assert.Equal("make_up", offer.GetProperty("card").GetString());
        var data = offer.GetProperty("cardData");
        Assert.Equal(wednesday, data.GetProperty("scheduledActionId").GetGuid());
        Assert.Equal("2026-01-09", data.GetProperty("date").GetString());

        var res = await c.PostAsync($"/scheduled-actions/{wednesday}/move-later", null);
        res.EnsureSuccessStatusCode();
        var moved = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Single(a => a.GetProperty("id").GetGuid() == wednesday);
        Assert.Equal("2026-01-09", moved.GetProperty("date").GetString());
        Assert.Equal(JsonValueKind.Null, moved.GetProperty("checkIn").ValueKind);
    }

    [Fact]
    public async Task No_make_up_is_offered_when_the_habit_already_meets_every_later_day()
    {
        var (c, wednesday, _) = await PlanAsync(withFriday: true);
        await SkipAsync(c, wednesday, "That's okay. The next session is on Friday.");

        Assert.Null((await LastCoachMessageAsync(c)).GetProperty("card").GetString());
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await c.PostAsync($"/scheduled-actions/{wednesday}/move-later", null)).StatusCode);
    }

    [Fact]
    public async Task A_skipped_task_gets_no_make_up_offer_because_it_moves_at_the_refresh()
    {
        var (c, _, shoes) = await PlanAsync();
        await SkipAsync(c, shoes, "That's okay. It'll move to the next free time.");
        Assert.Null((await LastCoachMessageAsync(c)).GetProperty("card").GetString());
    }
}

/// <summary>Its own factory, because it moves the fake clock past the Plan's first week.</summary>
public sealed class LateMissTests(WaypointApiFactory factory) : MissTestBase(factory), IClassFixture<WaypointApiFactory>
{
    [Fact]
    public async Task No_make_up_is_offered_on_a_day_that_has_already_passed_when_the_skip_is_checked_in_late()
    {
        var (c, wednesday, _) = await PlanAsync();
        var userId = (await c.GetFromJsonAsync<JsonElement>("/me")).GetProperty("id").GetGuid();
        factory.Clock.Advance(TimeSpan.FromDays(5)); // Saturday the 10th: Friday's free Slot is behind us
        c = await ReauthAsync(userId); // the access token lasts 15 minutes
        await SkipAsync(c, wednesday, "That's okay. The next session is whenever it comes.");

        Assert.Null((await LastCoachMessageAsync(c)).GetProperty("card").GetString());
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await c.PostAsync($"/scheduled-actions/{wednesday}/move-later", null)).StatusCode);
    }
}
