using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>An Aspiration's lifecycle: maintenance, a new chapter, reached, and Delete. The fake clock sits at Monday 2026-01-05.</summary>
public sealed class AspirationLifecycleTests(WaypointApiFactory factory) : ProgressTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private async Task<JsonElement> StatusOf(HttpClient c, Guid id) =>
        (await c.GetFromJsonAsync<JsonElement>("/aspirations")).EnumerateArray().Single(a => a.GetProperty("id").GetGuid() == id);

    [Fact]
    public async Task Moving_to_maintenance_keeps_the_habits_drops_the_Milestones_and_stops_Projections()
    {
        var (c, _, id) = await SeedAsync("1:100:done;2:100:done");
        var before = await c.GetFromJsonAsync<JsonElement>($"/progress/{id}");
        Assert.True(before.GetProperty("milestones").GetArrayLength() > 0);

        var response = await c.PostAsync($"/aspirations/{id}/maintenance", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        Assert.Equal("maintenance", (await StatusOf(c, id)).GetProperty("status").GetString());
        var plan = await PlansAsync(c, id);
        var version = await c.GetFromJsonAsync<JsonElement>($"/plan-versions/{plan.GetProperty("currentVersionId").GetGuid()}");
        Assert.Equal("maintenance", version.GetProperty("reason").GetString());
        Assert.Equal(0, version.GetProperty("milestones").GetArrayLength());
        Assert.Equal("Easy run", version.GetProperty("habits")[0].GetProperty("title").GetString());

        var progress = await c.GetFromJsonAsync<JsonElement>($"/progress/{id}");
        Assert.Equal(0, progress.GetProperty("milestones").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, progress.GetProperty("next").ValueKind);
        // Both weeks kept up: two of the last eight.
        Assert.Equal(2, progress.GetProperty("consistency").GetProperty("weeksKeptUp").GetInt32());
        Assert.Equal(8, progress.GetProperty("consistency").GetProperty("ofWeeks").GetInt32());

        var list = await c.GetFromJsonAsync<JsonElement>("/progress");
        Assert.Equal(0, list.GetProperty("aspirations").GetArrayLength());
        Assert.Equal(id, list.GetProperty("maintenance")[0].GetProperty("aspirationId").GetGuid());
    }

    [Fact]
    public async Task Mark_reached_keeping_it_up_moves_to_maintenance()
    {
        var (c, _, id) = await SeedAsync("1:100:done");

        var response = await c.PostAsJsonAsync($"/aspirations/{id}/reached", new { keepUp = true });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("maintenance", (await StatusOf(c, id)).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Mark_reached_and_close_ends_the_habits_but_keeps_the_history()
    {
        var (c, userId, id) = await SeedAsync("1:100:done");
        var upcoming = await SeedFutureActionAsync(userId, id);

        var response = await c.PostAsJsonAsync($"/aspirations/{id}/reached", new { keepUp = false });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("closed", (await StatusOf(c, id)).GetProperty("status").GetString());
        var actions = (await c.GetFromJsonAsync<JsonElement>("/scheduled-actions")).EnumerateArray().Select(a => a.GetProperty("id").GetGuid()).ToList();
        Assert.DoesNotContain(upcoming, actions); // nothing more is asked of the user
        Assert.Single(actions);                    // the checked-in week stays as history
        // It is listed as history and can be opened, so the user can still delete it.
        var list = await c.GetFromJsonAsync<JsonElement>("/progress");
        Assert.Equal(id, list.GetProperty("closed")[0].GetProperty("aspirationId").GetGuid());
        var opened = await c.GetAsync($"/progress/{id}");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        Assert.Equal("closed", (await opened.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("status").GetString());
        var plan = await PlansAsync2(c, id);
        Assert.Null(plan);                         // a closed Aspiration has no Plan screen
        using var scope = ScopeFor(userId);
        var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
        Assert.True(await db.PlanVersions.AnyAsync(p => p.AspirationId == id));
        Assert.True(await db.CheckIns.AnyAsync());
    }

    private IServiceScope ScopeFor(Guid userId)
    {
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        return scope;
    }

    private async Task<JsonElement?> PlansAsync2(HttpClient c, Guid id) =>
        (await c.GetFromJsonAsync<JsonElement>("/plans")).EnumerateArray().Where(p => p.GetProperty("aspirationId").GetGuid() == id).Select(p => (JsonElement?)p).FirstOrDefault();

    private async Task<Guid> SeedFutureActionAsync(Guid userId, Guid aspirationId)
    {
        using var scope = ScopeFor(userId);
        var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
        var version = await db.PlanVersions.Where(p => p.AspirationId == aspirationId && p.Status == PlanVersionStatus.Approved).Select(p => p.Id).FirstAsync();
        var slot = await db.Slots.Select(s => s.Id).FirstAsync();
        var action = new ScheduledAction
        {
            Id = Guid.NewGuid(), UserId = userId, AspirationId = aspirationId, PlanVersionId = version, SlotId = slot,
            Date = Start.AddDays(2), Title = "Easy run", Minutes = 30,
        };
        db.ScheduledActions.Add(action);
        await db.SaveChangesAsync();
        return action.Id;
    }

    [Fact]
    public async Task Delete_removes_every_row_under_the_Aspiration_and_leaves_the_rest()
    {
        var (c, userId, id) = await SeedAsync("1:100:done;2:100:partly:50");
        var other = Guid.NewGuid();
        using (var scope = ScopeFor(userId))
        {
            var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
            var now = DateTime.UtcNow;
            var anchor = new Anchor { Id = Guid.NewGuid(), UserId = userId, Words = "after dinner", Time = "19:30" };
            db.Anchors.Add(anchor);
            var version = await db.PlanVersions.Where(p => p.AspirationId == id).Select(p => p.Id).FirstAsync();
            db.Habits.Add(new Habit { Id = Guid.NewGuid(), UserId = userId, PlanVersionId = version, Title = "Walk", TimesPerWeek = 3, Minutes = 0, SlotIds = "", AnchorId = anchor.Id });
            var interview = new Interview { Id = Guid.NewGuid(), UserId = userId, AspirationId = id, OwnWords = "x", Status = InterviewStatus.Confirmed, CreatedAt = now };
            db.Interviews.Add(interview);
            db.InterviewMessages.Add(new InterviewMessage { Id = Guid.NewGuid(), UserId = userId, InterviewId = interview.Id, Role = "user", Text = "hi", CreatedAt = now });
            db.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), UserId = userId, Role = "user", Text = "about it", AspirationId = id, CreatedAt = now });
            db.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), UserId = userId, Role = "user", Text = "general", CreatedAt = now });
            db.Jobs.Add(new Job { Id = Guid.NewGuid(), UserId = userId, Kind = "reminder_text", AspirationId = id, PayloadJson = "{}", State = JobState.Pending, NextRunAt = now, CreatedAt = now });
            db.Pauses.Add(new Pause { Id = Guid.NewGuid(), UserId = userId, ClientId = "p-" + id, AspirationId = id, StartsAt = now, StartedBy = PauseStarters.User });
            db.Aspirations.Add(new Aspiration
            {
                Id = other, UserId = userId, OwnWords = "learn Spanish", ShortName = "Spanish", Hue = "teal", Status = "active", ShareHoursPerWeek = 1,
                HopedForDate = new DateOnly(2026, 12, 1), OnSkip = "gentle_check", CreatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        var response = await c.DeleteAsync($"/aspirations/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        using var check = ScopeFor(userId);
        var after = check.ServiceProvider.GetRequiredService<WaypointDb>();
        Assert.False(await after.PlanVersions.AnyAsync(p => p.AspirationId == id));
        Assert.False(await after.Milestones.AnyAsync());
        Assert.False(await after.Habits.AnyAsync());
        Assert.False(await after.PlanTasks.AnyAsync());
        Assert.False(await after.ScheduledActions.AnyAsync(a => a.AspirationId == id));
        Assert.False(await after.CheckIns.AnyAsync());
        Assert.False(await after.ProgressReadings.AnyAsync(r => r.AspirationId == id));
        Assert.False(await after.MeasureReadings.AnyAsync(r => r.AspirationId == id));
        Assert.False(await after.Interviews.AnyAsync(i => i.AspirationId == id));
        Assert.False(await after.InterviewMessages.AnyAsync());
        Assert.False(await after.ChatMessages.AnyAsync(m => m.AspirationId == id));
        Assert.False(await after.Jobs.AnyAsync(j => j.AspirationId == id));
        Assert.False(await after.Pauses.AnyAsync(p => p.AspirationId == id));
        Assert.False(await after.Anchors.AnyAsync());
        Assert.False(await after.Aspirations.AnyAsync(a => a.Id == id));
        // What isn't under it stays.
        Assert.True(await after.Aspirations.AnyAsync(a => a.Id == other));
        Assert.True(await after.ChatMessages.AnyAsync(m => m.Text == "general"));
        Assert.True(await after.Slots.AnyAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await c.DeleteAsync($"/aspirations/{id}")).StatusCode);
    }

    [Fact]
    public async Task A_new_chapter_runs_a_short_Interview_and_adds_Milestones_to_the_maintenance_Aspiration()
    {
        var (c, userId, id) = await SeedAsync("1:100:done");
        (await c.PostAsync($"/aspirations/{id}/maintenance", null)).EnsureSuccessStatusCode();
        using (var scope = ScopeFor(userId))
            await scope.ServiceProvider.GetRequiredService<WaypointDb>().Users.ExecuteUpdateAsync(u => u.SetProperty(x => x.SetupAt, DateTime.UtcNow)); // the first Interview set the user up
        factory.Coach.Fail = false;

        var started = await c.PostAsync($"/aspirations/{id}/new-chapter", null);
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        var interview = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        var hand = HandOff();
        hand.Remove("user"); // the user's Slots and Time budget are already saved
        factory.Coach.Enqueue($"Here is the next chapter.\n{Marker}\n{hand.ToJsonString()}");
        (await c.PostAsJsonAsync($"/interviews/{interview}/messages", new { text = "A full marathon", clientId = "ch-1" })).EnsureSuccessStatusCode();
        // The Coach was told this is a short, new-chapter Interview.
        Assert.Contains("new chapter", factory.Coach.Requests.Last().System, StringComparison.OrdinalIgnoreCase);

        var confirm = await c.PostAsJsonAsync($"/interviews/{interview}/confirm", new { shortName = (string?)null });
        Assert.True(confirm.StatusCode == HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());
        Assert.Equal(id, (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aspirationId").GetGuid());
        var back = await StatusOf(c, id);
        Assert.Equal("active", back.GetProperty("status").GetString());
        Assert.Equal("2026-09-20", back.GetProperty("hopedForDate").GetString());

        var plan = JsonNode.Parse(Plan().ToJsonString().Replace("weekday mornings", "mornings"))!.AsObject();
        Script(plan);
        await RunJobsAsync();
        var pending = (await PlansAsync(c, id)).GetProperty("pendingVersionId").GetGuid();
        (await c.PostAsync($"/plan-versions/{pending}/approve", null)).EnsureSuccessStatusCode();

        var now = await c.GetFromJsonAsync<JsonElement>($"/progress/{id}");
        Assert.Equal(2, now.GetProperty("milestones").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, now.GetProperty("consistency").ValueKind);
    }

    [Fact]
    public async Task A_new_chapter_needs_a_maintenance_Aspiration()
    {
        var (c, _, id) = await SeedAsync("1:100:done");
        Assert.Equal(HttpStatusCode.Conflict, (await c.PostAsync($"/aspirations/{id}/new-chapter", null)).StatusCode);
    }

    private async Task<List<(string Kind, Guid Id, bool Deleted)>> SyncAsync(HttpClient c)
    {
        var page = await c.GetFromJsonAsync<JsonElement>("/sync?cursor=0");
        return page.GetProperty("changes").EnumerateArray().Select(x => (x.GetProperty("kind").GetString()!, x.GetProperty("id").GetGuid(), x.GetProperty("deleted").GetBoolean())).ToList();
    }

    [Fact]
    public async Task Mark_reached_and_close_tells_the_phone_the_coming_Actions_are_gone()
    {
        var (c, userId, id) = await SeedAsync("1:100:done");
        var upcoming = await SeedFutureActionAsync(userId, id);

        (await c.PostAsJsonAsync($"/aspirations/{id}/reached", new { keepUp = false })).EnsureSuccessStatusCode();

        Assert.Contains(await SyncAsync(c), x => x.Kind == "scheduled_action" && x.Id == upcoming && x.Deleted);
    }

    [Fact]
    public async Task Delete_tells_the_phone_the_Aspiration_is_gone()
    {
        var (c, _, id) = await SeedAsync("1:100:done");

        Assert.Equal(HttpStatusCode.NoContent, (await c.DeleteAsync($"/aspirations/{id}")).StatusCode);

        var changes = await SyncAsync(c);
        Assert.Contains(changes, x => x.Kind == "aspiration" && x.Id == id && x.Deleted);
        Assert.Contains(changes, x => x.Kind == "scheduled_action" && x.Deleted);
    }

    [Fact]
    public async Task A_new_chapter_ignores_another_Aspirations_open_Interview()
    {
        var (c, userId, id) = await SeedAsync("1:100:done");
        (await c.PostAsync($"/aspirations/{id}/maintenance", null)).EnsureSuccessStatusCode();
        Guid other;
        using (var scope = ScopeFor(userId))
        {
            var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
            other = Guid.NewGuid();
            db.Interviews.Add(new Interview { Id = other, UserId = userId, OwnWords = "learn Spanish", Status = InterviewStatus.InProgress, CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var started = await c.PostAsync($"/aspirations/{id}/new-chapter", null);

        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        Assert.NotEqual(other, (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task A_weekly_refresh_keeps_a_maintenance_Aspirations_habits_and_makes_no_Milestone_proposal()
    {
        var (c, userId, id) = await SeedAsync(Half, readings: "on_track;on_track;on_track+over");
        (await c.PostAsync($"/aspirations/{id}/maintenance", null)).EnsureSuccessStatusCode();
        Guid before;
        int readings;
        using (var scope = ScopeFor(userId))
        {
            var seeded = scope.ServiceProvider.GetRequiredService<WaypointDb>();
            before = await seeded.PlanVersions.Where(p => p.AspirationId == id && p.Status == PlanVersionStatus.Approved).Select(p => p.Id).SingleAsync();
            readings = await seeded.ProgressReadings.CountAsync(r => r.AspirationId == id);
        }

        await RefreshAsync(userId);

        using var check = ScopeFor(userId);
        var db = check.ServiceProvider.GetRequiredService<WaypointDb>();
        var versions = await db.PlanVersions.Where(p => p.AspirationId == id && p.Id != before && p.Status == PlanVersionStatus.Proposed).ToListAsync();
        Assert.All(versions, v => Assert.DoesNotContain("dates_", v.Reason));
        foreach (var v in versions) Assert.False(await db.Milestones.AnyAsync(m => m.PlanVersionId == v.Id));
        Assert.False(await db.Milestones.AnyAsync(m => m.PlanVersionId == before));
        Assert.Equal(readings, await db.ProgressReadings.CountAsync(r => r.AspirationId == id)); // nothing judged
        Assert.Empty(CoachLines(userId, id));
        Assert.True(await db.Habits.AnyAsync(h => h.PlanVersionId == before));
    }

    [Fact]
    public async Task Confirming_a_first_Interview_whose_Aspiration_is_not_in_maintenance_makes_a_new_Aspiration()
    {
        var (c, userId, id) = await SeedAsync("1:100:done");
        Guid interview;
        using (var scope = ScopeFor(userId))
        {
            var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
            interview = Guid.NewGuid();
            db.Interviews.Add(new Interview { Id = interview, UserId = userId, AspirationId = id, OwnWords = "a marathon", Status = InterviewStatus.AwaitingConfirmation,
                HandOffJson = HandOff().ToJsonString(), CreatedAt = DateTime.UtcNow });
            await db.SaveChangesAsync();
        }

        var confirm = await c.PostAsJsonAsync($"/interviews/{interview}/confirm", new { shortName = (string?)null });

        Assert.True(confirm.StatusCode == HttpStatusCode.OK, await confirm.Content.ReadAsStringAsync());
        Assert.NotEqual(id, (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aspirationId").GetGuid());
    }
}
