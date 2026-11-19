using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed class GuideTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private static readonly string Body = string.Join(" ", Enumerable.Repeat("Warm up, run easy, walk when you need to.", 9));

    private static object Guide(string action, object[]? targets = null, object[]? withheld = null, string referral = "", string? body = null) => new
    {
        action, body = body ?? Body, targets = targets ?? [], withheld = withheld ?? [], referral,
    };

    private static object Target(string what, double low, double high, string unit = "min", string why = "a gentle start") => new { what, low, high, unit, why };

    private async Task<JsonElement> ActionAsync(HttpClient c, string title) =>
        (await c.GetFromJsonAsync<JsonElement>("/scheduled-actions")).EnumerateArray().First(a => a.GetProperty("title").GetString() == title);

    private async Task<(HttpClient Client, Guid AspirationId, Guid VersionId)> DraftWithGuidesAsync(JsonObject? handOff, params object[] replies)
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync(handOff);
        foreach (var r in replies) factory.Coach.EnqueueGuides(r);
        var versionId = await DraftedAsync(c, aspirationId, ShiftedPlan(0));
        await RunJobsAsync();
        return (c, aspirationId, versionId);
    }

    [Fact]
    public async Task A_Guide_written_with_the_Plan_reaches_the_Action_once_the_Plan_is_approved()
    {
        var (c, _, versionId) = await DraftWithGuidesAsync(null,
            new { guides = new[] { Guide("Easy run", [Target("Run time", 20, 30, "min", "you can already run 3 km")]) } });
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();

        var guide = (await ActionAsync(c, "Easy run")).GetProperty("guide");
        Assert.Contains("Warm up", guide.GetProperty("body").GetString());
        Assert.Equal(20, guide.GetProperty("targets")[0].GetProperty("low").GetDouble());
        Assert.Equal(JsonValueKind.Null, (await ActionAsync(c, "Buy running shoes")).GetProperty("guide").ValueKind);
    }

    [Fact]
    public async Task A_Guide_with_a_target_below_a_safe_floor_is_sent_back_and_never_stored()
    {
        factory.Coach.Requests.Clear();
        var low = Guide("Easy run", [Target("Daily calories", 1100, 1300, "kcal", "to lose weight faster")]);
        var (c, _, versionId) = await DraftWithGuidesAsync(null, new { guides = new[] { low } }, new { guides = new[] { low } });
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();

        var guideCalls = factory.Coach.Requests.Where(r => r.Kind == Waypoint.Api.Coach.CoachCallKind.Guide).ToList();
        Assert.Equal(2, guideCalls.Count); // asked again once with the problem
        Assert.Contains("below_safe_floor", guideCalls[1].Turns.Last().Text);
        Assert.Equal(JsonValueKind.Null, (await ActionAsync(c, "Easy run")).GetProperty("guide").ValueKind);
    }

    [Fact]
    public async Task The_floor_for_women_is_1200_and_a_deficit_over_500_is_rejected()
    {
        factory.Coach.Requests.Clear();
        var hand = HandOff();
        hand["aspiration"]!["domain_picture"] = new JsonObject { ["about"] = "38-year-old woman, 1.65 m, 80 kg" };
        var ok = Guide("Easy run", [Target("Daily calories", 1250, 1500, "kcal", "a gentle deficit")]);
        var (c, _, versionId) = await DraftWithGuidesAsync(hand, new { guides = new[] { ok } });
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        Assert.Equal(1250, (await ActionAsync(c, "Easy run")).GetProperty("guide").GetProperty("targets")[0].GetProperty("low").GetDouble());

        factory.Coach.Requests.Clear();
        var deficit = Guide("Easy run", [Target("Daily deficit", 400, 800, "kcal", "to lose faster")]);
        var (c2, _, v2) = await DraftWithGuidesAsync(null, new { guides = new[] { deficit } }, new { guides = new[] { deficit } });
        (await c2.PostAsync($"/plan-versions/{v2}/approve", null)).EnsureSuccessStatusCode();
        Assert.Contains("deficit_over_500", factory.Coach.Requests.Last(r => r.Kind == Waypoint.Api.Coach.CoachCallKind.Guide).Turns.Last().Text);
        Assert.Equal(JsonValueKind.Null, (await ActionAsync(c2, "Easy run")).GetProperty("guide").ValueKind);
    }

    [Fact]
    public async Task A_calorie_target_is_held_to_the_daily_floor_even_without_day_wording()
    {
        factory.Coach.Requests.Clear();
        var low = Guide("Easy run", [Target("Calories", 900, 1100, "kcal", "to lose weight faster")]);
        var (c, _, versionId) = await DraftWithGuidesAsync(null, new { guides = new[] { low } }, new { guides = new[] { low } });
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();

        Assert.Contains("below_safe_floor", factory.Coach.Requests.Last(r => r.Kind == Waypoint.Api.Coach.CoachCallKind.Guide).Turns.Last().Text);
        Assert.Equal(JsonValueKind.Null, (await ActionAsync(c, "Easy run")).GetProperty("guide").ValueKind);
    }

    private static JsonObject RedFlagHandOff()
    {
        var hand = HandOff();
        hand["aspiration"]!["domain_picture"] = new JsonObject
        {
            ["about"] = "46-year-old man", ["limits"] = "Type 2 diabetes on metformin; meniscus tear in the right knee, still sore on stairs",
        };
        return hand;
    }

    [Fact]
    public async Task A_red_flag_picture_gives_withheld_areas_and_one_referral_card()
    {
        var withheld = Guide("Easy run", [Target("Run time", 10, 20, "min")],
            [new { area = "leg load", reason = "your knee is still sore" }, new { area = "calories and carbs", reason = "diabetes medication" }],
            "Your knee and your tablets are worth a word with your physio and doctor before we set amounts.");
        var (c, _, versionId) = await DraftWithGuidesAsync(RedFlagHandOff(), new { guides = new[] { withheld } });
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();

        var guide = (await ActionAsync(c, "Easy run")).GetProperty("guide");
        Assert.Equal(["leg load", "calories and carbs"], guide.GetProperty("withheld").EnumerateArray().Select(w => w.GetProperty("area").GetString()));
        Assert.Contains("physio", guide.GetProperty("referral").GetString());
        Assert.DoesNotContain("physio", guide.GetProperty("body").GetString());
        // The rest of the Plan carries on: the Plan is approved and its other Actions are scheduled.
        Assert.NotEqual(default, await ActionAsync(c, "Buy running shoes"));
        var cards = (await c.GetFromJsonAsync<JsonElement[]>("/coach/messages"))!.Where(m => m.GetProperty("card").GetString() == "referral").ToList();
        Assert.Single(cards);
        Assert.Contains("physio", cards[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task A_Guide_that_withholds_without_a_referral_is_rejected()
    {
        factory.Coach.Requests.Clear();
        var bad = Guide("Easy run", withheld: [new { area = "leg load", reason = "knee" }]);
        var (c, _, versionId) = await DraftWithGuidesAsync(RedFlagHandOff(), new { guides = new[] { bad } }, new { guides = new[] { bad } });
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        Assert.Contains("referral_missing", factory.Coach.Requests.Last(r => r.Kind == Waypoint.Api.Coach.CoachCallKind.Guide).Turns.Last().Text);
        Assert.Equal(JsonValueKind.Null, (await ActionAsync(c, "Easy run")).GetProperty("guide").ValueKind);
    }

    private static async Task<List<(string Name, JsonElement Data)>> ChatAsync(HttpClient c, string text, Guid aspirationId)
    {
        var res = await c.PostAsJsonAsync("/coach/messages", new { text, clientId = "g-" + Guid.NewGuid().ToString("N"), aspirationId });
        var events = new List<(string, JsonElement)>();
        foreach (var block in (await res.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            events.Add((lines[0][7..], JsonDocument.Parse(lines[1][6..]).RootElement.Clone()));
        }
        return events;
    }

    private static string Edit(object guide, string reason = "you asked for something shorter") =>
        "\n<<GUIDE>>\n" + JsonSerializer.Serialize(new { action = "Easy run", reason, guide });

    [Fact]
    public async Task A_chat_request_edits_the_Guide_at_once_and_keeps_the_change_with_the_Plan_version()
    {
        var (c, aspirationId, versionId) = await DraftWithGuidesAsync(null, new { guides = new[] { Guide("Easy run") } });
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        var shorter = new { body = Body + " Keep it to twenty minutes.", targets = new object[] { Target("Run time", 15, 20) }, withheld = Array.Empty<object>(), referral = "" };
        factory.Coach.Enqueue("Done, it is shorter now.", Edit(shorter)[..9], Edit(shorter)[9..]); // the marker arrives split across chunks

        var events = await ChatAsync(c, "Can the run be shorter?", aspirationId);

        Assert.All(events.Where(e => e.Name == "delta"), e => Assert.DoesNotContain("GUIDE", e.Data.GetProperty("text").GetString()));
        Assert.Equal("done", events[^1].Name);
        var guide = (await ActionAsync(c, "Easy run")).GetProperty("guide");
        Assert.Contains("twenty minutes", guide.GetProperty("body").GetString());
        var messages = (await c.GetFromJsonAsync<JsonElement[]>("/coach/messages"))!;
        var reply = messages.Last(m => m.GetProperty("role").GetString() == "coach");
        Assert.Equal("Done, it is shorter now.", reply.GetProperty("text").GetString());
        Assert.Equal("guide", reply.GetProperty("card").GetString());
        var version = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{versionId}");
        var log = version.GetProperty("guideLog").EnumerateArray().Single();
        Assert.Equal("Easy run", log.GetProperty("action").GetString());
        Assert.Equal("chat", log.GetProperty("source").GetString());
    }

    [Fact]
    public async Task A_chat_edit_below_a_safe_floor_is_refused_and_the_Coach_is_late_not_faked()
    {
        var (c, aspirationId, versionId) = await DraftWithGuidesAsync(null, new { guides = new[] { Guide("Easy run") } });
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        var low = new { body = Body, targets = new object[] { Target("Daily calories", 900, 1100, "kcal") }, withheld = Array.Empty<object>(), referral = "" };
        factory.Coach.Enqueue("Sure." + Edit(low));

        var events = await ChatAsync(c, "Cut my calories", aspirationId);

        Assert.Equal("error", events[^1].Name);
        Assert.Empty((await ActionAsync(c, "Easy run")).GetProperty("guide").GetProperty("targets").EnumerateArray());
    }
}
