using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public static class ProgressStates
{
    public const string TooEarly = "too_early";
    public const string OnTrack = "on_track";
    public const string Slipping = "slipping";
    public const string Ahead = "ahead";
    /// <summary>The optimistic end of the final Milestone is past the Settle-for date; the UI says "Your call".</summary>
    public const string PastSettleFor = "past_settle_for";
}

/// <summary>One Aspiration's state as judged at one Weekly refresh, stored so the next refresh can ask "two in a row". User-owned.</summary>
public sealed class ProgressReading
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AspirationId { get; set; }
    public Guid RefreshId { get; set; }
    public Guid? PlanVersionId { get; set; }
    public DateTime At { get; set; }
    public double? Pace { get; set; }
    public required string State { get; set; }
    /// <summary>The middle of the next Milestone's Projection was past its date.</summary>
    public bool MiddleOver { get; set; }
    /// <summary>The late end of the next Milestone's Projection was at least a week before its date.</summary>
    public bool LateEarly { get; set; }
    public bool PastSettleFor { get; set; }
    /// <summary>The Coach said a line about it at this refresh (a state change, or the monthly "where you stand").</summary>
    public bool Spoke { get; set; }
}

public sealed record WeekScore(int Back, int Planned, int Done)
{
    public double? Pace => Planned > 0 ? (double)Done / Planned : null;
}

public sealed record Projected(DateOnly Early, DateOnly Middle, DateOnly Late);

/// <summary>Pace, Projection and the state rules. Pure arithmetic: the Coach model only phrases what comes out.</summary>
public static class ProgressMath
{
    public const int WindowWeeks = 4;
    /// <summary>With less history than this, a Projection is never narrower than a week either side.</summary>
    public const int SettledWeeks = 6;
    public const double PaceFloor = 0.1;
    /// <summary>The first this many Weekly refreshes of a Plan give no dated Projection.</summary>
    public const int TooEarlyRefreshes = 2;

    /// <summary>Minutes done over minutes planned, the most recent week counting 4, the one before 3, then 2 and 1.</summary>
    public static double? Pace(IReadOnlyList<WeekScore> weeks)
    {
        var planned = weeks.Where(w => w.Back is >= 1 and <= WindowWeeks).Sum(w => (WindowWeeks + 1 - w.Back) * (double)w.Planned);
        if (planned == 0) return null;
        return weeks.Where(w => w.Back is >= 1 and <= WindowWeeks).Sum(w => (WindowWeeks + 1 - w.Back) * (double)w.Done) / planned;
    }

    public static string PaceWords(double? pace) => pace switch
    {
        null => "none",
        >= 0.9 => "full",
        >= 0.7 => "steady",
        >= 0.4 => "easing",
        _ => "light",
    };

    /// <summary>
    /// When the work planned up to <paramref name="date"/> will be done if Pace holds: the remaining planned weeks over Pace.
    /// The ends come from the fastest and slowest weekly Pace of the window.
    /// </summary>
    public static Projected? Project(DateOnly today, DateOnly date, IReadOnlyList<WeekScore> weeks)
    {
        if (Pace(weeks) is not { } pace) return null;
        var scored = weeks.Where(w => w.Pace is not null).Select(w => w.Pace!.Value).ToList();
        var remainingWeeks = Math.Max(0, date.DayNumber - today.DayNumber) / 7.0;
        DateOnly At(double p) => today.AddDays((int)Math.Round(remainingWeeks / Math.Max(p, PaceFloor) * 7));
        var middle = At(pace);
        var early = At(scored.Max());
        var late = At(scored.Min());
        if (scored.Count < SettledWeeks)
        {
            if (early > middle.AddDays(-7)) early = middle.AddDays(-7);
            if (late < middle.AddDays(7)) late = middle.AddDays(7);
        }
        if (early < today) early = today;
        return new Projected(early, middle, late);
    }

    /// <summary>
    /// The state at a Weekly refresh. <paramref name="refreshNumber"/> counts this refresh among the Aspiration's.
    /// Slipping and Ahead need two refreshes in a row; Past Settle-for needs one.
    /// </summary>
    public static string Judge(int refreshNumber, bool middleOver, bool lateEarly, bool pastSettleFor, ProgressReading? previous)
    {
        if (refreshNumber <= TooEarlyRefreshes) return ProgressStates.TooEarly;
        if (pastSettleFor) return ProgressStates.PastSettleFor;
        if (middleOver && previous is { MiddleOver: true }) return ProgressStates.Slipping;
        if (lateEarly && previous is { LateEarly: true }) return ProgressStates.Ahead;
        return ProgressStates.OnTrack;
    }

    /// <summary>"early", "mid" or "late" in the month the date falls in.</summary>
    public static string PartOfMonth(DateOnly d) => d.Day <= 10 ? "early" : d.Day <= 20 ? "mid" : "late";
}

/// <summary>What the server knows about one Aspiration's progress as of a day: Pace, the Milestones, their Projections and the stored state.</summary>
public sealed record AspirationProgress(
    Aspiration Aspiration, PlanVersion? Current, IReadOnlyList<Milestone> Milestones, IReadOnlyList<WeekScore> Weeks, double? Pace,
    ProgressReading? Latest, int Readings, Milestone? Next, Projected? NextProjection, Projected? FinalProjection)
{
    public bool TooEarly => Latest is null || Latest.State == ProgressStates.TooEarly;
}

/// <summary>Maintenance: how many of the last weeks the habits were kept up (mostly done).</summary>
public sealed record Consistency(int WeeksKeptUp, int OfWeeks);

public sealed class ProgressCalculator(WaypointDb db, TimeProvider clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    /// <summary>The first day after "today": the weeks Pace looks at end the day before it.</summary>
    public DateOnly Start => Today.AddDays(1);

    private sealed record Row(Guid Id, DateOnly Date, int Minutes, string? State, int? Spent);

    /// <summary>Minutes planned and done per week, newest first. Each Action is scored with the minutes of the Plan version it ran under.</summary>
    public async Task<List<WeekScore>> WeeksAsync(Guid aspirationId, DateOnly start, int weeks, CancellationToken ct)
    {
        var from = start.AddDays(-7 * weeks);
        var rows = await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.Date >= from && a.Date < start)
            .Select(a => new Row(a.Id, a.Date, a.Minutes,
                db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => c.State).FirstOrDefault(),
                db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => c.MinutesSpent).FirstOrDefault()))
            .ToListAsync(ct);
        var passed = await ListPassedAsync(ct);
        return Enumerable.Range(1, weeks).Select(back =>
        {
            var lo = start.AddDays(-7 * back);
            var week = rows.Where(r => r.Date >= lo && r.Date < lo.AddDays(7)).Where(r => r.State is not null || r.Date <= passed).ToList();
            return new WeekScore(back, week.Sum(r => r.Minutes), week.Sum(r => Done(r)));
        }).ToList();
    }

    /// <summary>Weeks kept up of the last <see cref="AspirationLifecycle.ConsistencyWeeks"/>: a week counts when it was mostly done, as the refresh judges it.</summary>
    public async Task<Consistency> ConsistencyAsync(Guid aspirationId, DateOnly start, CancellationToken ct)
    {
        var weeks = await WeeksAsync(aspirationId, start, AspirationLifecycle.ConsistencyWeeks, ct);
        return new Consistency(weeks.Count(w => w.Planned > 0 && w.Done >= WeeklyRefreshes.MostlyDone * w.Planned), AspirationLifecycle.ConsistencyWeeks);
    }

    private static int Done(Row r) => r.State switch
    {
        CheckInState.Done => r.Minutes,
        CheckInState.Partly => r.Spent ?? 0,
        _ => 0,
    };

    /// <summary>The day up to which an unreported Action counts as not done: the latest refresh whose "did it" list has been answered or has lapsed.</summary>
    public async Task<DateOnly> ListPassedAsync(CancellationToken ct)
    {
        var opened = await db.WeeklyRefreshes.Where(r => (r.Status == RefreshStatus.Accepted || r.Status == RefreshStatus.AutoApplied) && r.OpenedAt != null)
            .OrderByDescending(r => r.OpenedAt).Select(r => r.OpenedAt).FirstOrDefaultAsync(ct);
        return opened is { } o ? DateOnly.FromDateTime(o) : DateOnly.MinValue;
    }

    public async Task<AspirationProgress> ComputeAsync(Aspiration a, DateOnly start, CancellationToken ct)
    {
        var current = await db.PlanVersions.FirstOrDefaultAsync(p => p.AspirationId == a.Id && p.Status == PlanVersionStatus.Approved, ct);
        var milestones = current is null ? [] : await db.Milestones.Where(m => m.PlanVersionId == current.Id).OrderBy(m => m.Date).ToListAsync(ct);
        var weeks = await WeeksAsync(a.Id, start, ProgressMath.WindowWeeks, ct);
        var readings = await db.ProgressReadings.Where(r => r.AspirationId == a.Id).OrderByDescending(r => r.At).ToListAsync(ct);
        var today = start.AddDays(-1);
        var next = milestones.FirstOrDefault(m => m.State != "reached");
        var last = milestones.Count > 0 ? milestones[^1] : null;
        return new AspirationProgress(a, current, milestones, weeks, ProgressMath.Pace(weeks), readings.FirstOrDefault(), readings.Count, next,
            next is null ? null : ProgressMath.Project(today, next.Date, weeks),
            last is null || last.State == "reached" ? null : ProgressMath.Project(today, last.Date, weeks));
    }
}
