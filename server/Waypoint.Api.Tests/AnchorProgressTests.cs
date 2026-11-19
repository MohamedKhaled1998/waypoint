using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Coach;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed class AnchorProgressTests(WaypointApiFactory factory) : AnchorTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private async Task<HttpClient> CheckInAllAsync(Guid userId, Func<JsonElement, string> stateOf)
    {
        var c = await ReauthAsync(userId);
        foreach (var a in (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!)
            (await c.PutAsJsonAsync("/check-ins", new
            {
                scheduledActionId = a.GetProperty("id").GetGuid(), clientId = "ci-" + Guid.NewGuid().ToString("N"), state = stateOf(a),
                tappedAt = factory.Clock.GetUtcNow().UtcDateTime,
            })).EnsureSuccessStatusCode();
        return c;
    }

    [Fact]
    public async Task Anchor_habits_stay_out_of_the_Time_budget_and_Pace_but_count_in_consistency_and_tolerance()
    {
        var (_, userId, aspirationId, _) = await ApprovedAsync(PlanWithAnchor());
        factory.Clock.Advance(new DateTime(2026, 1, 17, 9, 0, 0, DateTimeKind.Utc) - factory.Clock.GetUtcNow().UtcDateTime);
        var c = await CheckInAllAsync(userId, a => a.GetProperty("title").GetString() == "Walk after dinner" ? "skipped" : "done");

        var p = await c.GetFromJsonAsync<JsonElement>($"/aspirations/{aspirationId}/progress");

        Assert.Equal(90, p.GetProperty("plannedMinutesPerWeek").GetInt32()); // three 30-minute runs; the walks add nothing
        Assert.Equal(1.0, p.GetProperty("pace").GetDouble(), 3);             // skipped walks do not drag Pace down
        Assert.Equal(5.0 / 7, p.GetProperty("tolerance").GetProperty("share").GetDouble(), 3); // but they count as sessions here
        Assert.Equal("absorb", p.GetProperty("tolerance").GetProperty("band").GetString());
        var walk = p.GetProperty("habits").EnumerateArray().Single(h => h.GetProperty("title").GetString() == "Walk after dinner");
        Assert.Equal(0, walk.GetProperty("done").GetInt32());
        Assert.Equal(2, walk.GetProperty("planned").GetInt32());
        Assert.Equal("after dinner", walk.GetProperty("anchor").GetProperty("words").GetString());
    }
}
