using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed class InterviewTests(WaypointApiFactory factory) : IClassFixture<WaypointApiFactory>
{
    private const string Marker = "<<HANDOFF>>";

    private async Task<HttpClient> SignedInClientAsync()
    {
        var sub = "sub-" + Guid.NewGuid().ToString("N");
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/google",
            new { idToken = FakeGoogleVerifier.TokenFor(sub) });
        var tokens = (await response.Content.ReadFromJsonAsync<TokenPair>())!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static JsonObject ValidHandOff(bool withUser = true, string shortName = "Half marathon")
    {
        var h = new JsonObject
        {
            ["ready"] = true,
            ["aspiration"] = new JsonObject
            {
                ["aspiration"] = "run a half marathon",
                ["short_name"] = shortName,
                ["short_name_suggestions"] = new JsonArray("Half marathon", "21K"),
                ["picture"] = "You want to run 21 km by September.",
                ["success_looks_like"] = "finish 21 km without walking",
                ["why_now"] = "turning 40",
                ["starting_point"] = "can run 3 km",
                ["past_attempts"] = new JsonArray(new JsonObject
                {
                    ["what"] = "couch to 5k", ["what_worked"] = "mornings", ["what_derailed"] = "a cold",
                }),
                ["share_hours_per_week"] = 3,
                ["constraints"] = new JsonArray("bad left knee"),
                ["domain_picture"] = new JsonObject
                {
                    ["routine"] = "desk job", ["skill"] = "beginner",
                    ["limits"] = new JsonArray("no gym"), ["likes"] = new JsonArray("trails"),
                    ["dislikes"] = new JsonArray("treadmills"),
                },
                ["hoped_for_date"] = "2026-09-20",
                ["settle_for_date"] = "2026-11-01",
                ["settle_for_picture"] = "a solid 10k",
                ["on_skip"] = "gentle_check",
                ["open_questions"] = new JsonArray("which race?"),
            },
        };
        if (withUser)
            h["user"] = new JsonObject
            {
                ["slots"] = new JsonArray(new JsonObject
                {
                    ["label"] = "weekday mornings",
                    ["days"] = new JsonArray("mon", "wed", "fri"), ["start"] = "07:00", ["end"] = "07:45",
                }),
                ["time_budget_hours_per_week"] = 5,
                ["contact_frequency"] = "Gentle",
            };
        return h;
    }

    private static string Reply(string text, JsonObject? handOff = null) =>
        handOff is null ? text : $"{text}\n{Marker}\n{handOff.ToJsonString()}";

    private static async Task<List<SseEvent>> SseAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        var events = new List<SseEvent>();
        foreach (var block in (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))[7..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))[6..];
            events.Add(new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone()));
        }
        return events;
    }

    private static async Task<Guid> StartAsync(HttpClient c, string words = "run a half marathon")
    {
        var r = await c.PostAsJsonAsync("/interviews", new { ownWords = words });
        r.EnsureSuccessStatusCode();
        return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
    }

    private static async Task<List<SseEvent>> SayAsync(HttpClient c, Guid id, string text, string clientId) =>
        await SseAsync(await c.PostAsJsonAsync($"/interviews/{id}/messages", new { text, clientId }));

    private static Task<JsonElement> GetAsync(HttpClient c, Guid id) =>
        c.GetFromJsonAsync<JsonElement>($"/interviews/{id}");

    /// <summary>Runs an Interview to a valid hand-off awaiting confirmation.</summary>
    private async Task<Guid> RunToHandOffAsync(HttpClient c, JsonObject? handOff = null)
    {
        factory.Coach.Fail = false;
        var id = await StartAsync(c);
        factory.Coach.Enqueue("What would done look like?");
        await SayAsync(c, id, "I want to run 21 km", "m-1");
        factory.Coach.Enqueue(Reply("Here is what I heard.", handOff ?? ValidHandOff()));
        await SayAsync(c, id, "Mornings, three days", "m-2");
        return id;
    }

    [Fact]
    public async Task An_Interview_runs_to_a_hand_off_and_waits_for_confirmation()
    {
        factory.Coach.Fail = false;
        var c = await SignedInClientAsync();
        var id = await StartAsync(c);
        Assert.Equal("in_progress", (await GetAsync(c, id)).GetProperty("status").GetString());

        factory.Coach.Enqueue("What would ", "done look like?");
        var first = await SayAsync(c, id, "I want to run 21 km", "m-1");
        Assert.Equal(["delta", "delta", "done"], first.Select(e => e.Name));
        Assert.Equal("in_progress", first[^1].Data.GetProperty("status").GetString());

        factory.Coach.Enqueue(Reply("Here is what I heard.", ValidHandOff()));
        var last = await SayAsync(c, id, "Mornings, three days", "m-2");

        Assert.DoesNotContain(last.Where(e => e.Name == "delta"), e => e.Data.GetProperty("text").GetString()!.Contains("ready"));
        Assert.Equal("awaiting_confirmation", last[^1].Data.GetProperty("status").GetString());
        var view = await GetAsync(c, id);
        Assert.Equal("awaiting_confirmation", view.GetProperty("status").GetString());
        Assert.Equal("Half marathon", view.GetProperty("handOff").GetProperty("aspiration").GetProperty("short_name").GetString());
        Assert.Equal(4, view.GetProperty("messages").GetArrayLength());
        Assert.Equal("Here is what I heard.", view.GetProperty("messages").EnumerateArray().Last().GetProperty("text").GetString());
    }

    [Fact]
    public async Task Leaving_and_resuming_continues_the_same_transcript_and_the_Coach_recaps()
    {
        factory.Coach.Fail = false;
        var c = await SignedInClientAsync();
        var id = await StartAsync(c);
        factory.Coach.Enqueue("What would done look like?");
        await SayAsync(c, id, "I want to run 21 km", "m-1");

        // The phone is closed; later the same Interview is found and resumed.
        var current = await c.GetFromJsonAsync<JsonElement>("/interviews/current");
        Assert.Equal(id, current.GetProperty("id").GetGuid());
        Assert.Equal(id, await StartAsync(c, "something else"));
        factory.Coach.Enqueue("Welcome back. You want to run 21 km. What would done look like?");
        var events = await SseAsync(await c.PostAsync($"/interviews/{id}/resume", null));

        Assert.Equal("done", events[^1].Name);
        var sent = factory.Coach.Requests[^1];
        Assert.Contains(sent.Turns, t => t.Role == "user" && t.Text == "I want to run 21 km");
        Assert.Contains(sent.Turns, t => t.Role == "assistant" && t.Text == "What would done look like?");
        Assert.Contains("repeat", sent.Turns[^1].Text);
        var view = await GetAsync(c, id);
        Assert.Equal(["user", "coach", "coach"], view.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
    }

    [Fact]
    public async Task Confirming_creates_the_Aspiration_saves_the_user_block_and_posts_the_summary_card()
    {
        var c = await SignedInClientAsync();
        var id = await RunToHandOffAsync(c);

        var response = await c.PostAsJsonAsync($"/interviews/{id}/confirm", new { shortName = "21K" });

        response.EnsureSuccessStatusCode();
        var created = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("21K", created.GetProperty("shortName").GetString());
        Assert.Equal("coral", created.GetProperty("hue").GetString());
        Assert.Equal("confirmed", (await GetAsync(c, id)).GetProperty("status").GetString());
        var aspirations = await c.GetFromJsonAsync<JsonElement>("/aspirations");
        var a = aspirations.EnumerateArray().Single();
        Assert.Equal("active", a.GetProperty("status").GetString());
        Assert.Equal("2026-09-20", a.GetProperty("hopedForDate").GetString());
        var me = await c.GetFromJsonAsync<JsonElement>("/me/setup");
        Assert.Equal(5, me.GetProperty("timeBudgetHours").GetDouble());
        Assert.Equal("Gentle", me.GetProperty("contactFrequency").GetString());
        var slot = me.GetProperty("slots").EnumerateArray().Single();
        Assert.Equal("07:00", slot.GetProperty("start").GetString());
        Assert.Equal(["mon", "wed", "fri"], slot.GetProperty("days").EnumerateArray().Select(d => d.GetString()));
        var thread = await c.GetFromJsonAsync<JsonElement>("/coach/messages");
        var card = thread.EnumerateArray().Single(m => m.GetProperty("card").ValueKind == JsonValueKind.String);
        Assert.Equal("interview_summary", card.GetProperty("card").GetString());
        Assert.Equal("21K", card.GetProperty("cardData").GetProperty("shortName").GetString());
        // Confirming twice does not create a second one.
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/interviews/{id}/confirm", new { })).StatusCode);
    }

    [Fact]
    public async Task A_later_Interview_does_not_ask_for_or_overwrite_the_user_block()
    {
        var c = await SignedInClientAsync();
        await c.PostAsJsonAsync($"/interviews/{await RunToHandOffAsync(c)}/confirm", new { });

        var second = await StartAsync(c, "learn Spanish");
        factory.Coach.Enqueue(Reply("Here it is.", ValidHandOff(withUser: false, shortName: "Spanish")));
        await SayAsync(c, second, "B1 by next summer", "n-1");
        Assert.DoesNotContain("Slots: the recurring", factory.Coach.Requests[^1].System);
        var response = await c.PostAsJsonAsync($"/interviews/{second}/confirm", new { });

        response.EnsureSuccessStatusCode();
        Assert.Equal("teal", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("hue").GetString());
        var me = await c.GetFromJsonAsync<JsonElement>("/me/setup");
        Assert.Equal(1, me.GetProperty("slots").GetArrayLength());
    }

    public static TheoryData<string> InvalidHandOffs => new()
    {
        "no_hoped_for_date", "no_settle_for", "short_name_too_long", "slot_without_window", "slot_start_after_end", "no_user_block", "slot_days_not_strings",
    };

    private static JsonObject Break(string how)
    {
        var h = ValidHandOff();
        var a = h["aspiration"]!.AsObject();
        switch (how)
        {
            case "no_hoped_for_date": a.Remove("hoped_for_date"); break;
            case "no_settle_for": a.Remove("settle_for_date"); a.Remove("settle_for_picture"); break;
            case "short_name_too_long": a["short_name"] = "Half marathon in September"; break;
            case "slot_without_window": h["user"]!["slots"]![0]!.AsObject().Remove("start"); break;
            case "slot_start_after_end": h["user"]!["slots"]![0]!["start"] = "09:00"; break;
            case "no_user_block": h.Remove("user"); break;
            case "slot_days_not_strings": h["user"]!["slots"]![0]!["days"] = new JsonArray(1, 2); break;
        }
        return h;
    }

    [Theory]
    [MemberData(nameof(InvalidHandOffs))]
    public async Task An_invalid_hand_off_is_rejected_the_Coach_is_asked_again_and_nothing_is_created(string how)
    {
        factory.Coach.Fail = false;
        var c = await SignedInClientAsync();
        var id = await StartAsync(c);
        factory.Coach.Enqueue(Reply("Here it is.", Break(how)));
        factory.Coach.Enqueue(Reply("Here it is.", Break(how)));
        factory.Coach.Enqueue(Reply("Here it is.", Break(how)));
        var before = factory.Coach.Requests.Count;

        var events = await SayAsync(c, id, "answer", "m-1");

        Assert.Equal("error", events[^1].Name);
        Assert.Equal(3, factory.Coach.Requests.Count - before);
        Assert.Contains("rejected", factory.Coach.Requests[^1].Turns[^1].Text);
        Assert.Equal("in_progress", (await GetAsync(c, id)).GetProperty("status").GetString());
        Assert.Equal(System.Net.HttpStatusCode.Conflict, (await c.PostAsJsonAsync($"/interviews/{id}/confirm", new { })).StatusCode);
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/aspirations")).GetArrayLength());
    }

    [Fact]
    public async Task When_the_Coach_fixes_the_hand_off_when_asked_again_the_Interview_reaches_confirmation()
    {
        factory.Coach.Fail = false;
        var c = await SignedInClientAsync();
        var id = await StartAsync(c);
        factory.Coach.Enqueue(Reply("Here it is.", Break("no_hoped_for_date")));
        factory.Coach.Enqueue(Reply("Here it is, fixed.", ValidHandOff()));

        var events = await SayAsync(c, id, "answer", "m-1");

        Assert.Equal("awaiting_confirmation", events[^1].Data.GetProperty("status").GetString());
        Assert.Equal(["delta", "done"], events.Select(e => e.Name).Distinct());
    }

    [Fact]
    public async Task A_short_name_over_16_characters_is_rejected_at_confirmation()
    {
        var c = await SignedInClientAsync();
        var id = await RunToHandOffAsync(c);

        var response = await c.PostAsJsonAsync($"/interviews/{id}/confirm", new { shortName = "A very long short name" });

        Assert.Equal(System.Net.HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.Equal(0, (await c.GetFromJsonAsync<JsonElement>("/aspirations")).GetArrayLength());
    }

    [Fact]
    public async Task Correcting_something_reopens_the_Interview_and_the_picture_updates()
    {
        var c = await SignedInClientAsync();
        var id = await RunToHandOffAsync(c);

        var changed = ValidHandOff();
        changed["aspiration"]!["picture"] = "You want to run 21 km by October.";
        factory.Coach.Enqueue(Reply("Fixed.", changed));
        var events = await SayAsync(c, id, "No, October", "m-3");

        Assert.Equal("awaiting_confirmation", events[^1].Data.GetProperty("status").GetString());
        var picture = (await GetAsync(c, id)).GetProperty("handOff").GetProperty("aspiration").GetProperty("picture").GetString();
        Assert.Equal("You want to run 21 km by October.", picture);
    }

    [Fact]
    public async Task A_failed_call_leaves_the_message_saved_and_the_resend_gets_the_reply_without_free_text_in_logs()
    {
        var c = await SignedInClientAsync();
        var id = await StartAsync(c);
        factory.Coach.Fail = true;
        var failed = await SayAsync(c, id, "my-secret-words-qwerty", "m-1");
        Assert.Equal(["error"], failed.Select(e => e.Name));

        factory.Coach.Fail = false;
        factory.Coach.Enqueue("Thanks.");
        var retry = await SayAsync(c, id, "my-secret-words-qwerty", "m-1");

        Assert.Equal("done", retry[^1].Name);
        var messages = (await GetAsync(c, id)).GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal(["user", "coach"], messages.Select(m => m.GetProperty("role").GetString()));
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("qwerty") || l.Contains("21 km"));
    }

    [Fact]
    public async Task Another_user_never_sees_an_Interview()
    {
        var mine = await SignedInClientAsync();
        var id = await StartAsync(mine);
        Assert.Equal(System.Net.HttpStatusCode.NotFound, (await (await SignedInClientAsync()).GetAsync($"/interviews/{id}")).StatusCode);
    }
}
