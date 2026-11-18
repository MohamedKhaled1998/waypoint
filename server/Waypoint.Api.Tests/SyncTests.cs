using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed class SyncTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private async Task<(HttpClient Client, Guid ActionId)> WithActionAsync()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var versionId = await DraftedAsync(c, aspirationId);
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        var first = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))![0];
        return (c, first.GetProperty("id").GetGuid());
    }

    private static async Task<JsonElement> PullAsync(HttpClient c, long cursor) =>
        await c.GetFromJsonAsync<JsonElement>($"/sync?cursor={cursor}");

    private static JsonElement[] Of(JsonElement pull, string kind) =>
        pull.GetProperty("changes").EnumerateArray().Where(x => x.GetProperty("kind").GetString() == kind).ToArray();

    private static Task<HttpResponseMessage> CheckIn(HttpClient c, Guid actionId, string clientId, string state, DateTime tappedAt) =>
        c.PutAsJsonAsync("/check-ins", new { scheduledActionId = actionId, clientId, state, minutes = (int?)null, reasons = new string[0], note = (string?)null, tappedAt });

    private static readonly DateTime T0 = new(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc);

    [Fact]
    public async Task A_first_pull_returns_the_scheduled_Actions_and_Milestones_and_a_later_pull_returns_only_what_changed()
    {
        var (c, actionId) = await WithActionAsync();

        var first = await PullAsync(c, 0);
        Assert.Equal(5, Of(first, "scheduled_action").Length);
        Assert.Equal(2, Of(first, "milestone").Length);
        var cursor = first.GetProperty("cursor").GetInt64();

        var quiet = await PullAsync(c, cursor);
        Assert.Empty(quiet.GetProperty("changes").EnumerateArray());
        Assert.Equal(cursor, quiet.GetProperty("cursor").GetInt64());

        await CheckIn(c, actionId, "ci-1", "done", T0);
        var after = await PullAsync(c, cursor);
        var changed = Of(after, "scheduled_action").Single();
        Assert.Equal(actionId, changed.GetProperty("id").GetGuid());
        Assert.Equal("done", changed.GetProperty("data").GetProperty("checkIn").GetProperty("state").GetString());
        Assert.Contains(Of(after, "chat_message"), m => m.GetProperty("data").GetProperty("card").GetString() == "check_in");
        Assert.Empty(Of(after, "milestone"));
        Assert.True(after.GetProperty("cursor").GetInt64() > cursor);
    }

    [Fact]
    public async Task An_undone_Check_in_comes_back_as_a_change_on_the_Action_and_a_deleted_echo()
    {
        var (c, actionId) = await WithActionAsync();
        await CheckIn(c, actionId, "ci-1", "done", T0);
        var cursor = (await PullAsync(c, 0)).GetProperty("cursor").GetInt64();

        (await c.DeleteAsync($"/scheduled-actions/{actionId}/check-in")).EnsureSuccessStatusCode();
        var after = await PullAsync(c, cursor);
        Assert.Equal(JsonValueKind.Null, Of(after, "scheduled_action").Single().GetProperty("data").GetProperty("checkIn").ValueKind);
        Assert.True(Of(after, "chat_message").Single().GetProperty("deleted").GetBoolean());
    }

    [Fact]
    public async Task A_pull_shows_only_the_signed_in_users_changes()
    {
        var (c, _) = await WithActionAsync();
        var (other, _) = await SignedInAsync();
        Assert.Empty((await PullAsync(other, 0)).GetProperty("changes").EnumerateArray());
        Assert.NotEmpty((await PullAsync(c, 0)).GetProperty("changes").EnumerateArray());
    }

    [Fact]
    public async Task A_Coach_reply_marks_the_message_it_answers_as_changed()
    {
        var (c, actionId) = await WithActionAsync();
        await CheckIn(c, actionId, "ci-1", "done", T0);
        var cursor = (await PullAsync(c, 0)).GetProperty("cursor").GetInt64();
        factory.Coach.Fail = false;
        factory.Coach.Enqueue("Nice work.");
        factory.Clock.Advance(TimeSpan.FromSeconds(10));
        await RunJobsAsync();

        var after = await PullAsync(c, cursor);
        var messages = Of(after, "chat_message");
        Assert.Contains(messages, m => m.GetProperty("data").GetProperty("role").GetString() == "coach");
        Assert.Contains(messages, m => m.GetProperty("data").GetProperty("card").GetString() == "check_in"
            && m.GetProperty("data").GetProperty("replied").GetBoolean());
    }

    [Fact]
    public async Task Moving_one_Action_to_another_Slot_time_is_idempotent_by_client_id_and_syncs()
    {
        var (c, actionId) = await WithActionAsync();
        var cursor = (await PullAsync(c, 0)).GetProperty("cursor").GetInt64();
        var action = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Single(a => a.GetProperty("id").GetGuid() == actionId);
        var slotId = (await PullAsync(c, 0)).GetProperty("slots").EnumerateArray().Single().GetProperty("id").GetGuid();
        var from = DateOnly.Parse(action.GetProperty("date").GetString()!);
        var to = from.AddDays(7); // same weekday, so the Slot's days fit

        var res = await c.PutAsJsonAsync($"/scheduled-actions/{actionId}/move", new { clientId = "mv-1", date = to.ToString("yyyy-MM-dd"), slotId });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var again = await c.PutAsJsonAsync($"/scheduled-actions/{actionId}/move", new { clientId = "mv-1", date = to.ToString("yyyy-MM-dd"), slotId });
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);

        var moved = Of(await PullAsync(c, cursor), "scheduled_action").Single();
        Assert.Equal(to.ToString("yyyy-MM-dd"), moved.GetProperty("data").GetProperty("date").GetString());

        // A replay after a later move leaves the later move alone.
        var back = from.ToString("yyyy-MM-dd");
        await c.PutAsJsonAsync($"/scheduled-actions/{actionId}/move", new { clientId = "mv-2", date = back, slotId });
        await c.PutAsJsonAsync($"/scheduled-actions/{actionId}/move", new { clientId = "mv-1", date = to.ToString("yyyy-MM-dd"), slotId });
        var now = (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Single(a => a.GetProperty("id").GetGuid() == actionId);
        Assert.Equal(back, now.GetProperty("date").GetString());
    }

    [Fact]
    public async Task A_move_to_a_day_the_Slot_does_not_cover_or_to_a_Slot_that_is_not_the_users_is_rejected()
    {
        var (c, actionId) = await WithActionAsync();
        var slotId = (await PullAsync(c, 0)).GetProperty("slots").EnumerateArray().Single().GetProperty("id").GetGuid();
        // 2026-01-06 is a Tuesday; the Slot is Monday, Wednesday and Friday.
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync($"/scheduled-actions/{actionId}/move", new { clientId = "mv-1", date = "2026-01-13", slotId })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PutAsJsonAsync($"/scheduled-actions/{actionId}/move", new { clientId = "mv-2", date = "2026-01-14", slotId = Guid.NewGuid() })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await c.PutAsJsonAsync($"/scheduled-actions/{Guid.NewGuid()}/move", new { clientId = "mv-3", date = "2026-01-14", slotId })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync($"/scheduled-actions/{actionId}/move", new { clientId = "", date = "2026-01-14", slotId })).StatusCode);
    }

    [Fact]
    public async Task A_replayed_outbox_is_idempotent_across_Check_ins_messages_and_approvals()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var versionId = await DraftedAsync(c, aspirationId);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).StatusCode);
        var actions = await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions");
        Assert.Equal(5, actions!.Length);
        var actionId = actions[0].GetProperty("id").GetGuid();

        for (var i = 0; i < 2; i++) await CheckIn(c, actionId, "ci-1", "done", T0);
        factory.Coach.Fail = false;
        factory.Coach.Enqueue("Hello.");
        for (var i = 0; i < 2; i++) (await c.PostAsJsonAsync("/coach/messages", new { text = "hi", clientId = "msg-1" })).EnsureSuccessStatusCode();

        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.Single(thread!, m => m.GetProperty("clientId").GetString() == "msg-1");
        Assert.Single(thread!, m => m.GetProperty("card").GetString() == "check_in");
    }

    [Fact]
    public async Task A_stale_approval_is_rejected_with_the_newer_version_and_leaves_the_Actions_alone()
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        var first = await DraftedAsync(c, aspirationId);
        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Waypoint.Api.Coach.JobQueue>().EnqueueAsync(Waypoint.Api.Coach.JobKinds.Plan, userId,
                new Dictionary<string, Guid> { ["aspirationId"] = aspirationId }, aspirationId);
        var second = await DraftedAsync(c, aspirationId);

        var stale = await c.PostAsync($"/plan-versions/{first}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal(second, (await stale.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("newer").GetProperty("id").GetGuid());
        Assert.Empty(Of(await PullAsync(c, 0), "scheduled_action"));
    }
}
