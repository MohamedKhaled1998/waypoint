using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Coach;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>Moves the fake clock by days, so it has its own factory and clock apart from the other Plan tests.</summary>
public sealed class PlanWindowTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    [Fact]
    public async Task Approving_a_day_later_still_schedules_the_whole_window_and_a_week_later_asks_for_a_fresh_draft()
    {
        // Drafted on 2026-01-05, so the window is 2026-01-06 to 2026-01-19. Approved on 2026-01-06: all of it is still ahead.
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        var versionId = await DraftedAsync(c, aspirationId);
        factory.Clock.Advance(TimeSpan.FromDays(1));
        c = await ReauthAsync(userId);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).StatusCode);
        var scheduled = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!;
        Assert.Equal(["2026-01-07", "2026-01-09", "2026-01-12", "2026-01-14", "2026-01-16"], scheduled.Select(a => a.GetProperty("date").GetString()));

        // A second Aspiration drafted on 2026-01-06 (window from 01-07), approved on 2026-01-13, after its window began.
        var (c2, userId2, _, aspiration2) = await ConfirmedAsync();
        var stale = await DraftedAsync(c2, aspiration2);
        factory.Clock.Advance(TimeSpan.FromDays(7));
        c2 = await ReauthAsync(userId2);
        var rejected = await c2.PostAsync($"/plan-versions/{stale}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        var body = await rejected.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stale", body.GetProperty("error").GetString());
        Assert.Equal("window_started", body.GetProperty("reason").GetString());
        Assert.Empty(await c2.GetFromJsonAsync<JsonElement[]>("/scheduled-actions") ?? []);
        Assert.True((await PlansAsync(c2, aspiration2)).GetProperty("drafting").GetBoolean());

        // The fresh draft starts on 2026-01-14; approving it schedules the full two weeks from there.
        Script(ShiftedPlan(7));
        Assert.Equal(1, await RunJobsAsync());
        var fresh = (await PlansAsync(c2, aspiration2)).GetProperty("pendingVersionId").GetGuid();
        Assert.NotEqual(stale, fresh);
        Assert.Equal(HttpStatusCode.OK, (await c2.PostAsync($"/plan-versions/{fresh}/approve", null)).StatusCode);
        var second = (await c2.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!;
        Assert.Equal(["2026-01-14", "2026-01-16", "2026-01-19", "2026-01-21", "2026-01-23"], second.Select(a => a.GetProperty("date").GetString()));
    }
}
