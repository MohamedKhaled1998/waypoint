using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>One scheduled Action with how it was reported, as the progress measures read it.</summary>
public sealed record Reported(DateOnly Date, int Minutes, string? State, int? MinutesSpent, bool IsAnchor);

/// <summary>
/// Pace, tolerance and consistency for one Aspiration. Deterministic server code; the Coach only phrases the results.
/// Anchor habits take no Slot time, so they are out of Pace and the Time budget; they are tracked as consistency
/// and, like every Action, count in the tolerance bands.
/// </summary>
public static class Progress
{
    public const int PaceWeeks = 4;
    public const int ToleranceWeeks = 2;
    public const double Absorb = 0.7;
    public const double ReThink = 0.3;

    /// <summary>Minutes done ÷ minutes planned over the last 4 weeks, newer weeks counting more (4, 3, 2, 1). Null when nothing was planned.</summary>
    public static double? Pace(IEnumerable<Reported> actions, DateOnly today)
    {
        double done = 0, planned = 0;
        foreach (var a in actions.Where(a => !a.IsAnchor && Counts(a, today)))
        {
            var week = (today.DayNumber - a.Date.DayNumber) / 7; // 0 = this week back to 3 = oldest
            if (week >= PaceWeeks) continue;
            var w = PaceWeeks - week;
            planned += w * a.Minutes;
            done += w * Done(a);
        }
        return planned == 0 ? null : done / planned;
    }

    /// <summary>
    /// The share of sessions in the last two weeks that were done or partly (showing up counts), anchors included,
    /// and the band: Absorb at 70% or more, Ease back below that, Re-think when both weeks were under 30%.
    /// </summary>
    public static (double? Share, string Band) Tolerance(IEnumerable<Reported> actions, DateOnly today)
    {
        var counted = actions.Where(a => Counts(a, today) && today.DayNumber - a.Date.DayNumber < 7 * ToleranceWeeks).ToList();
        if (counted.Count == 0) return (null, "absorb");
        double Share(IEnumerable<Reported> xs) => xs.Count() == 0 ? 1 : xs.Count(a => a.State is CheckInState.Done or CheckInState.Partly) / (double)xs.Count();
        var share = Share(counted);
        var thisWeek = counted.Where(a => today.DayNumber - a.Date.DayNumber < 7).ToList();
        var lastWeek = counted.Except(thisWeek).ToList();
        var band = thisWeek.Count > 0 && lastWeek.Count > 0 && Share(thisWeek) < ReThink && Share(lastWeek) < ReThink ? "re_think"
            : share >= Absorb ? "absorb" : "ease_back";
        return (share, band);
    }

    private static int Done(Reported a) => a.State switch
    {
        CheckInState.Done => a.Minutes,
        CheckInState.Partly => a.MinutesSpent ?? 0,
        _ => 0,
    };

    /// <summary>A reported Action always counts; an unreported one once its day has passed.</summary>
    private static bool Counts(Reported a, DateOnly today) => a.Date <= today && (a.State is not null || a.Date < today);

    public static void MapProgress(this WebApplication app)
    {
        app.MapGet("/aspirations/{id:guid}/progress", async (Guid id, WaypointDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await db.Aspirations.AnyAsync(a => a.Id == id, ct)) return Results.NotFound();
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var from = today.AddDays(-7 * PaceWeeks);
            var rows = await db.ScheduledActions.Where(a => a.AspirationId == id && a.Date > from && a.Date <= today)
                .Select(a => new
                {
                    a.Date, a.Minutes, a.Title, a.AnchorId, Check = db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => new { c.State, c.MinutesSpent }).FirstOrDefault(),
                }).ToListAsync(ct);
            var reported = rows.Select(r => new Reported(r.Date, r.Minutes, r.Check?.State, r.Check?.MinutesSpent, r.AnchorId is not null)).ToList();

            // The Time budget counts the current Plan's Slot habits; Anchor habits add nothing.
            var current = await db.PlanVersions.Where(p => p.AspirationId == id && p.Status == PlanVersionStatus.Approved).Select(p => p.Id).FirstOrDefaultAsync(ct);
            var habits = await db.Habits.Where(h => h.PlanVersionId == current).ToListAsync(ct);
            var anchors = await db.Anchors.ToDictionaryAsync(a => a.Id, ct);
            var (share, band) = Tolerance(reported, today);

            var windowFrom = today.AddDays(-7 * ToleranceWeeks);
            return Results.Ok(new
            {
                PlannedMinutesPerWeek = habits.Where(h => h.AnchorId is null).Sum(h => h.TimesPerWeek * h.Minutes),
                Pace = Pace(reported, today),
                Tolerance = new { Share = share, Band = band },
                Habits = habits.Select(h =>
                {
                    var mine = rows.Where(r => r.Title == h.Title && r.Date > windowFrom && (r.Check is not null || r.Date < today)).ToList();
                    return new
                    {
                        h.Title, h.TimesPerWeek, h.TargetMinutes, h.ReminderOn,
                        Anchor = h.AnchorId is { } aid && anchors.TryGetValue(aid, out var an) ? new { an.Words, an.Time } : null,
                        Done = mine.Count(r => r.Check?.State is CheckInState.Done or CheckInState.Partly), Planned = mine.Count,
                    };
                }),
            });
        }).RequireAuthorization();
    }
}
