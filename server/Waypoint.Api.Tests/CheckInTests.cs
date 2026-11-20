using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Waypoint.Api.Coach;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed class CheckInTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private async Task<(HttpClient Client, Guid ActionId)> WithActionAsync()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var versionId = await DraftedAsync(c, aspirationId);
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        var first = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))![0];
        return (c, first.GetProperty("id").GetGuid());
    }

    private static Task<HttpResponseMessage> CheckIn(HttpClient c, Guid actionId, string clientId, string state, DateTime tappedAt,
        int? minutes = null, string[]? reasons = null, string? note = null) =>
        c.PutAsJsonAsync("/check-ins", new { scheduledActionId = actionId, clientId, state, minutes, reasons, note, tappedAt });

    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_Check_in_is_stored_with_its_tapped_at_time_and_shows_on_the_scheduled_Action()
    {
        var (c, actionId) = await WithActionAsync();

        var res = await CheckIn(c, actionId, "ci-1", "partly", T0, minutes: 20, reasons: ["tired"], note: "legs heavy");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);

        var action = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Single(a => a.GetProperty("id").GetGuid() == actionId);
        var ci = action.GetProperty("checkIn");
        Assert.Equal("partly", ci.GetProperty("state").GetString());
        Assert.Equal(20, ci.GetProperty("minutes").GetInt32());
        Assert.Equal("tired", ci.GetProperty("reasons")[0].GetString());
        Assert.Equal(T0, ci.GetProperty("tappedAt").GetDateTime().ToUniversalTime());
        Assert.Equal("07:00", action.GetProperty("slotStart").GetString());
    }

    [Fact]
    public async Task Sending_the_same_client_id_again_changes_nothing()
    {
        var (c, actionId) = await WithActionAsync();
        await CheckIn(c, actionId, "ci-1", "done", T0);
        await CheckIn(c, actionId, "ci-1", "done", T0);

        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.Single(thread!, m => m.GetProperty("card").GetString() == "check_in");
    }

    [Fact]
    public async Task A_later_tap_replaces_the_Check_in_and_an_earlier_one_arriving_late_is_ignored()
    {
        var (c, actionId) = await WithActionAsync();
        await CheckIn(c, actionId, "ci-1", "done", T0);
        await CheckIn(c, actionId, "ci-2", "skipped", T0.AddSeconds(10), reasons: ["no_time"]);
        await CheckIn(c, actionId, "ci-0", "partly", T0.AddSeconds(5), minutes: 10);

        var ci = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Single(a => a.GetProperty("id").GetGuid() == actionId).GetProperty("checkIn");
        Assert.Equal("skipped", ci.GetProperty("state").GetString());
        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.Single(thread!, m => m.GetProperty("card").GetString() == "check_in");
    }

    [Fact]
    public async Task Partly_requires_minutes_and_unknown_states_are_rejected()
    {
        var (c, actionId) = await WithActionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn(c, actionId, "a", "partly", T0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn(c, actionId, "b", "partly", T0, minutes: 0)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn(c, actionId, "c", "maybe", T0)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await CheckIn(c, Guid.NewGuid(), "d", "done", T0)).StatusCode);
    }

    [Fact]
    public async Task A_Check_in_with_a_note_and_one_without_get_replies_on_their_configured_models()
    {
        var (c, actionId) = await WithActionAsync();
        var secondId = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))![1].GetProperty("id").GetGuid();
        factory.Coach.Fail = false;

        // Other tests' replies come due on the same fake clock and would take a scripted reply: answer only this note.
        var responder = factory.Coach.Responder;
        factory.Coach.Responder = req => req.Kind == CoachCallKind.CheckInReplyWithNote && req.Turns.Any(t => t.Text.Contains("felt great"))
            ? ["Thanks for ", "telling me."]
            : responder?.Invoke(req);
        try
        {
            await CheckIn(c, actionId, "ci-1", "done", T0, note: "felt great");
            factory.Clock.Advance(TimeSpan.FromMinutes(1));
            await RunJobsAsync();
        }
        finally { factory.Coach.Responder = responder; }
        var withNote = factory.Coach.Requests.Last(r => r.Turns.Any(t => t.Text.Contains("felt great")));
        Assert.Equal(CoachCallKind.CheckInReplyWithNote, withNote.Kind);
        Assert.Equal(ConfiguredModel(CoachCallKind.CheckInReplyWithNote), withNote.Model);

        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.Equal("Thanks for telling me.", thread!.Last().GetProperty("text").GetString());
        Assert.Equal("coach", thread!.Last().GetProperty("role").GetString());

        factory.Coach.Enqueue("Nice.");
        await CheckIn(c, secondId, "ci-2", "done", T0.AddMinutes(1));
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        Assert.Equal(CoachCallKind.CheckInReplyNoNote, factory.Coach.Requests.Last().Kind);
        Assert.Equal(ConfiguredModel(CoachCallKind.CheckInReplyNoNote), factory.Coach.Requests.Last().Model);
    }

    private string ConfiguredModel(CoachCallKind kind) =>
        factory.Services.GetRequiredService<IOptions<CoachOptions>>().Value.For(kind).Model;

    [Fact]
    public async Task When_the_model_fails_the_Check_in_stays_and_no_reply_is_faked()
    {
        var (c, actionId) = await WithActionAsync();
        factory.Coach.Fail = true;
        try
        {
            await CheckIn(c, actionId, "ci-1", "done", T0);
            factory.Clock.Advance(TimeSpan.FromMinutes(1));
            await RunJobsAsync();
        }
        finally { factory.Coach.Fail = false; }

        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.DoesNotContain(thread!, m => m.GetProperty("role").GetString() == "coach" && m.GetProperty("card").ValueKind == JsonValueKind.Null);
        Assert.Contains(thread!, m => m.GetProperty("card").GetString() == "check_in");
    }

    [Fact]
    public async Task Undo_removes_the_Check_in_its_echo_and_the_pending_reply()
    {
        var (c, actionId) = await WithActionAsync();
        factory.Coach.Fail = false;
        await CheckIn(c, actionId, "ci-1", "done", T0);
        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/scheduled-actions/{actionId}/check-in")).StatusCode);

        var action = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Single(a => a.GetProperty("id").GetGuid() == actionId);
        Assert.Equal(JsonValueKind.Null, action.GetProperty("checkIn").ValueKind);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();
        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.DoesNotContain(thread!, m => m.GetProperty("card").GetString() == "check_in");
        Assert.DoesNotContain(thread!, m => m.GetProperty("role").GetString() == "coach" && m.GetProperty("card").ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Changing_a_Check_in_drops_the_old_Coach_reply_and_queues_a_reply_to_the_new_state()
    {
        var (c, actionId) = await WithActionAsync();
        factory.Coach.Fail = false;
        factory.Coach.Enqueue("Great job!");
        await CheckIn(c, actionId, "ci-1", "done", T0);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();

        factory.Coach.Enqueue("That's okay.");
        await CheckIn(c, actionId, "ci-2", "skipped", T0.AddSeconds(10), reasons: ["no_time"]);
        var mid = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.DoesNotContain(mid!, m => m.GetProperty("text").GetString() == "Great job!");
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await RunJobsAsync();

        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.DoesNotContain(thread!, m => m.GetProperty("text").GetString() == "Great job!");
        Assert.Equal("That's okay.", thread!.Last().GetProperty("text").GetString());
    }

    [Fact]
    public async Task Unknown_reasons_too_many_reasons_and_a_long_note_are_rejected_not_a_server_error()
    {
        var (c, actionId) = await WithActionAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn(c, actionId, "a", "skipped", T0, reasons: [new string('x', 300)])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn(c, actionId, "b", "skipped", T0, reasons: ["free text"])).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn(c, actionId, "c", "skipped", T0, reasons: Enumerable.Repeat("tired", 40).ToArray())).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await CheckIn(c, actionId, "d", "done", T0, note: new string('n', 5000))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await CheckIn(c, actionId, "e", "skipped", T0, reasons: ["something_else"], note: "other")).StatusCode);
    }

    [Fact]
    public async Task Concurrent_first_Check_ins_for_one_Action_do_not_fail()
    {
        var (c, actionId) = await WithActionAsync();
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(i =>
            CheckIn(c, actionId, $"ci-{i}", "done", T0.AddSeconds(i))));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.Single(thread!, m => m.GetProperty("card").GetString() == "check_in");
    }
}
