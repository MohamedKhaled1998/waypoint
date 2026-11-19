using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Coach;
using Waypoint.Api.Push;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed class PushTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private static async Task Register(HttpClient c, string token) =>
        (await c.PutAsJsonAsync("/me/push-token", new { token })).EnsureSuccessStatusCode();

    private List<PushMessage> SentTo(string token) => factory.Push.Sent.Where(m => m.Token == token).ToList();

    [Fact]
    public async Task A_finished_Plan_job_sends_Plan_ready_to_the_registered_token()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var token = "tok-" + Guid.NewGuid();
        await Register(c, token);

        var versionId = await DraftedAsync(c, aspirationId);

        var push = Assert.Single(SentTo(token));
        Assert.Equal("Plan ready", push.Title);
        Assert.Equal("plan_ready", push.Data["route"]);
        Assert.Equal(versionId.ToString(), push.Data["planVersionId"]);
    }

    [Fact]
    public async Task No_push_without_a_registered_token()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var before = factory.Push.Sent.Count;
        await DraftedAsync(c, aspirationId);
        Assert.Equal(before, factory.Push.Sent.Count);
    }

    [Fact]
    public async Task An_unprompted_Coach_message_sends_a_push_that_opens_the_chat()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var token = "tok-" + Guid.NewGuid();
        await Register(c, token);

        using (var scope = factory.Services.CreateScope())
        {
            // The message's owner is the signed-in user of the scope: the job runner's way of acting for a user.
            var me = await c.GetFromJsonAsync<JsonElement>("/me");
            scope.ServiceProvider.GetRequiredService<Waypoint.Api.Auth.CurrentUser>().Override = me.GetProperty("id").GetGuid();
            await scope.ServiceProvider.GetRequiredService<CoachMessenger>().PostUnpromptedAsync(new GateDecision.Send(), aspirationId, "How did the run go?", CancellationToken.None);
        }
        await RunJobsAsync();

        var push = Assert.Single(SentTo(token));
        Assert.Equal("coach", push.Data["route"]);
        Assert.Equal("How did the run go?", push.Body);
        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.Contains(thread!, m => m.GetProperty("text").GetString() == "How did the run go?");
    }

    [Fact]
    public async Task An_invalid_token_is_dropped_after_the_push_fails()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var token = "dead-" + Guid.NewGuid();
        factory.Push.Invalid[token] = true;
        await Register(c, token);

        await DraftedAsync(c, aspirationId);
        Assert.Single(SentTo(token));

        // A later push finds no token to send to.
        using (var scope = factory.Services.CreateScope())
        {
            var me = await c.GetFromJsonAsync<JsonElement>("/me");
            scope.ServiceProvider.GetRequiredService<Waypoint.Api.Auth.CurrentUser>().Override = me.GetProperty("id").GetGuid();
            await scope.ServiceProvider.GetRequiredService<CoachMessenger>().PostUnpromptedAsync(new GateDecision.Send(), aspirationId, "Hello again", CancellationToken.None);
        }
        await RunJobsAsync();
        Assert.Single(SentTo(token));
    }

    [Fact]
    public async Task Registering_again_replaces_the_token_and_signed_out_requests_are_refused()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var old = "old-" + Guid.NewGuid();
        var fresh = "new-" + Guid.NewGuid();
        await Register(c, old);
        await Register(c, fresh);

        await DraftedAsync(c, aspirationId);
        Assert.Empty(SentTo(old));
        Assert.Single(SentTo(fresh));

        var anon = await factory.CreateClient().PutAsJsonAsync("/me/push-token", new { token = "x" });
        Assert.Equal(HttpStatusCode.Unauthorized, anon.StatusCode);
        var blank = await c.PutAsJsonAsync("/me/push-token", new { token = " " });
        Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
    }
}
