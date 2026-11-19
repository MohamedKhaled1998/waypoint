using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// The Settings page's server side. Most settings just change a field; two bend the Plan and so never change it directly:
/// a Time budget change becomes a Re-plan the user approves, and Slot edits wait for the next Weekly refresh.
/// </summary>
public static class Settings
{
    public const string BudgetChangeReason = "time_budget_change";
    public const int SnapMinutes = 15;
    /// <summary>The Slot track runs 05:00 to 24:00; midnight is kept as 23:59, the last minute a "HH:mm" time can name.</summary>
    public static readonly TimeOnly SlotFrom = new(5, 0);
    public const string DayEnd = "24:00", DayEndStored = "23:59";
    private static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    private static readonly string[] Frequencies = ["Quiet", "Gentle", "Chatty"];

    public sealed record BudgetRequest(double? Hours);
    public sealed record SlotRequest(string? Label, string[]? Days, string? Start, string? End);
    public sealed record FrequencyRequest(string? Value);
    public sealed record QuietRequest(string? Start, string? End);
    public sealed record RefreshRequest(string? Day, string? Time);
    public sealed record AlarmsRequest(bool? Allowed);

    /// <summary>Time budget is in whole or half hours, between one and forty a week.</summary>
    public static bool ValidBudget(double? hours) => hours is >= 1 and <= 40 && hours * 2 == Math.Floor(hours.Value * 2);

    private static bool Snapped(TimeOnly t) => t.Minute % SnapMinutes == 0 && t.Second == 0;

    /// <summary>A time of day on the 15-minute grid ("HH:mm").</summary>
    public static TimeOnly? SnappedTime(string? s) => HandOff.Time(s) is { } t && Snapped(t) ? t : null;

    /// <summary>A Slot's window as stored: 05:00 to 24:00 on the 15-minute grid, start before end.</summary>
    public static (string Start, string End)? SlotWindow(string? start, string? end)
    {
        if (SnappedTime(start) is not { } s || s < SlotFrom) return null;
        TimeOnly e;
        if (end == DayEnd) e = new TimeOnly(23, 59);
        else if (SnappedTime(end) is { } t) e = t;
        else return null;
        return s < e ? (s.ToString("HH:mm", CultureInfo.InvariantCulture), e.ToString("HH:mm", CultureInfo.InvariantCulture)) : null;
    }

    private static string? DaysText(string[]? days) =>
        days is { Length: > 0 } && days.All(Days.Contains) ? string.Join(",", Days.Where(days.Contains)) : null;

    public static void MapSettings(this WebApplication app)
    {
        var g = app.MapGroup("/settings").RequireAuthorization();
        g.MapGet("/", async (WaypointDb db, CancellationToken ct) => Results.Ok(await ViewAsync(db, ct)));

        // Not a direct change: the Coach proposes a Re-plan to fit, and the budget moves when it is approved.
        g.MapPut("/time-budget", async (BudgetRequest req, WaypointDb db, JobQueue jobs, CancellationToken ct) =>
        {
            if (!ValidBudget(req.Hours)) return Results.BadRequest();
            var user = await db.Users.SingleAsync(ct);
            if (user.TimeBudgetHours is not { } current) { user.TimeBudgetHours = req.Hours; await db.SaveChangesAsync(ct); return Results.Ok(await ViewAsync(db, ct)); }
            user.RequestedTimeBudgetHours = req.Hours == current ? null : req.Hours;
            // Asking for the current budget again withdraws the request, and the proposals still waiting with it.
            if (user.RequestedTimeBudgetHours is null) await ReplaceWaitingBudgetProposalsAsync(db, ct);
            if (user.RequestedTimeBudgetHours is not null) jobs.Add(JobKinds.BudgetChange, user.Id, new Dictionary<string, Guid> { ["userId"] = user.Id });
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ViewAsync(db, ct));
        });

        // Slot edits wait for the next Weekly refresh. Editing a Slot again replaces its waiting edit.
        g.MapPut("/slots/{id:guid}", async (Guid id, SlotRequest req, WaypointDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!await db.Slots.AnyAsync(s => s.Id == id, ct)) return Results.NotFound();
            return await QueueSlotAsync(id, null, req, db, clock, ct);
        });
        g.MapPost("/slots", (SlotRequest req, WaypointDb db, TimeProvider clock, CancellationToken ct) => QueueSlotAsync(null, null, req, db, clock, ct));
        // A waiting new Slot is edited or taken back by its edit ID, so a correction never adds a second Slot.
        g.MapPut("/slots/new/{editId:guid}", async (Guid editId, SlotRequest req, WaypointDb db, TimeProvider clock, CancellationToken ct) =>
            !await db.SlotEdits.AnyAsync(e => e.Id == editId && e.SlotId == null, ct) ? Results.NotFound() : await QueueSlotAsync(null, editId, req, db, clock, ct));
        g.MapDelete("/slots/new/{editId:guid}", async (Guid editId, WaypointDb db, CancellationToken ct) =>
        {
            var edit = await db.SlotEdits.FirstOrDefaultAsync(e => e.Id == editId && e.SlotId == null, ct);
            if (edit is null) return Results.NotFound();
            db.SlotEdits.Remove(edit);
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ViewAsync(db, ct));
        });

        g.MapPut("/contact-frequency", async (FrequencyRequest req, WaypointDb db, CancellationToken ct) =>
        {
            if (!Frequencies.Contains(req.Value)) return Results.BadRequest();
            (await db.Users.SingleAsync(ct)).ContactFrequency = req.Value;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ViewAsync(db, ct));
        });

        // Quiet hours may run over midnight (22:00 to 07:00): one span, so only equal ends are refused.
        g.MapPut("/quiet-hours", async (QuietRequest req, WaypointDb db, CancellationToken ct) =>
        {
            if (SnappedTime(req.Start) is not { } s || SnappedTime(req.End) is not { } e || s == e) return Results.BadRequest();
            var user = await db.Users.SingleAsync(ct);
            user.QuietStart = req.Start!; user.QuietEnd = req.End!;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ViewAsync(db, ct));
        });

        g.MapPut("/weekly-refresh", async (RefreshRequest req, WaypointDb db, TimeProvider clock, CancellationToken ct) =>
        {
            if (!Enum.TryParse<DayOfWeek>(req.Day, true, out var day) || !Enum.IsDefined(day) || SnappedTime(req.Time) is null) return Results.BadRequest();
            var user = await db.Users.SingleAsync(ct);
            user.RefreshDay = day; user.RefreshTime = req.Time!;
            // The refresh already booked moves to the new day and time.
            var now = clock.GetUtcNow().UtcDateTime;
            var booked = await db.WeeklyRefreshes.FirstOrDefaultAsync(r => r.Status == RefreshStatus.Scheduled, ct);
            if (booked is not null)
            {
                booked.RunAt = WeeklyRefreshes.NextRun(user, now);
                var job = (await db.Jobs.Where(j => j.Kind == JobKinds.WeeklyRefresh && j.State == JobState.Pending).ToListAsync(ct))
                    .FirstOrDefault(j => j.PayloadJson.Contains(booked.Id.ToString()));
                if (job is not null) job.NextRunAt = booked.RunAt;
            }
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ViewAsync(db, ct));
        });

        // What the phone last saw: whether exact alarms are allowed.
        g.MapPut("/exact-alarms", async (AlarmsRequest req, WaypointDb db, CancellationToken ct) =>
        {
            if (req.Allowed is null) return Results.BadRequest();
            (await db.Users.SingleAsync(ct)).ExactAlarmsAllowed = req.Allowed;
            await db.SaveChangesAsync(ct);
            return Results.Ok(await ViewAsync(db, ct));
        });
    }

    private static async Task<IResult> QueueSlotAsync(Guid? slotId, Guid? newEditId, SlotRequest req, WaypointDb db, TimeProvider clock, CancellationToken ct)
    {
        if (DaysText(req.Days) is not { } days || SlotWindow(req.Start, req.End) is not { } window) return Results.BadRequest();
        var user = await db.Users.SingleAsync(ct);
        var label = string.IsNullOrWhiteSpace(req.Label) ? null : req.Label.Trim();
        if (label is { Length: > 64 }) return Results.BadRequest();
        var existing = slotId is { } sid ? await db.Slots.FirstAsync(s => s.Id == sid, ct) : null;
        label ??= existing?.Label ?? "Slot";
        if (slotId is not null)
            db.SlotEdits.RemoveRange(await db.SlotEdits.Where(e => e.SlotId == slotId).ToListAsync(ct));
        if (newEditId is { } eid)
            db.SlotEdits.RemoveRange(await db.SlotEdits.Where(e => e.Id == eid && e.SlotId == null).ToListAsync(ct));
        db.SlotEdits.Add(new SlotEdit
        {
            Id = newEditId ?? Guid.NewGuid(), UserId = user.Id, SlotId = slotId, Label = label, Days = days, Start = window.Start, End = window.End,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(ct);
        return Results.Ok(await ViewAsync(db, ct));
    }

    /// <summary>The Weekly refresh applies what is waiting: changed Slots keep their ID, so the Plan's links hold.</summary>
    public static async Task ApplySlotEditsAsync(WaypointDb db, CancellationToken ct)
    {
        var edits = await db.SlotEdits.OrderBy(e => e.CreatedAt).ToListAsync(ct);
        if (edits.Count == 0) return;
        var slots = await db.Slots.ToDictionaryAsync(s => s.Id, ct);
        foreach (var e in edits)
        {
            if (e.SlotId is { } id && slots.TryGetValue(id, out var slot)) { slot.Label = e.Label; slot.Days = e.Days; slot.Start = e.Start; slot.End = e.End; }
            else if (e.SlotId is null) db.Slots.Add(new Slot { Id = Guid.NewGuid(), UserId = e.UserId, Label = e.Label, Days = e.Days, Start = e.Start, End = e.End });
        }
        db.SlotEdits.RemoveRange(edits);
    }

    /// <summary>The proposals of a Time budget change that still wait for a tap are replaced: the request behind them is gone.</summary>
    public static Task<int> ReplaceWaitingBudgetProposalsAsync(WaypointDb db, CancellationToken ct) =>
        db.PlanVersions.Where(p => p.Reason == BudgetChangeReason && p.Status == PlanVersionStatus.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Replaced), ct);

    /// <summary>
    /// A request with no proposal waiting and no job about to make one is stale (its proposals were replaced by newer ones): clear it,
    /// so Settings stops showing it and a later approval cannot apply it.
    /// </summary>
    public static async Task ClearStaleBudgetRequestAsync(WaypointDb db, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(ct);
        if (user.RequestedTimeBudgetHours is null) return;
        if (await db.PlanVersions.AnyAsync(p => p.Reason == BudgetChangeReason && p.Status == PlanVersionStatus.Proposed, ct)) return;
        if (await db.Jobs.AnyAsync(j => j.Kind == JobKinds.BudgetChange && j.State == JobState.Pending, ct)) return;
        user.RequestedTimeBudgetHours = null;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>When the Time budget moves, active Aspirations with no Plan keep their share of it: scaled by the same ratio.</summary>
    public static async Task ScaleUnplannedSharesAsync(WaypointDb db, double ratio, CancellationToken ct)
    {
        var planned = await db.PlanVersions.Where(p => p.Status == PlanVersionStatus.Approved).Select(p => p.AspirationId).ToListAsync(ct);
        foreach (var a in await db.Aspirations.Where(a => a.Status == "active" && !planned.Contains(a.Id)).ToListAsync(ct))
            a.ShareHoursPerWeek = Math.Round(a.ShareHoursPerWeek * ratio, 2);
    }

    private static double Hours(Slot s) => Math.Round(Slots.Count(s.Days) * PlanRules.Room(s) / 60, 2);

    private static async Task<object> ViewAsync(WaypointDb db, CancellationToken ct)
    {
        var u = await db.Users.SingleAsync(ct);
        var edits = await db.SlotEdits.ToListAsync(ct);
        var slots = (await db.Slots.OrderBy(s => s.Start).ToListAsync(ct)).Select(s => new
        {
            s.Id, s.Label, Days = s.Days.Split(','), s.Start, s.End, Hours = Hours(s),
            Pending = edits.Where(e => e.SlotId == s.Id).Select(e => new { e.Label, Days = e.Days.Split(','), e.Start, e.End }).FirstOrDefault(),
        });
        var split = await db.Aspirations.Where(a => a.Status == "active").OrderBy(a => a.CreatedAt)
            .Select(a => new { AspirationId = a.Id, a.ShortName, a.Hue, Hours = a.ShareHoursPerWeek }).ToListAsync(ct);
        var newSlots = edits.Where(e => e.SlotId is null).Select(e => new { e.Id, e.Label, Days = e.Days.Split(','), e.Start, e.End });
        return new
        {
            u.TimeBudgetHours, u.RequestedTimeBudgetHours, Split = split, Slots = slots, NewSlots = newSlots,
            u.ContactFrequency, u.QuietStart, u.QuietEnd, RefreshDay = u.RefreshDay.ToString().ToLowerInvariant(), u.RefreshTime,
            u.ExactAlarmsAllowed,
        };
    }
}

public static class Slots
{
    public static int Count(string days) => days.Split(',', StringSplitOptions.RemoveEmptyEntries).Length;
}

public sealed partial class WeeklyRefreshRunner
{
    /// <summary>
    /// The user asked for a different Time budget in Settings: one proposed Re-plan per Aspiration with a Plan, its share and
    /// habit minutes scaled by the same ratio. The budget itself moves when a proposal is approved. With nothing planned yet, it just moves.
    /// </summary>
    public async Task ProposeBudgetChangeAsync(CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(ct);
        if (user.RequestedTimeBudgetHours is not { } requested) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var (start, slots, planned, currents, pauses, used) = await PrepareAsync(now, respectPauses: false, only: null, ct);
        if (user.TimeBudgetHours is not { } current || current <= 0 || planned.Count == 0)
        {
            if (user.TimeBudgetHours is > 0 and var old) await Settings.ScaleUnplannedSharesAsync(db, requested / old, ct);
            user.TimeBudgetHours = requested;
            user.RequestedTimeBudgetHours = null;
            await db.SaveChangesAsync(ct);
            return;
        }
        anchorHabitsInAll = 0;
        var ratio = requested / current;
        // A newer request replaces proposals still waiting for their tap.
        await Settings.ReplaceWaitingBudgetProposalsAsync(db, ct);

        foreach (var aspiration in planned)
        {
            var currentVersion = currents.First(p => p.AspirationId == aspiration.Id);
            var version = await ProposeAsync(aspiration, currentVersion, slots, used, start, now, null, pauses, ct,
                reshape: habits => ScaleTo(habits, ratio, slots), reason: Settings.BudgetChangeReason,
                stamp: v =>
                {
                    v.SourceVersionId = currentVersion.Id;
                    v.ShareBeforeHours = aspiration.ShareHoursPerWeek;
                    v.ShareAfterHours = Math.Round(aspiration.ShareHoursPerWeek * ratio, 2);
                },
                why: Settings.BudgetChangeReason);
            BudgetCuts.ProposalCard(db, jobs, aspiration, version, now);
        }
        await db.SaveChangesAsync(ct);
        log.LogInformation("Time budget change proposed for {Count} Aspirations", planned.Count);
    }

    /// <summary>Slot habits' sessions grow or shrink by the ratio, in steps of five minutes, never under ten nor over what their Slots hold.</summary>
    private static List<JsonObject> ScaleTo(List<H> habits, double ratio, Dictionary<Guid, Slot> slots)
    {
        var changes = new List<JsonObject>();
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            if (h.IsAnchor) continue;
            var room = h.SlotIds.Where(slots.ContainsKey).Select(id => (int)PlanRules.Room(slots[id])).DefaultIfEmpty(int.MaxValue).Min();
            var minutes = (int)(Math.Round(h.Minutes * ratio / WeeklyRefreshes.MinutesStep) * WeeklyRefreshes.MinutesStep);
            minutes = Math.Min(room, Math.Max(BudgetCuts.MinSessionMinutes, minutes));
            if (minutes == h.Minutes) continue;
            habits[i] = h with { Minutes = minutes };
            changes.Add(Change(Settings.BudgetChangeReason, h.Title, "minutes", h.Minutes, minutes));
        }
        return changes;
    }
}
