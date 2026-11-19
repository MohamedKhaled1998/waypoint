using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>The Progress tab and the Aspiration screen's Route, as data. Dates are decided here; the phone words them.</summary>
public static class ProgressApi
{
    public static void MapProgress(this WebApplication app)
    {
        var g = app.MapGroup("/progress").RequireAuthorization();

        g.MapGet("/", async (WaypointDb db, ProgressCalculator calc, CancellationToken ct) =>
        {
            var aspirations = await db.Aspirations.Where(a => a.Status == "active").OrderBy(a => a.CreatedAt).ToListAsync(ct);
            var views = new List<object>();
            foreach (var a in aspirations) views.Add(View(await calc.ComputeAsync(a, calc.Start, ct)));
            return Results.Ok(new
            {
                ThisWeek = await WeekCardAsync(db, calc, aspirations, 0, ct),
                LastWeek = await WeekCardAsync(db, calc, aspirations, 1, ct),
                Aspirations = views,
            });
        });

        g.MapGet("/{aspirationId:guid}", async (Guid aspirationId, WaypointDb db, ProgressCalculator calc, CancellationToken ct) =>
            await db.Aspirations.FirstOrDefaultAsync(a => a.Id == aspirationId && a.Status != "closed", ct) is { } a
                ? Results.Ok(View(await calc.ComputeAsync(a, calc.Start, ct))) : Results.NotFound());
    }

    private static object? Words(Projected? p, bool tooEarly)
    {
        if (tooEarly) return new { Kind = "too_early" };
        if (p is null) return null;
        object W(DateOnly d) => new { Part = ProgressMath.PartOfMonth(d), d.Month };
        // A range no wider than the ±1 week floor is said as one "around" date.
        var narrow = p.Late.DayNumber - p.Early.DayNumber <= 14;
        return new
        {
            Kind = narrow ? "around" : "between",
            Early = p.Early, p.Middle, p.Late,
            From = W(narrow ? p.Middle : p.Early), To = W(narrow ? p.Middle : p.Late),
        };
    }

    private static object View(AspirationProgress p)
    {
        var a = p.Aspiration;
        var state = p.Latest?.State ?? ProgressStates.TooEarly;
        var tooEarly = p.TooEarly;
        var final = p.Milestones.Count > 0 ? p.Milestones[^1] : null;
        var weeks = p.Weeks;
        var firstOpen = p.Next;
        var milestones = p.Milestones.Select(m =>
        {
            var upcoming = m.State != "reached";
            var projection = upcoming ? Words(m == firstOpen ? p.NextProjection : null, tooEarly) : null;
            return new
            {
                m.Id, m.Date, m.Title, m.ReachedWhen, m.Kind, m.State,
                YouAreHere = m == firstOpen,
                // Judged at the refresh, so only the next Milestone carries the stored state; the final one also shows "Your call".
                Pill = !upcoming ? null : m == firstOpen ? state : m == final && state == ProgressStates.PastSettleFor ? state : tooEarly ? ProgressStates.TooEarly : null,
                Projection = projection,
            };
        }).ToList();
        return new
        {
            AspirationId = a.Id, a.ShortName, a.Hue, a.OwnWords, a.HopedForDate, a.SettleForDate, a.SettleForPicture,
            HasPlan = p.Current is not null,
            Pace = p.Pace, PaceWords = ProgressMath.PaceWords(p.Pace),
            State = state,
            MilestonesReached = p.Milestones.Count(m => m.State == "reached"),
            MilestonesTotal = p.Milestones.Count,
            Next = firstOpen is null ? null : milestones.First(m => m.Id == firstOpen.Id),
            Milestones = milestones,
        };
    }

    /// <summary>Hours given against planned, in total and per Aspiration, for a calendar week (0 = this one, 1 = last).</summary>
    private static async Task<object> WeekCardAsync(WaypointDb db, ProgressCalculator calc, List<Aspiration> aspirations, int back, CancellationToken ct)
    {
        var today = calc.Today;
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7) - 7 * back);
        var passed = await calc.ListPassedAsync(ct);
        var ids = aspirations.Select(a => a.Id).ToList();
        var rows = await db.ScheduledActions.Where(x => ids.Contains(x.AspirationId) && x.Date >= monday && x.Date < monday.AddDays(7))
            .Select(x => new { x.AspirationId, x.Date, x.Minutes, Check = db.CheckIns.Where(c => c.ScheduledActionId == x.Id).Select(c => new { c.State, c.MinutesSpent }).FirstOrDefault() })
            .ToListAsync(ct);
        // This week's plan is all there, to be done; last week's unreported Actions count only once the "did it" list has passed.
        var counted = rows.Where(r => back == 0 || r.Check is not null || r.Date <= passed).ToList();
        var per = aspirations.Select(a =>
        {
            var mine = counted.Where(r => r.AspirationId == a.Id).ToList();
            return new
            {
                AspirationId = a.Id, a.ShortName, a.Hue,
                PlannedMinutes = mine.Sum(r => r.Minutes),
                DoneMinutes = mine.Sum(r => r.Check?.State switch { CheckInState.Done => r.Minutes, CheckInState.Partly => r.Check.MinutesSpent ?? 0, _ => 0 }),
            };
        }).ToList();
        var planned = per.Sum(x => x.PlannedMinutes);
        var done = per.Sum(x => x.DoneMinutes);
        var line = planned == 0 ? "nothing_planned" : done >= WeeklyRefreshes.MostlyDone * planned ? "kept_up" : done > 0 ? "some" : "none";
        return new { Start = monday, PlannedMinutes = planned, DoneMinutes = done, Line = line, Aspirations = per };
    }
}
