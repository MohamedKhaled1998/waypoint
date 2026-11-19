using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed class PlanTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    [Fact]
    public async Task Confirming_enqueues_a_Plan_job_and_running_it_stores_a_proposal_that_approving_makes_version_1()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();

        var before = await PlansAsync(c, aspirationId);
        Assert.True(before.GetProperty("drafting").GetBoolean());
        Assert.Equal(JsonValueKind.Null, before.GetProperty("pendingVersionId").ValueKind);

        var versionId = await DraftedAsync(c, aspirationId);
        var after = await PlansAsync(c, aspirationId);
        Assert.False(after.GetProperty("drafting").GetBoolean());
        Assert.Equal(JsonValueKind.Null, after.GetProperty("currentVersionId").ValueKind);
        Assert.Empty(await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions") ?? []);

        var request = factory.Coach.Requests.Last(r => r.Kind == CoachCallKind.Plan);
        Assert.Equal(CoachCallKind.Plan, request.Kind);

        var proposal = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{versionId}");
        Assert.Equal("proposed", proposal.GetProperty("status").GetString());
        Assert.Equal("first_plan", proposal.GetProperty("reason").GetString());
        var milestones = proposal.GetProperty("milestones").EnumerateArray().ToList();
        Assert.Equal(["Run 5 km", "Run 10 km"], milestones.Select(m => m.GetProperty("title").GetString()));
        Assert.Equal(["2026-02-02", "2026-06-01"], milestones.Select(m => m.GetProperty("date").GetString()));
        Assert.Equal(["ability", "effort"], milestones.Select(m => m.GetProperty("kind").GetString()));
        Assert.Equal(["you can run 5 km without stopping", "you have done 12 long runs"], milestones.Select(m => m.GetProperty("reachedWhen").GetString()));
        Assert.All(milestones, m => Assert.Equal("upcoming", m.GetProperty("state").GetString()));
        Assert.Equal(5, proposal.GetProperty("twoWeeks").GetArrayLength());

        var thread = await c.GetFromJsonAsync<JsonElement[]>("/coach/messages");
        Assert.Contains(thread!, m => m.GetProperty("card").GetString() == "proposal");

        var approved = await c.PostAsync($"/plan-versions/{versionId}/approve", null);
        Assert.Equal(HttpStatusCode.OK, approved.StatusCode);
        var v1 = await approved.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, v1.GetProperty("number").GetInt32());
        Assert.Equal("approved", v1.GetProperty("status").GetString());
        Assert.Equal(1, v1.GetProperty("habits").GetArrayLength());
        Assert.Equal(3, v1.GetProperty("habits")[0].GetProperty("timesPerWeek").GetInt32());
        Assert.Equal(2, v1.GetProperty("tasks").GetArrayLength());
        Assert.Contains(v1.GetProperty("tasks").EnumerateArray(), t => t.GetProperty("due").ValueKind == JsonValueKind.Null);

        var scheduled = await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions");
        Assert.Equal(5, scheduled!.Length);
        // Both weeks of the window (2026-01-06 to 2026-01-19) get their Actions; none was scheduled before approval.
        Assert.Equal(["2026-01-07", "2026-01-09", "2026-01-12", "2026-01-14", "2026-01-16"], scheduled.Select(a => a.GetProperty("date").GetString()));
        Assert.Equal(3, scheduled.Count(a => a.GetProperty("date").GetString()!.CompareTo("2026-01-13") < 0));
        Assert.Equal(2, scheduled.Count(a => a.GetProperty("date").GetString()!.CompareTo("2026-01-13") >= 0));
        Assert.Equal("Buy running shoes", scheduled[4].GetProperty("title").GetString());
        Assert.Equal("2026-01-07", scheduled[0].GetProperty("date").GetString());
        Assert.Equal(30, scheduled[0].GetProperty("minutes").GetInt32());
        Assert.Equal("weekday mornings", scheduled[0].GetProperty("slot").GetString());

        var done = await PlansAsync(c, aspirationId);
        Assert.Equal(versionId, done.GetProperty("currentVersionId").GetGuid());
        Assert.Equal(JsonValueKind.Null, done.GetProperty("pendingVersionId").ValueKind);
    }

    [Fact]
    public async Task Job_payloads_hold_row_ids_only()
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        await DraftedAsync(c, aspirationId);

        using var scope = factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<WaypointDb>().Jobs.IgnoreQueryFilters().Where(j => j.UserId == userId).ToList();
        Assert.NotEmpty(jobs);
        foreach (var job in jobs)
        {
            var payload = JsonNode.Parse(job.PayloadJson)!.AsObject();
            Assert.NotEmpty(payload);
            foreach (var (_, value) in payload)
                Assert.True(Guid.TryParse(value!.GetValue<string>(), out _), "payload value is not an ID");
        }
        Assert.Contains(jobs, j => j.AspirationId == aspirationId);
    }

    public static TheoryData<string> BrokenPlans => new() { "settle_for", "off_slot_day", "overfull_slot", "duplicate_habit", "duplicate_task", "bad_kind" };

    private static JsonObject Broken(string how) => Plan(p =>
    {
        switch (how)
        {
            case "settle_for": p["milestones"]![1]!["date"] = "2026-11-02"; break;
            case "off_slot_day": p["first_two_weeks"]![0]!["date"] = "2026-01-08"; break; // a Thursday
            case "overfull_slot": p["first_two_weeks"]!.AsArray().Add(Act("2026-01-07", "Easy run", "habit", 30)); break; // 60 min in 45
            case "duplicate_habit": p["habits"]!.AsArray().Add(p["habits"]![0]!.DeepClone()); break;
            case "duplicate_task": p["tasks"]!.AsArray().Add(p["tasks"]![0]!.DeepClone()); break;
            case "bad_kind": p["milestones"]![0]!["kind"] = "wish"; break;
        }
    });

    private static string ProblemFor(string how) => how switch
    {
        "settle_for" => "past_settle_for_date", "off_slot_day" => "not_a_slot_day", "overfull_slot" => "slot_overfull",
        "duplicate_habit" => "habit_1_title_duplicate", "duplicate_task" => "task_2_title_duplicate", _ => "milestone_0_kind_invalid",
    };

    [Theory]
    [MemberData(nameof(BrokenPlans))]
    public async Task A_draft_that_breaks_a_hard_rule_goes_back_to_the_Coach_once_with_what_was_wrong(string how)
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        Script(Broken(how));
        Script(Plan());
        var calls = factory.Coach.Requests.Count;
        await RunJobsAsync();
        await RunJobsAsync(); // the "Plan ready" push job

        Assert.Equal(2, factory.Coach.Requests.Count - calls);
        var second = factory.Coach.Requests.Last();
        Assert.Contains(second.Turns, t => t.Role == "user" && t.Text.Contains(ProblemFor(how)));

        // The valid second draft is the one stored, and the rejected one left nothing behind.
        var pending = (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").GetGuid();
        var stored = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{pending}");
        Assert.Equal("Short sessions before work, because mornings worked for you.", stored.GetProperty("rationale").GetString());
        Assert.Equal(2, stored.GetProperty("milestones").GetArrayLength());
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
        Assert.Equal(1, db.PlanVersions.IgnoreQueryFilters().Count(v => v.AspirationId == aspirationId));
    }

    [Theory]
    [MemberData(nameof(BrokenPlans))]
    public async Task A_draft_still_broken_after_the_resend_is_not_stored_and_is_retried_as_a_job(string how)
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        Script(Broken(how));
        Script(Broken(how));
        await RunJobsAsync();

        var p = await PlansAsync(c, aspirationId);
        Assert.Equal(JsonValueKind.Null, p.GetProperty("pendingVersionId").ValueKind);
        Assert.True(p.GetProperty("drafting").GetBoolean());

        Assert.Equal(0, await RunJobsAsync()); // backing off
        factory.Clock.Advance(TimeSpan.FromMinutes(5));
        Script(Plan());
        Assert.Equal(1, await RunJobsAsync());
        await RunJobsAsync(); // leaves no "Plan ready" push job for the other tests
        Assert.NotEqual(JsonValueKind.Null, (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").ValueKind);
    }

    [Fact]
    public async Task A_provider_failure_retries_on_a_1_5_25_minute_schedule_without_running_early_and_logs_no_free_text()
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        factory.Coach.Fail = true;
        Assert.Equal(1, await RunJobsAsync()); // attempt 1 fails
        foreach (var minutes in new[] { 1, 5 })
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await RunJobsAsync()); // not early
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(1, await RunJobsAsync()); // fails again
        }
        factory.Clock.Advance(TimeSpan.FromMinutes(25) - TimeSpan.FromSeconds(1));
        Assert.Equal(0, await RunJobsAsync());
        factory.Clock.Advance(TimeSpan.FromSeconds(1));
        factory.Coach.Fail = false;
        Script(Plan());
        Assert.Equal(1, await RunJobsAsync()); // attempt 4 succeeds
        c = await ReauthAsync(userId);

        Assert.NotEqual(JsonValueKind.Null, (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").ValueKind);
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("run a half marathon") || l.Contains("Short sessions"));
    }

    [Fact]
    public async Task After_MaxAttempts_the_draft_is_late_not_drafting_and_a_retry_drafts_a_new_Plan()
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        factory.Coach.Fail = true;
        Assert.Equal(1, await RunJobsAsync());
        foreach (var minutes in new[] { 1, 5, 25, 125 })
        {
            factory.Clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromSeconds(1));
            Assert.Equal(0, await RunJobsAsync());
            factory.Clock.Advance(TimeSpan.FromSeconds(1));
            Assert.Equal(1, await RunJobsAsync());
            c = await ReauthAsync(userId);
            if (minutes < 125) Assert.True((await PlansAsync(c, aspirationId)).GetProperty("drafting").GetBoolean());
        }

        var late = await PlansAsync(c, aspirationId);
        Assert.False(late.GetProperty("drafting").GetBoolean());
        Assert.True(late.GetProperty("draftLate").GetBoolean());
        factory.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(0, await RunJobsAsync()); // gave up: never runs again by itself
        c = await ReauthAsync(userId);

        factory.Coach.Fail = false;
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync($"/aspirations/{aspirationId}/plan-retry", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Accepted, (await c.PostAsync($"/aspirations/{aspirationId}/plan-retry", null)).StatusCode); // one pending job only
        var retrying = await PlansAsync(c, aspirationId);
        Assert.True(retrying.GetProperty("drafting").GetBoolean());
        Assert.False(retrying.GetProperty("draftLate").GetBoolean());
        Script(Plan());
        Assert.Equal(1, await RunJobsAsync());
        await RunJobsAsync(); // the "Plan ready" push job
        Assert.NotEqual(JsonValueKind.Null, (await PlansAsync(c, aspirationId)).GetProperty("pendingVersionId").ValueKind);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsync($"/aspirations/{Guid.NewGuid()}/plan-retry", null)).StatusCode);
    }

    [Fact]
    public async Task Another_users_Plan_job_is_not_visible_to_the_query_filter_or_to_their_Plans()
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync(); // user A: a pending Plan job
        var (other, otherId) = await SignedInAsync();
        Assert.Empty(await other.GetFromJsonAsync<JsonElement[]>("/plans") ?? []);

        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Waypoint.Api.Auth.CurrentUser>().Override = otherId;
            Assert.Empty(scope.ServiceProvider.GetRequiredService<WaypointDb>().Jobs.ToList());
        }
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/aspirations/{aspirationId}/plan-retry", null)).StatusCode);
        Assert.True((await PlansAsync(c, aspirationId)).GetProperty("drafting").GetBoolean());
        await DraftedAsync(c, aspirationId); // leaves no pending job for the other tests
    }

    [Fact]
    public async Task Approving_a_version_the_server_has_replaced_is_rejected_and_shows_the_newer_one()
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        var first = await DraftedAsync(c, aspirationId);

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueAsync(JobKinds.Plan, userId,
                new Dictionary<string, Guid> { ["aspirationId"] = aspirationId });
        var second = await DraftedAsync(c, aspirationId);
        Assert.NotEqual(first, second);

        var stale = await c.PostAsync($"/plan-versions/{first}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("stale", body.GetProperty("error").GetString());
        Assert.Equal(second, body.GetProperty("newer").GetProperty("id").GetGuid());
        Assert.Empty(await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions") ?? []);

        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/plan-versions/{second}/approve", null)).StatusCode);
    }

    [Fact]
    public async Task Approving_a_version_that_is_already_approved_changes_nothing_and_one_that_was_superseded_is_rejected()
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        var first = await DraftedAsync(c, aspirationId);
        Assert.Equal(HttpStatusCode.OK, (await c.PostAsync($"/plan-versions/{first}/approve", null)).StatusCode);
        var again = await c.PostAsync($"/plan-versions/{first}/approve", null); // a retry after a lost response
        Assert.Equal(HttpStatusCode.OK, again.StatusCode);
        Assert.Equal(1, (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("number").GetInt32());
        Assert.Equal(5, (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Length);

        using (var scope = factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<JobQueue>().EnqueueAsync(JobKinds.Plan, userId,
                new Dictionary<string, Guid> { ["aspirationId"] = aspirationId }, aspirationId);
        var second = await DraftedAsync(c, aspirationId);
        var v2 = await c.PostAsync($"/plan-versions/{second}/approve", null);
        Assert.Equal(2, (await v2.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("number").GetInt32());

        var stale = await c.PostAsync($"/plan-versions/{first}/approve", null);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await stale.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(second, body.GetProperty("newer").GetProperty("id").GetGuid());
        Assert.Equal("superseded", (await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{first}")).GetProperty("status").GetString());
        Assert.Equal(10, (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Length); // each approval scheduled its own
    }

    [Fact]
    public async Task Two_approves_racing_schedule_the_Actions_once()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var versionId = await DraftedAsync(c, aspirationId);

        var results = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => c.PostAsync($"/plan-versions/{versionId}/approve", null)));
        Assert.All(results, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(5, (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!.Length);
        var view = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{versionId}");
        Assert.Equal(1, view.GetProperty("number").GetInt32());
        Assert.Equal(versionId, (await PlansAsync(c, aspirationId)).GetProperty("currentVersionId").GetGuid());
    }

    [Fact]
    public async Task Habits_and_tasks_with_the_same_title_in_different_Actions_link_to_the_right_rows()
    {
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        var versionId = await DraftedAsync(c, aspirationId);
        await c.PostAsync($"/plan-versions/{versionId}/approve", null);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
        var actions = db.ScheduledActions.IgnoreQueryFilters().Where(a => a.PlanVersionId == versionId).ToList();
        var habit = db.Habits.IgnoreQueryFilters().Single(h => h.PlanVersionId == versionId);
        var task = db.PlanTasks.IgnoreQueryFilters().Single(t => t.PlanVersionId == versionId && t.Title == "Buy running shoes");
        var slot = db.Slots.IgnoreQueryFilters().Single(s => s.Label == "weekday mornings" && s.UserId == habit.UserId);
        Assert.All(actions.Where(a => a.Title == "Easy run"), a => { Assert.Equal(habit.Id, a.HabitId); Assert.Null(a.TaskId); Assert.Equal(slot.Id, a.SlotId); });
        Assert.All(actions.Where(a => a.Title == "Buy running shoes"), a => { Assert.Equal(task.Id, a.TaskId); Assert.Null(a.HabitId); });
    }

    [Fact]
    public async Task Another_user_cannot_see_or_approve_a_Plan()
    {
        var versionId = await ConfirmAndDraftAsync();
        var (other, _) = await SignedInAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync($"/plan-versions/{versionId}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await other.PostAsync($"/plan-versions/{versionId}/approve", null)).StatusCode);
    }
}
