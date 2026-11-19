using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>Pause, set-aside Actions, the way back and the silence ladder; has its own factory and clock.</summary>
public sealed class PauseTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private DateOnly Today => DateOnly.FromDateTime(factory.Clock.GetUtcNow().UtcDateTime);

    /// <summary>Moves the clock to the next Monday 09:00; returns the days to shift the sample Plan so it starts the next day.</summary>
    private int AlignToMonday()
    {
        var now = factory.Clock.GetUtcNow();
        var next = now.Date.AddHours(9);
        while (next < now.UtcDateTime || next.DayOfWeek != DayOfWeek.Monday) next = next.AddDays(1);
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

    private static async Task<JsonElement[]> ActionsAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!;

    private static async Task<JsonElement> PauseAsync(HttpClient c, object body)
    {
        var res = await c.PostAsJsonAsync("/pauses", body);
        res.EnsureSuccessStatusCode();
        return await res.Content.ReadFromJsonAsync<JsonElement>();
    }

    private static string Id() => "p-" + Guid.NewGuid().ToString("N");

    [Fact]
    public async Task A_Pause_for_a_few_days_sets_aside_the_Actions_inside_it_and_Resume_brings_them_back()
    {
        var (c, _, _, shift) = await ApprovedPlanAsync();
        var firstFriday = new DateOnly(2026, 1, 9).AddDays(shift);

        var pause = await PauseAsync(c, new { clientId = Id(), endsOn = firstFriday.ToString("yyyy-MM-dd") });

        var actions = await ActionsAsync(c);
        var inside = actions.Where(a => DateOnly.Parse(a.GetProperty("date").GetString()!) <= firstFriday).ToList();
        Assert.NotEmpty(inside);
        Assert.All(inside, a => Assert.True(a.GetProperty("setAside").GetBoolean()));
        var after = actions.Where(a => DateOnly.Parse(a.GetProperty("date").GetString()!) > firstFriday).ToList();
        Assert.NotEmpty(after);
        Assert.All(after, a => Assert.False(a.GetProperty("setAside").GetBoolean()));

        (await c.PostAsync($"/pauses/{pause.GetProperty("id").GetGuid()}/resume", null)).EnsureSuccessStatusCode();
        Assert.All(await ActionsAsync(c), a => Assert.False(a.GetProperty("setAside").GetBoolean()));
    }

    private void AdvanceTo(DayOfWeek day, int hour, int minute = 0)
    {
        var now = factory.Clock.GetUtcNow();
        var at = now.Date.AddHours(hour).AddMinutes(minute);
        while (at <= now.UtcDateTime || at.DayOfWeek != day) at = at.AddDays(1);
        factory.Clock.Advance(at - now.UtcDateTime);
    }

    private async Task<JsonElement> RefreshAsync(Guid userId)
    {
        AdvanceTo(DayOfWeek.Sunday, 18, 1);
        await RunJobsAsync();
        return await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement>("/weekly-refresh");
    }

    [Fact]
    public async Task Set_aside_Actions_are_not_on_the_refreshs_unreported_list()
    {
        var (c, userId, _, shift) = await ApprovedPlanAsync();
        await PauseAsync(c, new { clientId = Id(), endsOn = new DateOnly(2026, 1, 9).AddDays(shift).ToString("yyyy-MM-dd") });

        var refresh = await RefreshAsync(userId);

        Assert.Equal(0, refresh.GetProperty("unreported").GetArrayLength());
    }

    [Fact]
    public async Task A_refresh_during_a_Pause_of_everything_proposes_nothing_and_sends_no_prompt()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        var token = "tok-" + Guid.NewGuid();
        (await c.PutAsJsonAsync("/me/push-token", new { token })).EnsureSuccessStatusCode();
        await PauseAsync(c, new { clientId = Id(), open = true });

        var refresh = await RefreshAsync(userId);

        Assert.Equal(0, refresh.GetProperty("proposals").GetArrayLength());
        Assert.DoesNotContain(factory.Push.Sent, m => m.Token == token);
    }

    private static async Task<JsonElement[]> WelcomesAsync(HttpClient c) =>
        (await c.GetFromJsonAsync<JsonElement[]>("/coach/messages"))!
            .Where(m => m.GetProperty("card").ValueKind == JsonValueKind.String && m.GetProperty("card").GetString() == "welcome_back").ToArray();

    [Fact]
    public async Task A_Pause_can_be_resumed_by_the_phones_own_client_ID_and_a_resend_starts_nothing_new()
    {
        var (c, _, _, _) = await ApprovedPlanAsync();
        var clientId = Id();
        var first = await PauseAsync(c, new { clientId, open = true });
        var again = await PauseAsync(c, new { clientId, open = true });
        Assert.Equal(first.GetProperty("id").GetGuid(), again.GetProperty("id").GetGuid());
        Assert.Single((await c.GetFromJsonAsync<JsonElement[]>("/pauses"))!, p => p.GetProperty("clientId").GetString() == clientId);

        (await c.PostAsync($"/pauses/by-client/{clientId}/resume", null)).EnsureSuccessStatusCode();

        Assert.DoesNotContain((await c.GetFromJsonAsync<JsonElement[]>("/pauses"))!, p => p.GetProperty("clientId").GetString() == clientId);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync("/pauses/by-client/nope/resume", null)).StatusCode);
    }

    [Fact]
    public async Task After_a_short_Pause_the_Coach_welcomes_back_once_and_suggests_Catch_up()
    {
        var (c, userId, _, shift) = await ApprovedPlanAsync();
        var last = new DateOnly(2026, 1, 9).AddDays(shift);
        await PauseAsync(c, new { clientId = Id(), endsOn = last.ToString("yyyy-MM-dd") });

        factory.Clock.Advance(TimeSpan.FromDays(3)); // Monday 09:00 -> Thursday: still inside the Pause
        await RunJobsAsync();
        Assert.Empty(await WelcomesAsync(await ReauthAsync(userId)));

        factory.Clock.Advance(TimeSpan.FromDays(2)); // Saturday: it ended at midnight after Friday
        await RunJobsAsync();
        await RunJobsAsync();

        var welcomes = await WelcomesAsync(await ReauthAsync(userId));
        var card = Assert.Single(welcomes).GetProperty("cardData");
        Assert.Equal("catch_up", card.GetProperty("suggested").GetString());
    }

    [Fact]
    public async Task After_a_long_Pause_the_welcome_back_suggests_shifting_the_dates_and_the_answer_is_kept()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        var pause = await PauseAsync(c, new { clientId = Id(), open = true });

        factory.Clock.Advance(TimeSpan.FromDays(20));
        c = await ReauthAsync(userId);
        (await c.PostAsync($"/pauses/{pause.GetProperty("id").GetGuid()}/resume", null)).EnsureSuccessStatusCode();
        await RunJobsAsync();

        var card = Assert.Single(await WelcomesAsync(c)).GetProperty("cardData");
        Assert.Equal("shift_dates", card.GetProperty("suggested").GetString());
        var answer = await c.PostAsJsonAsync($"/pauses/{pause.GetProperty("id").GetGuid()}/return", new { choice = "catch_up" });
        answer.EnsureSuccessStatusCode();
        Assert.Equal("catch_up", (await answer.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("returnChoice").GetString());
    }

    [Fact]
    public async Task Not_today_gets_no_welcome_back()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        await PauseAsync(c, new { clientId = Id() });

        factory.Clock.Advance(TimeSpan.FromDays(2));
        await RunJobsAsync();

        Assert.Empty(await WelcomesAsync(await ReauthAsync(userId)));
    }

    /// <summary>Runs the jobs due now and the ones they queue (a nudge queues its push).</summary>
    private async Task RunAllJobsAsync()
    {
        while (await RunJobsAsync() > 0) { }
    }

    private async Task<string> RegisterAsync(HttpClient c)
    {
        var token = "tok-" + Guid.NewGuid();
        (await c.PutAsJsonAsync("/me/push-token", new { token })).EnsureSuccessStatusCode();
        return token;
    }

    private List<Waypoint.Api.Push.PushMessage> SentTo(string token) => factory.Push.Sent.Where(m => m.Token == token && m.Data["route"] != "weekly_refresh").ToList();

    private async Task<string[]> SilenceLinesAsync(Guid userId) =>
        (await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/coach/messages"))!
            .Where(m => m.GetProperty("card").ValueKind == JsonValueKind.String && m.GetProperty("card").GetString() == "silence")
            .Select(m => m.GetProperty("text").GetString()!).ToArray();

    [Fact]
    public async Task Silence_brings_a_gentle_nudge_at_3_days_a_second_at_7_and_an_auto_Pause_at_14()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        var token = await RegisterAsync(c);

        factory.Clock.Advance(TimeSpan.FromDays(3) - TimeSpan.FromMinutes(1));
        await RunAllJobsAsync();
        Assert.Empty(SentTo(token));

        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunAllJobsAsync();
        Assert.Single(SentTo(token));
        await RunAllJobsAsync();
        Assert.Single(SentTo(token)); // one nudge, not one per check

        factory.Clock.Advance(TimeSpan.FromDays(4));
        await RunAllJobsAsync();
        var second = SentTo(token);
        Assert.Equal(2, second.Count);
        Assert.Equal("battery_settings", second[1].Data["hint"]);
        Assert.False(second[0].Data.ContainsKey("hint"));
        Assert.Empty(await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/pauses") ?? []);

        factory.Clock.Advance(TimeSpan.FromDays(7));
        await RunAllJobsAsync();
        Assert.Equal(3, SentTo(token).Count);
        Assert.Equal(3, (await SilenceLinesAsync(userId)).Length);
        var pauses = await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/pauses");
        var auto = Assert.Single(pauses!);
        Assert.Equal("coach", auto.GetProperty("startedBy").GetString());
        Assert.Equal(JsonValueKind.Null, auto.GetProperty("aspirationId").ValueKind);
        Assert.Equal(JsonValueKind.Null, auto.GetProperty("endsAt").ValueKind);

        // Nothing more follows, and the Actions in the Pause are set aside.
        factory.Clock.Advance(TimeSpan.FromDays(30));
        await RunAllJobsAsync();
        Assert.Equal(3, SentTo(token).Count);
    }

    [Fact]
    public async Task A_Check_in_or_an_app_open_starts_the_count_again()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        var token = await RegisterAsync(c);

        factory.Clock.Advance(TimeSpan.FromDays(2));
        c = await ReauthAsync(userId);
        (await c.PostAsync("/me/seen", null)).EnsureSuccessStatusCode();
        factory.Clock.Advance(TimeSpan.FromDays(2));
        await RunAllJobsAsync();
        Assert.Empty(SentTo(token));

        factory.Clock.Advance(TimeSpan.FromDays(1));
        await RunAllJobsAsync();
        Assert.Single(SentTo(token));
    }

    [Fact]
    public async Task A_refresh_prompt_is_not_sent_after_the_auto_Pause()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        var token = await RegisterAsync(c);

        factory.Clock.Advance(TimeSpan.FromDays(14));
        await RunAllJobsAsync();
        Assert.Single((await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/pauses"))!);

        await RefreshAsync(userId);
        Assert.DoesNotContain(factory.Push.Sent.Where(m => m.Token == token), m => m.Data["route"] == "weekly_refresh");
    }

    [Fact]
    public async Task While_the_Coach_is_late_no_nudge_is_faked_and_it_comes_when_the_model_is_back()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        var token = await RegisterAsync(c);

        factory.Clock.Advance(TimeSpan.FromDays(3));
        factory.Coach.Fail = true;
        await RunAllJobsAsync();
        factory.Coach.Fail = false;
        Assert.Empty(SentTo(token));
        Assert.Empty(await SilenceLinesAsync(userId));

        factory.Clock.Advance(TimeSpan.FromMinutes(2)); // the retry's backoff
        await RunAllJobsAsync();
        Assert.Single(SentTo(token));
    }

    [Fact]
    public async Task A_Pause_of_everything_silences_the_nudges()
    {
        var (c, _, _, _) = await ApprovedPlanAsync();
        var token = await RegisterAsync(c);
        await PauseAsync(c, new { clientId = Id(), open = true });

        factory.Clock.Advance(TimeSpan.FromDays(8));
        await RunAllJobsAsync();

        Assert.Empty(SentTo(token));
    }

    [Fact]
    public async Task The_first_week_back_from_a_Pause_runs_one_notch_lower_and_only_once()
    {
        var (c, userId, _, shift) = await ApprovedPlanAsync();
        await PauseAsync(c, new { clientId = Id(), endsOn = new DateOnly(2026, 1, 9).AddDays(shift).ToString("yyyy-MM-dd") });

        var refresh = await RefreshAsync(userId);
        var proposal = await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement>(
            $"/plan-versions/{refresh.GetProperty("proposals")[0].GetProperty("versionId").GetGuid()}");

        Assert.Equal("ease_back_return", proposal.GetProperty("changes").GetProperty("changes")[0].GetProperty("code").GetString());
        Assert.Equal(2, proposal.GetProperty("habits")[0].GetProperty("timesPerWeek").GetInt32());

        // The week after is no longer the first week back.
        var again = await RefreshAsync(userId);
        var next = await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement>(
            $"/plan-versions/{again.GetProperty("proposals")[0].GetProperty("versionId").GetGuid()}");
        Assert.DoesNotContain(next.GetProperty("changes").GetProperty("changes").EnumerateArray(), ch => ch.GetProperty("code").GetString() == "ease_back_return");
    }

    [Fact]
    public async Task Every_Aspiration_under_one_Pause_of_everything_gets_the_easier_first_week_back()
    {
        var (c, userId, _, shift) = await ApprovedPlanAsync();
        // A second Aspiration for the same user, with its own approved Plan.
        var hand = HandOff(settleFor: "2027-01-01");
        hand["aspiration"]!["hoped_for_date"] = "2026-12-20";
        var created = await c.PostAsJsonAsync("/interviews", new { ownWords = "learn the guitar" });
        var interviewId = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Coach.Enqueue($"Here is what I heard.\n{Marker}\n{hand.ToJsonString()}");
        (await c.PostAsJsonAsync($"/interviews/{interviewId}/messages", new { text = "Evenings", clientId = "m-2" })).EnsureSuccessStatusCode();
        var confirm = await c.PostAsJsonAsync($"/interviews/{interviewId}/confirm", new { shortName = "Guitar" });
        confirm.EnsureSuccessStatusCode();
        var second = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aspirationId").GetGuid();
        var plan = ShiftedPlan(shift);
        plan["tasks"]![1]!["minutes"] = 10;
        (await c.PostAsync($"/plan-versions/{await DraftedAsync(c, second, plan)}/approve", null)).EnsureSuccessStatusCode();

        await PauseAsync(c, new { clientId = Id(), endsOn = new DateOnly(2026, 1, 9).AddDays(shift).ToString("yyyy-MM-dd") });
        var refresh = await RefreshAsync(userId);

        var proposals = refresh.GetProperty("proposals").EnumerateArray().ToList();
        Assert.Equal(2, proposals.Count);
        foreach (var proposal in proposals)
        {
            var version = await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement>($"/plan-versions/{proposal.GetProperty("versionId").GetGuid()}");
            Assert.Equal("ease_back_return", version.GetProperty("changes").GetProperty("changes")[0].GetProperty("code").GetString());
        }
    }

    [Fact]
    public async Task The_users_return_ends_the_auto_Pause_and_refresh_prompts_come_back()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        factory.Clock.Advance(TimeSpan.FromDays(14));
        await RunAllJobsAsync();
        Assert.Single((await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/pauses"))!);

        (await (await ReauthAsync(userId)).PostAsync("/me/seen", null)).EnsureSuccessStatusCode(); // the user opens the app
        await RunAllJobsAsync();
        Assert.Empty((await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/pauses"))!);
        var refresh = await RefreshAsync(userId);
        Assert.NotEmpty(refresh.GetProperty("proposals").EnumerateArray()); // refreshes are back
    }

    [Fact]
    public async Task Asking_the_Coach_in_chat_to_step_back_starts_a_Pause_at_once()
    {
        var (c, userId, _, _) = await ApprovedPlanAsync();
        factory.Coach.PauseIntent = "{\"pause\": {\"aspirationId\": null, \"span\": \"until\", \"until\": \"" + Today.AddDays(3).ToString("yyyy-MM-dd") + "\"}}";
        try
        {
            var res = await c.PostAsJsonAsync("/coach/messages", new { text = "I'm on holiday for a few days", clientId = "chat-pause-1" });
            res.EnsureSuccessStatusCode();
            await res.Content.ReadAsStringAsync();
        }
        finally { factory.Coach.PauseIntent = null; }

        var pauses = (await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/pauses"))!;
        var pause = Assert.Single(pauses);
        Assert.Equal(JsonValueKind.Null, pause.GetProperty("aspirationId").ValueKind);
        Assert.Equal(Today.AddDays(4), DateOnly.FromDateTime(pause.GetProperty("endsAt").GetDateTime()));
        Assert.Contains(await ActionsAsync(c), a => a.GetProperty("setAside").GetBoolean());
        Assert.Contains(factory.Coach.Requests, r => r.Kind == Waypoint.Api.Coach.CoachCallKind.Chat && r.System.Contains("already started"));

        // A resend starts nothing new.
        await c.PostAsJsonAsync("/coach/messages", new { text = "I'm on holiday for a few days", clientId = "chat-pause-1" });
        Assert.Single((await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/pauses"))!);
    }
}
