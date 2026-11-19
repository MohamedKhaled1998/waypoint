using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// A prep habit (meal prep) supports an Anchor habit (eat at home). When a prep session is skipped, the Anchor habit's
/// nights from that session until the next prep are marked at risk, and the Coach is told. Decided here in code.
/// </summary>
public static class PrepRisk
{
    public const int NightsAheadDays = 7;

    /// <summary>Re-marks the linked nights after a prep Action's Check-in changed (or was undone). Saves.</summary>
    public static async Task UpdateAsync(WaypointDb db, ScheduledAction prep, CancellationToken ct)
    {
        var habit = prep.HabitId is { } hid ? await db.Habits.FirstOrDefaultAsync(h => h.Id == hid, ct) : null;
        if (habit?.PrepFor is not { } linked) return;
        var skipped = await db.CheckIns.AnyAsync(c => c.ScheduledActionId == prep.Id && c.State == CheckInState.Skipped, ct);
        var nextPrep = await db.ScheduledActions.Where(a => a.AspirationId == prep.AspirationId && a.Title == prep.Title && a.Date > prep.Date)
            .OrderBy(a => a.Date).Select(a => (DateOnly?)a.Date).FirstOrDefaultAsync(ct);
        var until = nextPrep ?? prep.Date.AddDays(NightsAheadDays);
        var nights = await db.ScheduledActions.Where(a => a.AspirationId == prep.AspirationId && a.Title == linked && a.AnchorId != null
            && a.Date >= prep.Date && a.Date < until).ToListAsync(ct);
        foreach (var n in nights) n.AtRisk = skipped;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>What the Coach is told next to a skipped prep: facts and the two ways out, never blame. Null when the Check-in is not one.</summary>
    public static async Task<string?> NoteForCoachAsync(WaypointDb db, CheckIn checkIn, CancellationToken ct)
    {
        if (checkIn.State != CheckInState.Skipped) return null;
        var prep = await db.ScheduledActions.FirstOrDefaultAsync(a => a.Id == checkIn.ScheduledActionId, ct);
        var linked = prep?.HabitId is { } hid ? (await db.Habits.FirstOrDefaultAsync(h => h.Id == hid, ct))?.PrepFor : null;
        if (prep is null || linked is null) return null;
        var atRisk = await db.ScheduledActions.CountAsync(a => a.AspirationId == prep.AspirationId && a.Title == linked && a.AtRisk, ct);
        return atRisk == 0 ? null
            : $"[App note: the skipped prep puts {atRisk} of the nights for \"{linked}\" at risk. Offer to move the prep or ease that week's target. No blame.]";
    }
}
