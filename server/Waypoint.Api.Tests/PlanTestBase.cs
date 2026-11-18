using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>Shared set-up for the Plan tests: sign in, run an Interview to an Aspiration, script and run the Plan job.</summary>
public abstract class PlanTestBase(WaypointApiFactory factory)
{
    protected readonly WaypointApiFactory factory = factory;

    protected const string Marker = "<<HANDOFF>>";

    protected async Task<(HttpClient Client, Guid UserId)> SignedInAsync()
    {
        var sub = "sub-" + Guid.NewGuid().ToString("N");
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/google", new { idToken = FakeGoogleVerifier.TokenFor(sub) });
        var tokens = (await response.Content.ReadFromJsonAsync<TokenPair>())!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        var me = await client.GetFromJsonAsync<JsonElement>("/me");
        var id = me.GetProperty("id").GetGuid();
        Subs[id] = sub;
        return (client, id);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, string> Subs = new();

    /// <summary>A fresh client for the same user, for after the fake clock has moved past the access token's life.</summary>
    protected async Task<HttpClient> ReauthAsync(Guid userId)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/google", new { idToken = FakeGoogleVerifier.TokenFor(Subs[userId]) });
        var tokens = (await response.Content.ReadFromJsonAsync<TokenPair>())!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    protected static JsonObject HandOff(string? settleFor = "2026-11-01") => new()
    {
        ["ready"] = true,
        ["aspiration"] = new JsonObject
        {
            ["aspiration"] = "run a half marathon", ["short_name"] = "Half marathon",
            ["picture"] = "You want to run 21 km by September.", ["success_looks_like"] = "finish 21 km",
            ["why_now"] = "turning 40", ["starting_point"] = "can run 3 km", ["share_hours_per_week"] = 3,
            ["hoped_for_date"] = "2026-09-20", ["settle_for_date"] = settleFor, ["on_skip"] = "gentle_check",
        },
        ["user"] = new JsonObject
        {
            ["slots"] = new JsonArray(new JsonObject
            {
                ["label"] = "weekday mornings", ["days"] = new JsonArray("mon", "wed", "fri"),
                ["start"] = "07:00", ["end"] = "07:45",
            }),
            ["time_budget_hours_per_week"] = 5, ["contact_frequency"] = "Gentle",
        },
    };

    /// <summary>A valid Plan for a start of 2026-01-06; each mutation breaks one hard rule.</summary>
    protected static JsonObject Plan(Action<JsonObject>? mutate = null)
    {
        var plan = new JsonObject
        {
            ["rationale"] = "Short sessions before work, because mornings worked for you.",
            ["milestones"] = new JsonArray(
                new JsonObject { ["id"] = "m1", ["title"] = "Run 5 km", ["date"] = "2026-02-02", ["kind"] = "ability", ["reached_when"] = "you can run 5 km without stopping" },
                new JsonObject { ["id"] = "m2", ["title"] = "Run 10 km", ["date"] = "2026-06-01", ["kind"] = "effort", ["reached_when"] = "you have done 12 long runs" }),
            ["habits"] = new JsonArray(new JsonObject
            {
                ["title"] = "Easy run", ["times_per_week"] = 3, ["minutes"] = 30, ["slots"] = new JsonArray("weekday mornings"),
            }),
            ["tasks"] = new JsonArray(new JsonObject { ["title"] = "Buy running shoes", ["milestone"] = "m1", ["minutes"] = 40, ["due"] = "this_week" },
                new JsonObject { ["title"] = "Book a physio visit", ["milestone"] = "m1", ["minutes"] = 20, ["due"] = "2026-01-16" }),
            ["first_two_weeks"] = new JsonArray(
                Act("2026-01-07", "Easy run", "habit", 30), Act("2026-01-09", "Easy run", "habit", 30),
                Act("2026-01-12", "Easy run", "habit", 30), Act("2026-01-14", "Easy run", "habit", 30),
                Act("2026-01-16", "Buy running shoes", "task", 40)),
            ["ramp"] = "One more session when the last two weeks went well.",
            ["assumptions"] = new JsonArray("You can run on Wednesdays."),
        };
        mutate?.Invoke(plan);
        return plan;
    }

    /// <summary>The valid Plan with every date moved a whole number of weeks on, so slot days still fit.</summary>
    protected static JsonObject ShiftedPlan(int days) => Plan(p =>
    {
        static string Move(string? d, int by) => d is null || !DateOnly.TryParseExact(d, "yyyy-MM-dd", out var x) ? d! : x.AddDays(by).ToString("yyyy-MM-dd");
        foreach (var a in p["first_two_weeks"]!.AsArray()) a!["date"] = Move(a["date"]!.GetValue<string>(), days);
        foreach (var t in p["tasks"]!.AsArray()) t!["due"] = Move(t["due"]!.GetValue<string>(), days);
    });

    protected static JsonObject Act(string date, string action, string kind, int minutes) => new()
    {
        ["date"] = date, ["slot"] = "weekday mornings", ["action"] = action, ["kind"] = kind, ["minutes"] = minutes,
    };

    protected async Task<(HttpClient Client, Guid UserId, Guid InterviewId, Guid AspirationId)> ConfirmedAsync(JsonObject? handOff = null)
    {
        var (c, userId) = await SignedInAsync();
        factory.Coach.Fail = false;
        var created = await c.PostAsJsonAsync("/interviews", new { ownWords = "run a half marathon" });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Coach.Enqueue($"Here is what I heard.\n{Marker}\n{(handOff ?? HandOff()).ToJsonString()}");
        (await c.PostAsJsonAsync($"/interviews/{id}/messages", new { text = "Mornings", clientId = "m-1" })).EnsureSuccessStatusCode();
        var confirm = await c.PostAsJsonAsync($"/interviews/{id}/confirm", new { shortName = (string?)null });
        confirm.EnsureSuccessStatusCode();
        var aspirationId = (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aspirationId").GetGuid();
        return (c, userId, id, aspirationId);
    }

    protected Task<int> RunJobsAsync() => factory.Services.GetRequiredService<JobRunner>().RunDueAsync(CancellationToken.None);

    protected void Script(JsonObject plan) => factory.Coach.Enqueue("```json\n" + plan.ToJsonString() + "\n```");

    protected static async Task<JsonElement> PlansAsync(HttpClient c, Guid aspirationId) =>
        (await c.GetFromJsonAsync<JsonElement>("/plans")).EnumerateArray().Single(p => p.GetProperty("aspirationId").GetGuid() == aspirationId);

    protected async Task<Guid> DraftedAsync(HttpClient c, Guid aspirationId, JsonObject? plan = null)
    {
        Script(plan ?? Plan());
        Assert.Equal(1, await RunJobsAsync());
        var p = await PlansAsync(c, aspirationId);
        return p.GetProperty("pendingVersionId").GetGuid();
    }

    protected async Task<Guid> ConfirmAndDraftAsync()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        return await DraftedAsync(c, aspirationId);
    }
}
