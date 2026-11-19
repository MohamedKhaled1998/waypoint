using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;

namespace Waypoint.Api.Push;

public sealed class DeviceToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Token { get; set; }
    public DateTime UpdatedAt { get; set; }
}

public sealed record PushTokenRequest(string? Token);

public static class Pushes
{
    public const int MaxTokenLength = 512;
    public const string PlanReadyRoute = "plan_ready";
    public const string CoachRoute = "coach";
    public const string BatteryHint = "battery_settings";
    public const string WeeklyRefreshRoute = "weekly_refresh";
    /// <summary>The notification category whose "Looks good" action accepts the refresh from the shade.</summary>
    public const string RefreshCategory = "weekly_refresh";

    public static void MapPushes(this WebApplication app)
    {
        var g = app.MapGroup("/me/push-token").RequireAuthorization();
        g.MapPut("/", async (PushTokenRequest req, WaypointDb db, Waypoint.Api.Auth.CurrentUser me, TimeProvider clock, CancellationToken ct) =>
        {
            var token = req.Token?.Trim();
            if (string.IsNullOrEmpty(token) || token.Length > MaxTokenLength) return Results.BadRequest();
            // One phone per user in v1: the newest token replaces the rest.
            var rows = await db.DeviceTokens.ToListAsync(ct);
            var same = rows.FirstOrDefault(t => t.Token == token);
            db.DeviceTokens.RemoveRange(rows.Where(t => t != same));
            if (same is null)
                db.DeviceTokens.Add(new DeviceToken { Id = Guid.NewGuid(), UserId = me.Id!.Value, Token = token, UpdatedAt = clock.GetUtcNow().UtcDateTime });
            else same.UpdatedAt = clock.GetUtcNow().UtcDateTime;
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        g.MapDelete("/", async (WaypointDb db, CancellationToken ct) =>
        {
            await db.DeviceTokens.ExecuteDeleteAsync(ct);
            return Results.NoContent();
        });
    }
}

/// <summary>Pushes a stored chat message to the user's phone. Runs as the job's user.</summary>
public sealed class PushNotifier(WaypointDb db, IPushSender sender, TimeProvider clock)
{
    private const int MaxBody = 140;

    /// <summary>
    /// The one weekly refresh prompt: sent once, never repeated if ignored, and not counted toward Contact frequency.
    /// "Looks good" in the shade accepts through the refresh ID.
    /// </summary>
    public async Task SendRefreshPromptAsync(Guid refreshId, CancellationToken ct)
    {
        var refresh = await db.WeeklyRefreshes.FirstOrDefaultAsync(r => r.Id == refreshId, ct);
        if (refresh is null || refresh.Status != RefreshStatus.Open) return;
        var tokens = await db.DeviceTokens.ToListAsync(ct);
        if (tokens.Count == 0) return;
        var first = await db.PlanVersions.Where(p => p.RefreshId == refreshId).OrderBy(p => p.CreatedAt).Select(p => p.Id).FirstOrDefaultAsync(ct);
        var data = new Dictionary<string, string>
        {
            ["route"] = Pushes.WeeklyRefreshRoute, ["refreshId"] = refreshId.ToString(), ["planVersionId"] = first.ToString(), ["category"] = Pushes.RefreshCategory,
        };
        foreach (var t in tokens)
            if (await sender.SendAsync(new PushMessage(t.Token, "Weekly refresh", "Your coming week is ready to look over.", data, Pushes.RefreshCategory), ct) == PushResult.InvalidToken)
                db.DeviceTokens.Remove(t);
        await db.SaveChangesAsync(ct);
    }

    public async Task SendForMessageAsync(Guid chatMessageId, CancellationToken ct)
    {
        var message = await db.ChatMessages.FirstOrDefaultAsync(m => m.Id == chatMessageId, ct);
        if (message is null) return;
        // Inside a Pause the Coach stays quiet: the message waits in the chat. Only the way back and the auto-Pause's own line get through.
        if (message.Card is not (Pauses.WelcomeBackCard or SilenceChecker.Card)
            && await Pauses.IsPausedNowAsync(db, clock.GetUtcNow().UtcDateTime, message.AspirationId, ct)) return;
        var tokens = await db.DeviceTokens.ToListAsync(ct);
        if (tokens.Count == 0) return;

        PushMessage Build(string token)
        {
            if (message.Card == "proposal")
            {
                var planVersionId = System.Text.Json.JsonDocument.Parse(message.CardJson ?? "{}").RootElement
                    .TryGetProperty("planVersionId", out var p) ? p.GetGuid().ToString() : "";
                return new PushMessage(token, "Plan ready", "Your Plan is ready to review.",
                    new Dictionary<string, string> { ["route"] = Pushes.PlanReadyRoute, ["planVersionId"] = planVersionId });
            }
            var text = message.Text.Length > MaxBody ? message.Text[..MaxBody] + "…" : message.Text;
            var data = new Dictionary<string, string> { ["route"] = Pushes.CoachRoute };
            // A second nudge after a week of silence: if reminders were being missed, the phone offers the battery setting.
            if (message.Card == SilenceChecker.Card && System.Text.Json.JsonDocument.Parse(message.CardJson ?? "{}").RootElement
                    .TryGetProperty("stage", out var stage) && stage.GetInt32() == 2)
                data["hint"] = Pushes.BatteryHint;
            return new PushMessage(token, "Coach", text, data);
        }

        foreach (var t in tokens)
            if (await sender.SendAsync(Build(t.Token), ct) == PushResult.InvalidToken)
                db.DeviceTokens.Remove(t);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Posts a Coach message the user didn't ask for, and queues its push (the job holds the message ID only).
/// It takes the <see cref="ContactGate"/>'s Send decision as a permit, so no sender can skip the gate.
/// </summary>
public sealed class CoachMessenger(WaypointDb db, JobQueue jobs, TimeProvider clock, Waypoint.Api.Auth.CurrentUser me)
{
    public async Task PostUnpromptedAsync(GateDecision.Send permit, Guid? aspirationId, string text, CancellationToken ct, string? card = null, string? cardJson = null)
    {
        ArgumentNullException.ThrowIfNull(permit);
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(), UserId = me.Id!.Value, Role = "coach", Text = text, AspirationId = aspirationId,
            CreatedAt = clock.GetUtcNow().UtcDateTime, Card = card, CardJson = cardJson, Unprompted = true,
        };
        db.ChatMessages.Add(message);
        jobs.Add(JobKinds.PushMessage, me.Id.Value, new Dictionary<string, Guid> { ["chatMessageId"] = message.Id }, aspirationId);
        await db.SaveChangesAsync(ct);
    }
}
