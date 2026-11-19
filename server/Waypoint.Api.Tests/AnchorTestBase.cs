using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Coach;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>Plans with Anchor habits, shared by the Anchor habit test classes. Each class has its own clock.</summary>
public abstract class AnchorTestBase(WaypointApiFactory factory) : PlanTestBase(factory)
{
    protected static JsonObject AnchorHabit(string title = "Walk after dinner", int times = 3, string words = "after dinner", string? prepFor = null)
    {
        var h = new JsonObject
        {
            ["title"] = title, ["times_per_week"] = times, ["target_minutes"] = 20,
            ["anchor"] = new JsonObject { ["words"] = words, ["time"] = "19:30", ["days"] = new JsonArray("tue", "thu", "sat") },
        };
        if (prepFor is not null) h["prep_for"] = prepFor;
        return h;
    }

    protected static JsonObject AnchorAct(string date, string title) => new() { ["date"] = date, ["anchor"] = "after dinner", ["action"] = title, ["kind"] = "habit", ["minutes"] = 0 };

    protected static JsonObject PlanWithAnchor(Action<JsonObject>? mutate = null) => Plan(p =>
    {
        p["habits"]!.AsArray().Add(AnchorHabit());
        p["first_two_weeks"]!.AsArray().Add(AnchorAct("2026-01-06", "Walk after dinner"));
        p["first_two_weeks"]!.AsArray().Add(AnchorAct("2026-01-08", "Walk after dinner"));
        mutate?.Invoke(p);
    });

    protected async Task<(HttpClient C, Guid UserId, Guid AspirationId, Guid VersionId)> ApprovedAsync(JsonObject plan)
    {
        var (c, userId, _, aspirationId) = await ConfirmedAsync();
        var v = await DraftedAsync(c, aspirationId, plan);
        (await c.PostAsync($"/plan-versions/{v}/approve", null)).EnsureSuccessStatusCode();
        return (c, userId, aspirationId, v);
    }

    protected static JsonObject PlanWithPrep() => PlanWithAnchor(p =>
    {
        p["habits"]!.AsArray().Add(new JsonObject
        {
            ["title"] = "Prep dinner", ["times_per_week"] = 1, ["minutes"] = 10, ["slots"] = new JsonArray("weekday mornings"), ["prep_for"] = "Walk after dinner",
        });
        p["first_two_weeks"]!.AsArray().Add(new JsonObject
        {
            ["date"] = "2026-01-07", ["slot"] = "weekday mornings", ["action"] = "Prep dinner", ["kind"] = "habit", ["minutes"] = 10,
        });
    });

    /// <summary>Anchor habits the user already holds in another Aspiration's approved Plan, so the cross-Aspiration count has something to count.</summary>
    protected async Task SeedOtherAnchorHabitsAsync(Guid userId, int count)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        var db = scope.ServiceProvider.GetRequiredService<Waypoint.Api.Data.WaypointDb>();
        var other = Guid.NewGuid();
        var version = Guid.NewGuid();
        var now = factory.Clock.GetUtcNow().UtcDateTime;
        db.Aspirations.Add(new Waypoint.Api.Data.Aspiration
        {
            Id = other, UserId = userId, ShortName = "Guitar", OwnWords = "learn guitar", Hue = "teal", Status = "closed",
            HopedForDate = DateOnly.FromDateTime(now).AddDays(200), OnSkip = "gentle_check", CreatedAt = now,
        });
        db.PlanVersions.Add(new Waypoint.Api.Data.PlanVersion
        {
            Id = version, UserId = userId, AspirationId = other, Number = 1, Status = Waypoint.Api.Data.PlanVersionStatus.Approved, Rationale = "", Reason = "first_plan",
            Ramp = "", AssumptionsJson = "[]", TwoWeeksJson = "[]", StartDate = DateOnly.FromDateTime(now), CreatedAt = now, ApprovedAt = now,
        });
        for (var i = 0; i < count; i++)
        {
            var anchor = Guid.NewGuid();
            db.Anchors.Add(new Waypoint.Api.Data.Anchor { Id = anchor, UserId = userId, Words = $"routine {i}", Time = "08:00" });
            db.Habits.Add(new Waypoint.Api.Data.Habit
            {
                Id = Guid.NewGuid(), UserId = userId, PlanVersionId = version, Title = $"Other habit {i}", TimesPerWeek = 3, SlotIds = "", AnchorId = anchor,
                Since = DateOnly.FromDateTime(now).AddDays(-60),
            });
        }
        await db.SaveChangesAsync();
    }

    protected static async Task<JsonElement[]> ActionsAsync(HttpClient c) => (await c.GetFromJsonAsync<JsonElement[]>("/scheduled-actions"))!;

    protected static Task<HttpResponseMessage> CheckIn(HttpClient c, JsonElement action, string state, DateTime at) => c.PutAsJsonAsync("/check-ins", new
    {
        scheduledActionId = action.GetProperty("id").GetGuid(), clientId = "ci-" + Guid.NewGuid().ToString("N"), state, tappedAt = at,
    });

}
