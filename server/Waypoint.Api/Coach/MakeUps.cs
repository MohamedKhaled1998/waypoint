using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public sealed record MakeUpOffer(Guid ScheduledActionId, DateOnly Date, Guid SlotId, string SlotLabel);

/// <summary>
/// A skipped habit session may move to a free Slot later the same week, never putting two sessions of the habit on
/// one day. Decided here in code; the Coach only phrases the offer.
/// </summary>
public static class MakeUps
{
    public static async Task<MakeUpOffer?> FindAsync(WaypointDb db, ScheduledAction action, TimeProvider clock, CancellationToken ct)
    {
        if (action.HabitId is null) return null; // tasks move at the Weekly refresh
        var habit = await db.Habits.FirstOrDefaultAsync(h => h.Id == action.HabitId, ct);
        if (habit is null) return null;
        var slotIds = habit.SlotIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
        var slots = await db.Slots.Where(s => slotIds.Contains(s.Id)).ToListAsync(ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var first = action.Date.AddDays(1) > today ? action.Date.AddDays(1) : today; // a day that has passed is no offer
        var weekEnd = action.Date.AddDays((7 - (int)action.Date.DayOfWeek) % 7); // the Sunday on or after
        var week = await db.ScheduledActions.Where(a => a.Date >= first && a.Date <= weekEnd).ToListAsync(ct);
        for (var date = first; date <= weekEnd; date = date.AddDays(1))
        {
            if (week.Any(a => a.Date == date && a.AspirationId == action.AspirationId && a.Title == action.Title)) continue;
            foreach (var slot in slots.OrderBy(s => s.Start).Where(s => s.Days.Split(',').Contains(PlanRules.Day(date))))
                if (week.Where(a => a.Date == date && a.SlotId == slot.Id).Sum(a => a.Minutes) + action.Minutes <= PlanRules.Room(slot))
                    return new MakeUpOffer(action.Id, date, slot.Id, slot.Label);
        }
        return null;
    }

    public static void MapMakeUps(this WebApplication app)
    {
        // Accepting the offer: the session moves, and its skip is forgotten. The server decides the place again.
        app.MapPost("/scheduled-actions/{id:guid}/move-later", async (Guid id, WaypointDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var action = await db.ScheduledActions.FirstOrDefaultAsync(a => a.Id == id, ct);
            if (action is null) return Results.NotFound();
            var checkIn = await db.CheckIns.FirstOrDefaultAsync(c => c.ScheduledActionId == id, ct);
            if (checkIn?.State != CheckInState.Skipped || await FindAsync(db, action, clock, ct) is not { } offer) return Results.Conflict(new { error = "no_free_slot" });
            action.Date = offer.Date;
            action.SlotId = offer.SlotId;
            db.CheckIns.Remove(checkIn);
            await db.SaveChangesAsync(ct);
            var slot = await db.Slots.FirstAsync(s => s.Id == action.SlotId, ct);
            return Results.Ok(Plans.ActionView(action, slot, null));
        }).RequireAuthorization();
    }
}
