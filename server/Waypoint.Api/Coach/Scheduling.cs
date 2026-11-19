using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// Scheduling across Aspirations, decided in code: the Aspirations share the user's Slots, so one Slot-day never holds
/// more minutes than the Slot, every Aspiration with Actions in a week keeps at least one, and when two compete for
/// room the Aspiration closest to slipping past its Settle-for line wins. The user never ranks Aspirations.
/// </summary>
public static class Scheduling
{
    /// <summary>One occurrence competing for room. <c>Fixed</c> ones (already checked in) are never moved.</summary>
    public sealed record Candidate(Guid Key, Guid AspirationId, DateOnly Date, Guid SlotId, int Minutes, bool Fixed);

    /// <summary>Lower rank wins. Rank by slack to the Settle-for date (Milestones planned to the line leave none), then earlier line, then older.</summary>
    public static Dictionary<Guid, int> Rank(IEnumerable<(Guid Id, DateOnly? SettleFor, DateOnly? LastMilestone, DateTime CreatedAt)> aspirations) =>
        aspirations
            .OrderBy(a => a.SettleFor is { } s && a.LastMilestone is { } m ? s.DayNumber - m.DayNumber : int.MaxValue)
            .ThenBy(a => a.SettleFor ?? DateOnly.MaxValue)
            .ThenBy(a => a.CreatedAt)
            .Select((a, i) => (a.Id, i))
            .ToDictionary(x => x.Id, x => x.i);

    /// <summary>The keys of the candidates that do not get a place.</summary>
    public static HashSet<Guid> Dropped(IReadOnlyList<Candidate> candidates, IReadOnlyDictionary<Guid, double> slotRoom, IReadOnlyDictionary<Guid, int> rank)
    {
        int Rank(Candidate c) => rank.GetValueOrDefault(c.AspirationId, int.MaxValue);
        var kept = new HashSet<Guid>(candidates.Where(c => c.Fixed).Select(c => c.Key));
        var used = new Dictionary<(DateOnly, Guid), double>();
        double Used((DateOnly, Guid) k) => used.GetValueOrDefault(k);
        foreach (var c in candidates.Where(c => c.Fixed)) used[(c.Date, c.SlotId)] = Used((c.Date, c.SlotId)) + c.Minutes;

        foreach (var c in candidates.Where(c => !c.Fixed).OrderBy(Rank))
            if (Used((c.Date, c.SlotId)) + c.Minutes <= slotRoom.GetValueOrDefault(c.SlotId))
            {
                kept.Add(c.Key);
                used[(c.Date, c.SlotId)] = Used((c.Date, c.SlotId)) + c.Minutes;
            }

        // The weekly minimum: an Aspiration with nothing left in a week takes a place from one that has more than one.
        DateOnly Week(DateOnly d) => d.AddDays(-(((int)d.DayOfWeek + 6) % 7));
        foreach (var starved in candidates.GroupBy(c => (c.AspirationId, Week: Week(c.Date))).OrderBy(g => Rank(g.First())))
        {
            if (starved.Any(c => kept.Contains(c.Key))) continue;
            foreach (var want in starved.OrderBy(c => c.Date))
            {
                var slotDay = (want.Date, want.SlotId);
                var room = slotRoom.GetValueOrDefault(want.SlotId);
                var evict = new List<Candidate>();
                var free = room - Used(slotDay);
                foreach (var other in candidates
                    .Where(c => !c.Fixed && kept.Contains(c.Key) && (c.Date, c.SlotId) == slotDay && c.AspirationId != want.AspirationId)
                    .OrderByDescending(Rank))
                {
                    if (free >= want.Minutes) break;
                    var inWeek = candidates.Count(c => kept.Contains(c.Key) && c.AspirationId == other.AspirationId && Week(c.Date) == Week(other.Date)
                        && !evict.Contains(c));
                    if (inWeek <= 1) continue;
                    evict.Add(other);
                    free += other.Minutes;
                }
                if (free < want.Minutes) continue;
                foreach (var e in evict)
                {
                    kept.Remove(e.Key);
                    used[slotDay] = Used(slotDay) - e.Minutes;
                }
                kept.Add(want.Key);
                used[slotDay] = Used(slotDay) + want.Minutes;
                break;
            }
        }
        return candidates.Where(c => !kept.Contains(c.Key)).Select(c => c.Key).ToHashSet();
    }

    /// <summary>
    /// Places the Actions just scheduled for <paramref name="aspirationId"/> among those of the user's other active Aspirations
    /// from <paramref name="from"/> on: drops the ones that lose a place (unchecked Actions of other Aspirations are removed).
    /// The removal is intended: the Aspiration that loses room keeps its weekly minimum and is re-planned by its next Weekly refresh,
    /// which plans from the Actions that remain. Nothing else refers to a removed Action: its reminder text is a column on it, jobs
    /// refer to the PlanVersion, and its removal is logged for the phone to pull. Pace and Projection are computed from the rows left.
    /// </summary>
    public static async Task ResolveAsync(WaypointDb db, Guid aspirationId, DateOnly from, List<ScheduledAction> added, CancellationToken ct)
    {
        var active = await db.Aspirations.Where(a => a.Status == "active" || a.Status == AspirationLifecycle.Maintenance).ToListAsync(ct);
        var activeIds = active.Select(a => a.Id).ToList();
        var others = await db.ScheduledActions.Where(a => a.AspirationId != aspirationId && a.Date >= from && activeIds.Contains(a.AspirationId)).ToListAsync(ct);
        var kept = await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.Date >= from
            && db.CheckIns.Any(c => c.ScheduledActionId == a.Id)).ToListAsync(ct);
        var checkedIn = (await db.CheckIns.Where(c => others.Select(a => a.Id).Contains(c.ScheduledActionId)).Select(c => c.ScheduledActionId).ToListAsync(ct)).ToHashSet();
        var removing = db.ChangeTracker.Entries<ScheduledAction>().Where(e => e.State == EntityState.Deleted).Select(e => e.Entity.Id).ToHashSet();

        var all = others.Where(a => !removing.Contains(a.Id)).Select(a => (Action: a, Fixed: checkedIn.Contains(a.Id)))
            .Concat(kept.Where(a => !removing.Contains(a.Id)).Select(a => (Action: a, Fixed: true)))
            .Concat(added.Select(a => (Action: a, Fixed: false)))
            // Anchor habits take no Slot time, so they never compete for a Slot.
            .Where(x => x.Action.SlotId is not null)
            .Select(x => new Candidate(x.Action.Id, x.Action.AspirationId, x.Action.Date, x.Action.SlotId!.Value, x.Action.Minutes, x.Fixed))
            .ToList();

        var slots = await db.Slots.ToListAsync(ct);
        // One PlanVersion per Aspiration: the current approved one (the highest number), never a stale one from before a refresh.
        var approved = await db.PlanVersions.Where(p => activeIds.Contains(p.AspirationId) && p.Status == PlanVersionStatus.Approved)
            .Select(p => new { p.Id, p.AspirationId, p.Number }).ToListAsync(ct);
        var current = approved.GroupBy(p => p.AspirationId).Select(g => g.OrderByDescending(p => p.Number).First()).ToList();
        var currentIds = current.Select(p => p.Id).ToList();
        var lastByVersion = await db.Milestones.Where(m => currentIds.Contains(m.PlanVersionId))
            .GroupBy(m => m.PlanVersionId).Select(g => new { Id = g.Key, Last = (DateOnly?)g.Max(m => m.Date) }).ToListAsync(ct);
        var lastMilestones = current.Select(p => new { p.AspirationId, Last = lastByVersion.FirstOrDefault(l => l.Id == p.Id)?.Last }).ToList();
        var rank = Rank(active.Select(a => (a.Id, a.SettleForDate, lastMilestones.FirstOrDefault(m => m.AspirationId == a.Id)?.Last, a.CreatedAt)));

        var dropped = Dropped(all, slots.ToDictionary(s => s.Id, PlanRules.Room), rank);
        foreach (var a in added.Where(a => dropped.Contains(a.Id))) db.ScheduledActions.Remove(a);
        db.ScheduledActions.RemoveRange(others.Where(a => dropped.Contains(a.Id)));
    }
}
