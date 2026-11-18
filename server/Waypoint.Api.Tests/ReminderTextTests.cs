using System.Net.Http.Json;
using System.Text.Json;
using Waypoint.Api.Coach;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed class ReminderTextTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private async Task<(HttpClient Client, Guid VersionId)> ApprovedAsync(JsonObjectMutation? mutate = null)
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var versionId = await DraftedAsync(c, aspirationId, Plan(p => mutate?.Invoke(p)));
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        return (c, versionId);
    }

    public delegate void JsonObjectMutation(System.Text.Json.Nodes.JsonObject plan);

    private static Task<JsonElement[]> ActionsAsync(HttpClient c) => c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions")!;

    [Fact]
    public async Task Approving_a_Plan_writes_reminder_text_on_the_light_model_and_stores_it_with_each_Action()
    {
        var (c, _) = await ApprovedAsync();
        Assert.All(await ActionsAsync(c), a => Assert.Equal(JsonValueKind.Null, a.GetProperty("reminderText").ValueKind));

        factory.Coach.Requests.Clear();
        await RunJobsAsync();

        var call = factory.Coach.Requests.First(r => r.Kind == CoachCallKind.ReminderText);
        Assert.Equal("claude-haiku-5-5", call.Model);
        Assert.All(await ActionsAsync(c), a => Assert.Equal("Easy run: shoes by the door.", a.GetProperty("reminderText").GetString()));
    }

    [Fact]
    public async Task A_model_failure_retries_later_and_never_blocks_the_approved_Plan()
    {
        var (c, versionId) = await ApprovedAsync();

        factory.Coach.Fail = true;
        try { await RunJobsAsync(); } finally { factory.Coach.Fail = false; }
        Assert.All(await ActionsAsync(c), a => Assert.Equal(JsonValueKind.Null, a.GetProperty("reminderText").ValueKind));
        Assert.Equal("approved", (await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{versionId}")).GetProperty("status").GetString());

        Assert.Equal(0, await RunJobsAsync()); // backing off
        factory.Clock.Advance(JobRunner.Backoff(1) + TimeSpan.FromSeconds(1));
        await RunJobsAsync();
        Assert.All(await ActionsAsync(c), a => Assert.NotEqual(JsonValueKind.Null, a.GetProperty("reminderText").ValueKind));
    }

    [Fact]
    public async Task The_Coach_sets_now_or_get_ready_with_a_lead_and_the_default_is_now()
    {
        var (c, _) = await ApprovedAsync(p => p["first_two_weeks"]![4]!.AsObject().Merge("get_ready", 90));
        var actions = await ActionsAsync(c);
        var task = actions.Single(a => a.GetProperty("title").GetString() == "Buy running shoes");
        Assert.Equal("get_ready", task.GetProperty("reminderKind").GetString());
        Assert.Equal(90, task.GetProperty("reminderLeadMinutes").GetInt32());
        var run = actions.First(a => a.GetProperty("title").GetString() == "Easy run");
        Assert.Equal("now", run.GetProperty("reminderKind").GetString());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("reminderLeadMinutes").ValueKind);
        Assert.True(run.GetProperty("reminderOn").GetBoolean());
    }

    [Fact]
    public async Task The_user_can_change_an_Actions_lead_time_or_switch_its_reminder_off()
    {
        var (c, _) = await ApprovedAsync();
        var id = (await ActionsAsync(c))[0].GetProperty("id").GetGuid();

        var res = await c.PutAsJsonAsync($"/scheduled-actions/{id}/reminder", new { kind = "get_ready", leadMinutes = 30, on = true });
        res.EnsureSuccessStatusCode();
        var a = (await ActionsAsync(c)).Single(x => x.GetProperty("id").GetGuid() == id);
        Assert.Equal("get_ready", a.GetProperty("reminderKind").GetString());
        Assert.Equal(30, a.GetProperty("reminderLeadMinutes").GetInt32());

        (await c.PutAsJsonAsync($"/scheduled-actions/{id}/reminder", new { kind = "now", leadMinutes = (int?)null, on = false })).EnsureSuccessStatusCode();
        Assert.False((await ActionsAsync(c)).Single(x => x.GetProperty("id").GetGuid() == id).GetProperty("reminderOn").GetBoolean());
        Assert.Equal(System.Net.HttpStatusCode.BadRequest,
            (await c.PutAsJsonAsync($"/scheduled-actions/{id}/reminder", new { kind = "get_ready", leadMinutes = 0, on = true })).StatusCode);
    }
}

internal static class JsonObjectExt
{
    public static void Merge(this System.Text.Json.Nodes.JsonObject o, string kind, int lead)
    {
        o["reminder"] = kind;
        o["lead_minutes"] = lead;
    }
}
