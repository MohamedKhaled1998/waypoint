using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>One Milestone's date moving from one day to another.</summary>
public sealed record DateShift(string Ref, string Title, DateOnly Before, DateOnly After);

/// <summary>
/// Milestone dates move only by proposal. The server decides which dates and by how much (never past Settle-for);
/// the Coach only phrases it. A proposal is a Plan version, so approving it goes through Plan review like any other.
/// </summary>
public static class DateProposals
{
    public const string Later = "dates_later";
    public const string Earlier = "dates_earlier";
    public const string SettleChoiceCard = "settle_choice";
    public static readonly string[] SettleOptions = ["move_date", "keep_date_smaller_picture", "make_more_room"];

    public static bool IsDates(string reason) => reason is Later or Earlier;

    /// <summary>Later dates: every open Milestone moves by the whole weeks the next one is projected to miss by, capped at Settle-for.</summary>
    public static List<DateShift> Slip(IReadOnlyList<Milestone> milestones, DateOnly projectedMiddle, DateOnly? settleFor)
    {
        var open = milestones.Where(m => m.State != "reached").OrderBy(m => m.Date).ToList();
        if (open.Count == 0) return [];
        var weeks = (int)Math.Ceiling((projectedMiddle.DayNumber - open[0].Date.DayNumber) / 7.0);
        if (weeks < 1) return [];
        return Shift(open, weeks * 7, settleFor, earliest: null);
    }

    /// <summary>
    /// Later dates for a shortfall while easing back or re-thinking: every open Milestone moves by the whole weeks the shortfall would take
    /// at the Aspiration's planned minutes a week (rounded up, at least one), capped at Settle-for.
    /// </summary>
    public static List<DateShift> ForShortfall(IReadOnlyList<Milestone> milestones, int shortfallMinutes, int weeklyMinutes, DateOnly? settleFor)
    {
        if (shortfallMinutes <= 0) return [];
        var open = milestones.Where(m => m.State != "reached").OrderBy(m => m.Date).ToList();
        if (open.Count == 0) return [];
        var weeks = weeklyMinutes > 0 ? Math.Max(1, (int)Math.Ceiling(shortfallMinutes / (double)weeklyMinutes)) : 1;
        return Shift(open, weeks * 7, settleFor, earliest: null);
    }

    /// <summary>Earlier dates: every open Milestone moves by the whole weeks the next one is ahead by, never before <paramref name="earliest"/>.</summary>
    public static List<DateShift> Sooner(IReadOnlyList<Milestone> milestones, DateOnly lateEnd, DateOnly earliest)
    {
        var open = milestones.Where(m => m.State != "reached").OrderBy(m => m.Date).ToList();
        if (open.Count == 0) return [];
        var weeks = (open[0].Date.DayNumber - lateEnd.DayNumber) / 7;
        if (weeks < 1) return [];
        return Shift(open, -weeks * 7, null, earliest);
    }

    private static List<DateShift> Shift(List<Milestone> open, int days, DateOnly? settleFor, DateOnly? earliest) =>
        open.Select(m =>
        {
            var to = m.Date.AddDays(days);
            if (settleFor is { } cap && to > cap) to = cap < m.Date ? m.Date : cap;
            if (earliest is { } floor && to < floor) to = floor > m.Date ? m.Date : floor;
            return new DateShift(m.Ref, m.Title, m.Date, to);
        }).Where(s => s.After != s.Before).ToList();

    public const string ChangeDatesIntent = "change_dates";
    public const string MoveDateIntent = "move_date";

    /// <summary>
    /// What a chat turn offers: the proposed version (added, not saved) and a plain line for the Coach to word it from.
    /// With no version, nothing was proposed and the line tells the Coach what to say instead (ask how far, or name the Settle-for line).
    /// </summary>
    public sealed record ChatOffer(Guid? VersionId, string Kind, string Brief);

    /// <summary>The most a user's own ask moves the dates in one proposal, in weeks either way.</summary>
    public const int MaxRequestedWeeks = 52;

    /// <summary>
    /// "Change dates" and "Move the date" from an Aspiration: a later-dates proposal the user reviews, decided by the server from the Projection.
    /// "Change dates" stays within Settle-for; "Move the date" is the user asking for the date past it. When no Milestone is projected to miss its
    /// date, the user's own ask still moves them: by the whole weeks <paramref name="requestedWeeks"/> reads from their message (later or earlier),
    /// under the same caps. Null when the intent is not about dates (the chat is still scoped to the Aspiration).
    /// </summary>
    public static async Task<ChatOffer?> ForChatAsync(WaypointDb db, ProgressCalculator progress, Aspiration aspiration, string? intent, DateTime now,
        Func<IReadOnlyList<Milestone>, Task<int?>> requestedWeeks, CancellationToken ct)
    {
        if (intent is not (ChangeDatesIntent or MoveDateIntent)) return null;
        var today = DateOnly.FromDateTime(now);
        var snap = await progress.ComputeAsync(aspiration, today.AddDays(1), ct);
        if (snap.Current is null) return null;
        var capped = intent == ChangeDatesIntent;
        var existing = await db.PlanVersions.Where(p => p.AspirationId == aspiration.Id && p.Reason == Later && p.Status == PlanVersionStatus.Proposed).FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            // The user asking to go past Settle-for is not served by a capped proposal: it is replaced by an uncapped one.
            var pendingCapped = JsonNode.Parse(existing.ChangesJson ?? "{}")?["capped"]?.GetValue<bool>() ?? true;
            if (capped || !pendingCapped) return new ChatOffer(existing.Id, Later, "a proposal is already waiting in Plan review");
            existing.Status = PlanVersionStatus.Superseded;
        }
        var shifts = snap.NextProjection is { } projection ? Slip(snap.Milestones, projection.Middle, capped ? aspiration.SettleForDate : null) : [];
        var reason = Later;
        if (shifts.Count == 0)
        {
            // Nothing is projected to slip, but the user asked: their ask moves the dates, by as much as they said.
            var open = snap.Milestones.Where(m => m.State != "reached").OrderBy(m => m.Date).ToList();
            if (open.Count == 0) return new ChatOffer(null, Later, "every Milestone is reached, so there are no dates to move; say so kindly");
            var weeks = await requestedWeeks(open);
            if (weeks is not { } w || w == 0 || Math.Abs(w) > MaxRequestedWeeks)
                return new ChatOffer(null, Later, "nothing was proposed yet: ask them, in one short question, how much later or earlier they'd like the dates, in weeks or by a date");
            if (w < 0)
            {
                reason = Earlier;
                if (await PendingAsync(db, aspiration.Id, Earlier, ct)) return new ChatOffer(null, Earlier, "a proposal to bring dates earlier is already waiting in Plan review");
                shifts = Shift(open, w * 7, null, earliest: today.AddDays(1));
            }
            else
                shifts = Shift(open, w * 7, capped ? aspiration.SettleForDate : null, earliest: null);
            if (shifts.Count == 0)
                return new ChatOffer(null, reason, w > 0
                    ? "the dates are already at the Settle-for date, so nothing was proposed; say that moving past it is their call, with \"Move the date\" on the Settle-for card"
                    : "the dates can't come any earlier than tomorrow, so nothing was proposed; say so kindly");
        }
        var version = await ProposeAsync(db, aspiration, snap.Current, reason, shifts, today.AddDays(1), now, "", ct, capped);
        var brief = string.Join("; ", shifts.Select(s => $"{s.Title}: {s.Before:yyyy-MM-dd} to {s.After:yyyy-MM-dd}"));
        return new ChatOffer(version.Id, reason, brief);
    }

    /// <summary>Whether the Coach already offered to bring this Milestone's date earlier: once is all it gets.</summary>
    public static async Task<bool> OfferedEarlierAsync(WaypointDb db, Guid aspirationId, string milestoneRef, CancellationToken ct)
    {
        var offers = await db.PlanVersions.Where(p => p.AspirationId == aspirationId && p.Reason == Earlier && p.ChangesJson != null)
            .Select(p => p.ChangesJson!).ToListAsync(ct);
        return offers.Any(json => JsonNode.Parse(json)?["changes"]?.AsArray().Any(c => c?["ref"]?.GetValue<string>() == milestoneRef) == true);
    }

    public static Task<bool> PendingAsync(WaypointDb db, Guid aspirationId, string reason, CancellationToken ct) =>
        db.PlanVersions.AnyAsync(p => p.AspirationId == aspirationId && p.Reason == reason && p.Status == PlanVersionStatus.Proposed, ct);

    /// <summary>A proposed version that copies the current Plan with the Milestones on their new dates. Adds rows; the caller saves.</summary>
    public static async Task<PlanVersion> ProposeAsync(WaypointDb db, Aspiration aspiration, PlanVersion current, string reason, List<DateShift> shifts,
        DateOnly start, DateTime now, string rationale, CancellationToken ct, bool capped = true)
    {
        var version = new PlanVersion
        {
            Id = Guid.NewGuid(), UserId = aspiration.UserId, AspirationId = aspiration.Id, Status = PlanVersionStatus.Proposed,
            Rationale = rationale, Reason = reason, Ramp = current.Ramp, AssumptionsJson = current.AssumptionsJson,
            // Dates only: no new window, so nothing in it goes stale while the user thinks.
            TwoWeeksJson = "[]", StartDate = start, CreatedAt = now,
            ChangesJson = new JsonObject
            {
                ["capped"] = capped,
                ["changes"] = new JsonArray(shifts.Select(s => (JsonNode)new JsonObject
                {
                    ["code"] = reason, ["what"] = "milestone", ["ref"] = s.Ref, ["title"] = s.Title,
                    ["before"] = s.Before.ToString("yyyy-MM-dd"), ["after"] = s.After.ToString("yyyy-MM-dd"),
                }).ToArray()),
            }.ToJsonString(),
        };
        db.PlanVersions.Add(version);
        await CopyAsync(db, version, current.Id, shifts, ct);
        return version;
    }

    /// <summary>
    /// Inside the approval's transaction, once it has won: copies the Plan as it stands now (a weekly refresh may have been approved since the
    /// proposal was made) and puts the proposed dates on it again, re-applying the hard rules: a Later date stays at or before Settle-for
    /// (unless the user asked to move past it), an Earlier date stays at or after today and the Milestone before it.
    /// Returns false when the Plan moved on: a Milestone no longer sits on the date the proposal was made from.
    /// </summary>
    public static async Task<bool> RebaseAsync(WaypointDb db, PlanVersion version, DateOnly today, CancellationToken ct)
    {
        var current = await db.PlanVersions.FirstOrDefaultAsync(p => p.AspirationId == version.AspirationId && p.Id != version.Id && p.Status == PlanVersionStatus.Approved, ct);
        if (current is null) return false;
        var settle = (await db.Aspirations.FirstAsync(a => a.Id == version.AspirationId, ct)).SettleForDate;
        var root = JsonNode.Parse(version.ChangesJson ?? "{}");
        var capped = root?["capped"]?.GetValue<bool>() ?? true;
        var shifts = (root?["changes"]?.AsArray() ?? [])
            .Select(c => new DateShift(c!["ref"]!.GetValue<string>(), c["title"]!.GetValue<string>(),
                DateOnly.Parse(c["before"]!.GetValue<string>()), DateOnly.Parse(c["after"]!.GetValue<string>()))).ToList();
        var now = await db.Milestones.Where(m => m.PlanVersionId == current.Id).ToListAsync(ct);
        if (shifts.Any(s => !now.Any(m => m.Ref == s.Ref && m.State != "reached" && m.Date == s.Before))) return false;
        await db.Milestones.Where(m => m.PlanVersionId == version.Id).ExecuteDeleteAsync(ct);
        await db.Habits.Where(h => h.PlanVersionId == version.Id).ExecuteDeleteAsync(ct);
        await db.PlanTasks.Where(t => t.PlanVersionId == version.Id).ExecuteDeleteAsync(ct);
        await CopyAsync(db, version, current.Id, shifts, ct, version.Reason == Later && capped ? settle : null, version.Reason == Earlier ? today : null);
        await db.SaveChangesAsync(ct);
        return true;
    }

    private static DateOnly Max(DateOnly a, DateOnly b) => a > b ? a : b;

    private static async Task CopyAsync(WaypointDb db, PlanVersion version, Guid fromId, List<DateShift> shifts, CancellationToken ct, DateOnly? cap = null, DateOnly? floor = null)
    {
        var to = shifts.ToDictionary(s => s.Ref, s => s.After);
        var milestones = await db.Milestones.Where(m => m.PlanVersionId == fromId).OrderBy(m => m.Date).ToListAsync(ct);
        var newIds = new Dictionary<Guid, Guid>();
        var moved = new Dictionary<Guid, int>();
        var previous = DateOnly.MinValue;
        foreach (var m in milestones)
        {
            var date = m.State != "reached" && to.TryGetValue(m.Ref, out var d) ? d : m.Date;
            if (cap is { } c && date > m.Date && date > c) date = c < m.Date ? m.Date : c;
            if (floor is { } f && date < m.Date) date = Max(date, Max(f, previous)) > m.Date ? m.Date : Max(date, Max(f, previous));
            previous = date;
            var copy = new Milestone
            {
                Id = Guid.NewGuid(), UserId = m.UserId, PlanVersionId = version.Id, Ref = m.Ref, Date = date, Title = m.Title,
                ReachedWhen = m.ReachedWhen, Kind = m.Kind, State = m.State,
            };
            newIds[m.Id] = copy.Id;
            moved[m.Id] = date.DayNumber - m.Date.DayNumber;
            db.Milestones.Add(copy);
        }
        foreach (var h in await db.Habits.Where(h => h.PlanVersionId == fromId).ToListAsync(ct))
            db.Habits.Add(new Habit { Id = Guid.NewGuid(), UserId = h.UserId, PlanVersionId = version.Id, Title = h.Title, TimesPerWeek = h.TimesPerWeek, Minutes = h.Minutes, SlotIds = h.SlotIds });
        foreach (var t in await db.PlanTasks.Where(t => t.PlanVersionId == fromId).ToListAsync(ct))
            db.PlanTasks.Add(new PlanTask
            {
                Id = Guid.NewGuid(), UserId = t.UserId, PlanVersionId = version.Id, MilestoneId = newIds[t.MilestoneId], Title = t.Title, Minutes = t.Minutes,
                DueDate = t.DueDate?.AddDays(moved[t.MilestoneId]),
            });
    }
}
