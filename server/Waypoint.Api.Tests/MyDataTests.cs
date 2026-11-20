using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>My data: export everything as a zip of JSON and Markdown, and delete the whole account.</summary>
public sealed class MyDataTests(WaypointApiFactory factory) : ProgressTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private IServiceScope ScopeFor(Guid userId)
    {
        var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        return scope;
    }

    private async Task AddExtrasAsync(Guid userId, Guid aspirationId, string marker)
    {
        using var scope = ScopeFor(userId);
        var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
        var now = DateTime.UtcNow;
        var interview = new Interview { Id = Guid.NewGuid(), UserId = userId, AspirationId = aspirationId, OwnWords = marker + " words", Status = InterviewStatus.Confirmed, CreatedAt = now };
        db.Interviews.Add(interview);
        db.InterviewMessages.Add(new InterviewMessage { Id = Guid.NewGuid(), UserId = userId, InterviewId = interview.Id, Role = "user", Text = marker + " interview answer", CreatedAt = now });
        db.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), UserId = userId, Role = "user", Text = marker + " chat line", AspirationId = aspirationId, CreatedAt = now });
        db.MeasureReadings.Add(new MeasureReading { Id = Guid.NewGuid(), UserId = userId, AspirationId = aspirationId, Date = new DateOnly(2026, 1, 5), Value = 96, Source = "user", CreatedAt = now });
        db.Pauses.Add(new Pause { Id = Guid.NewGuid(), UserId = userId, ClientId = "p-" + marker, AspirationId = aspirationId, StartsAt = now, StartedBy = PauseStarters.User });
        db.DeviceTokens.Add(new Waypoint.Api.Push.DeviceToken { Id = Guid.NewGuid(), UserId = userId, Token = "tok-" + marker, UpdatedAt = now });
        var milestone = await db.Milestones.FirstAsync();
        db.PlanTasks.Add(new PlanTask { Id = Guid.NewGuid(), UserId = userId, PlanVersionId = milestone.PlanVersionId, MilestoneId = milestone.Id, Title = marker + " task", Minutes = 20 });
        await db.SaveChangesAsync();
    }

    private static async Task<Dictionary<string, string>> UnzipAsync(HttpResponseMessage res)
    {
        Assert.Equal("application/zip", res.Content.Headers.ContentType?.MediaType);
        using var zip = new ZipArchive(await res.Content.ReadAsStreamAsync(), ZipArchiveMode.Read);
        var files = new Dictionary<string, string>();
        foreach (var e in zip.Entries)
        {
            using var r = new StreamReader(e.Open());
            files[e.FullName] = await r.ReadToEndAsync();
        }
        return files;
    }

    [Fact]
    public async Task Export_holds_every_kind_of_data_of_the_user_and_nothing_of_anyone_else()
    {
        var (c, userId, id) = await SeedAsync("1:100:done;2:100:partly:50");
        await AddExtrasAsync(userId, id, "alpha");
        var (_, otherUser, otherId) = await SeedAsync("1:100:done");
        await AddExtrasAsync(otherUser, otherId, "bravo");

        var res = await c.GetAsync("/me/export");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var files = await UnzipAsync(res);

        var json = JsonDocument.Parse(files["waypoint.json"]).RootElement;
        foreach (var kind in new[] { "aspirations", "interviews", "planVersions", "milestones", "measureReadings", "habits", "tasks", "actions", "checkIns", "pauses", "chat", "slots" })
            Assert.True(json.GetProperty(kind).GetArrayLength() > 0, kind);
        Assert.Contains(files.Keys, k => k.EndsWith(".md"));
        var all = string.Join("\n", files.Values);
        Assert.Contains("alpha chat line", all);
        Assert.Contains("alpha interview answer", all);
        Assert.DoesNotContain("bravo", all);
        Assert.DoesNotContain(otherId.ToString(), all);
        // Readable: the Markdown speaks in the Aspiration's own words, not JSON.
        var md = string.Join("\n", files.Where(f => f.Key.EndsWith(".md")).Select(f => f.Value));
        Assert.Contains("# ", md);
        Assert.Contains("Easy run", md);
        Assert.DoesNotContain("\"", md.Split('\n')[0]);
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().GetAsync("/me/export")).StatusCode);
    }

    [Fact]
    public async Task Deleting_the_account_leaves_no_row_with_the_UserId_and_revokes_the_tokens()
    {
        var (c, userId, id) = await SeedAsync("1:100:done;2:100:partly:50");
        await AddExtrasAsync(userId, id, "alpha");
        var (_, otherUser, otherId) = await SeedAsync("1:100:done");
        var refresh = await SignInRefreshAsync(userId);

        var res = await c.DeleteAsync("/me");
        Assert.Equal(HttpStatusCode.NoContent, res.StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
            Assert.False(await db.Users.IgnoreQueryFilters().AnyAsync(u => u.Id == userId));
            Assert.False(await db.Aspirations.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.PlanVersions.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.Milestones.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.Habits.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.PlanTasks.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.Anchors.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.ScheduledActions.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.CheckIns.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.ProgressReadings.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.MeasureReadings.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.Interviews.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.InterviewMessages.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.ChatMessages.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.Pauses.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.Slots.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.SlotEdits.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.WeeklyRefreshes.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.Jobs.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.DeviceTokens.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.RefreshTokens.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            Assert.False(await db.Changes.IgnoreQueryFilters().AnyAsync(a => a.UserId == userId));
            // Someone else's data stays.
            Assert.True(await db.Aspirations.IgnoreQueryFilters().AnyAsync(a => a.Id == otherId));
        }
        // The session is over: the refresh token no longer works, nor does the access token.
        Assert.Equal(HttpStatusCode.Unauthorized, (await factory.CreateClient().PostAsJsonAsync("/auth/refresh", new { refreshToken = refresh })).StatusCode);
        Assert.NotEqual(HttpStatusCode.OK, (await c.GetAsync("/settings")).StatusCode);
    }

    private async Task<string> SignInRefreshAsync(Guid userId)
    {
        using var scope = ScopeFor(userId);
        var sub = (await scope.ServiceProvider.GetRequiredService<WaypointDb>().Users.SingleAsync()).GoogleSub;
        var res = await factory.CreateClient().PostAsJsonAsync("/auth/google", new { idToken = FakeGoogleVerifier.TokenFor(sub) });
        return (await res.Content.ReadFromJsonAsync<TokenPair>())!.RefreshToken;
    }
}
