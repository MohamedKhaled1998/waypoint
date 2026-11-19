using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Data;
using Waypoint.Api.Push;

namespace Waypoint.Api.Coach;

/// <summary>
/// The one "welcome back" after a Pause: asks Catch-up or shift the dates. What is suggested is decided here by how long
/// the Pause ran; the Coach only phrases it. If the model is down it is late, never faked: the job retries.
/// </summary>
public sealed class WelcomeBackWriter(WaypointDb db, CoachMessenger messenger, ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock)
{
    private const string System = """
        You are the Coach in Waypoint, a personal coaching app. The user is back from a Pause. In two or three warm,
        plain sentences, welcome them back without any guilt, and ask whether they would like to Catch up gradually or
        just shift the dates. The first message is JSON with "suggested" ("catch_up" or "shift_dates"): lean toward
        that one, but say the other is fine too. Never invent numbers or dates. No emojis.
        """;

    public async Task WriteAsync(Guid pauseId, CancellationToken ct)
    {
        var pause = await db.Pauses.FirstOrDefaultAsync(p => p.Id == pauseId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (pause is null || pause.WelcomedAt is not null || pause.ScheduledActionId is not null) return;
        if (Pauses.IsActive(pause, now)) return; // not over yet

        var days = Pauses.Days(pause, now);
        var from = DateOnly.FromDateTime(pause.StartsAt);
        var actions = await db.ScheduledActions.Where(a => a.Date >= from).ToListAsync(ct);
        var checkedIn = (await db.CheckIns.Select(c => c.ScheduledActionId).ToListAsync(ct)).ToHashSet();
        var setAside = actions.Count(a => Pauses.IsSetAside([pause], a, checkedIn.Contains(a.Id)));
        if (days < Pauses.WelcomeMinDays || setAside == 0)
        {
            pause.WelcomedAt = now; // "Not today" and empty stretches get no welcome back
            await db.SaveChangesAsync(ct);
            return;
        }

        var suggested = days <= Pauses.CatchUpMaxDays ? Pauses.CatchUp : Pauses.ShiftDates;
        var choice = options.Value.For(CoachCallKind.Chat);
        var reply = new StringBuilder();
        await foreach (var chunk in model.StreamAsync(new CoachRequest(CoachCallKind.Chat, choice.Provider, choice.Model, System,
            [new CoachTurn("user", new JsonObject { ["suggested"] = suggested }.ToJsonString())]), ct))
            reply.Append(chunk);
        if (reply.Length == 0) throw new CoachUnavailableException("empty_reply");

        pause.WelcomedAt = now;
        var card = JsonSerializer.Serialize(new { pauseId = pause.Id, suggested, pauseDays = days, setAside }, JsonSerializerOptions.Web);
        await messenger.PostUnpromptedAsync(pause.AspirationId, reply.ToString(), ct, Pauses.WelcomeBackCard, card);
    }
}
