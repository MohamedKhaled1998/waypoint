using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// A temporary Time budget cut: what the user told the Coach ("work is crazy this month") bends the Plan for a while.
/// The server decides how much and when it ends; the model only reads the message and phrases the proposal. A cut is a
/// proposed Plan version per Aspiration; approving it lowers the share and books its own end, when a new version restores the Plan.
/// </summary>
public static class BudgetCuts
{
    public const string CutReason = "time_budget_cut";
    public const string RestoreReason = "time_budget_restore";
    /// <summary>The share of the Time budget a cut takes away.</summary>
    public const double CutFraction = 0.3;
    /// <summary>A cut is for at least a few days and at most about four months.</summary>
    public const int MinDays = 2, MaxDays = 120;
    public const int MinSessionMinutes = 10;

    /// <summary>The last day of a cut, if it is a sensible one from <paramref name="today"/>.</summary>
    public static DateOnly? ValidEnd(string? until, DateOnly today) =>
        PlanRules.ParseDate(until) is { } d && d.DayNumber - today.DayNumber is >= MinDays and <= MaxDays ? d : null;

    public static bool IsCut(string reason) => reason == CutReason;

    /// <summary>On approval of a cut: lower the share and the Time budget, and book the end.</summary>
    public static async Task OnApprovedAsync(WaypointDb db, JobQueue jobs, TimeProvider clock, PlanVersion version, CancellationToken ct)
    {
        var delta = (version.ShareBeforeHours ?? 0) - (version.ShareAfterHours ?? 0);
        var aspiration = await db.Aspirations.FirstAsync(a => a.Id == version.AspirationId, ct);
        aspiration.ShareHoursPerWeek -= delta;
        var user = await db.Users.SingleAsync(ct);
        if (user.TimeBudgetHours is { } hours) user.TimeBudgetHours = hours - delta;
        var restoreAt = version.RevertOn!.Value.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var wait = restoreAt - clock.GetUtcNow().UtcDateTime;
        jobs.Add(JobKinds.BudgetRestore, version.UserId, new Dictionary<string, Guid> { ["planVersionId"] = version.Id }, version.AspirationId,
            delay: wait > TimeSpan.Zero ? wait : TimeSpan.Zero);
    }

    /// <summary>The chat card that opens Plan review for a proposed version, with its "Plan ready" push.</summary>
    public static ChatMessage ProposalCard(WaypointDb db, JobQueue jobs, Aspiration aspiration, PlanVersion version, DateTime now)
    {
        var card = new JsonObject { ["planVersionId"] = version.Id, ["aspirationId"] = aspiration.Id, ["shortName"] = aspiration.ShortName, ["hue"] = aspiration.Hue, ["reason"] = version.Reason };
        var message = new ChatMessage
        {
            Id = Guid.NewGuid(), UserId = aspiration.UserId, Role = "coach", Text = version.Rationale,
            AspirationId = aspiration.Id, Card = "proposal", CardJson = card.ToJsonString(), CreatedAt = now,
        };
        db.ChatMessages.Add(message);
        jobs.Add(JobKinds.PushMessage, aspiration.UserId, new Dictionary<string, Guid> { ["chatMessageId"] = message.Id }, aspiration.Id);
        return message;
    }
}

public sealed partial class WeeklyRefreshRunner
{
    /// <summary>One proposed cut per Aspiration with a Plan, from what the user said in a chat message (named by ID; its date is in the intent).</summary>
    public async Task ProposeCutAsync(Guid chatMessageId, CancellationToken ct)
    {
        var message = await db.ChatMessages.FirstOrDefaultAsync(m => m.Id == chatMessageId, ct);
        var now = clock.GetUtcNow().UtcDateTime;
        if (message?.IntentJson is null || BudgetCuts.ValidEnd(JsonNode.Parse(message.IntentJson)?["until"]?.GetValue<string>(), DateOnly.FromDateTime(now)) is not { } until) return;
        var (start, slots, planned, currents, pauses, used) = await PrepareAsync(now, respectPauses: false, only: null, ct);
        anchorHabitsInAll = 0;

        foreach (var aspiration in planned)
        {
            var current = currents.First(p => p.AspirationId == aspiration.Id);
            var version = await ProposeAsync(aspiration, current, slots, used, start, now, null, pauses, ct,
                reshape: habits => Scale(habits, 1 - BudgetCuts.CutFraction, BudgetCuts.CutReason, source: null), reason: BudgetCuts.CutReason,
                stamp: v =>
                {
                    v.RevertOn = until;
                    v.SourceVersionId = current.Id;
                    v.ShareBeforeHours = aspiration.ShareHoursPerWeek;
                    v.ShareAfterHours = Math.Round(aspiration.ShareHoursPerWeek * (1 - BudgetCuts.CutFraction), 2);
                },
                why: BudgetCuts.CutReason);
            BudgetCuts.ProposalCard(db, jobs, aspiration, version, now);
        }
        await db.SaveChangesAsync(ct);
        log.LogInformation("Time budget cut proposed for {Count} Aspirations", planned.Count);
    }

    /// <summary>The cut ended: the share and the Time budget come back, and the Plan goes back to what it was, as a new version that needs no tap.</summary>
    public async Task RestoreAsync(Guid cutVersionId, CancellationToken ct)
    {
        var cut = await db.PlanVersions.FirstOrDefaultAsync(p => p.Id == cutVersionId, ct);
        if (cut?.SourceVersionId is null) return;
        // A retry after the first save only finishes what is missing: the restore version may still wait for its approval.
        var earlier = await db.PlanVersions.FirstOrDefaultAsync(p => p.Reason == BudgetCuts.RestoreReason && p.SourceVersionId == cut.Id, ct);
        if (earlier is not null || cut.RevertOn is null)
        {
            if (earlier?.Status == PlanVersionStatus.Proposed) await Plans.Approve(earlier.Id, db, clock, jobs, ct);
            return;
        }
        var now = clock.GetUtcNow().UtcDateTime;
        var aspiration = await db.Aspirations.FirstOrDefaultAsync(a => a.Id == cut.AspirationId, ct);
        if (aspiration is null) return;

        var (start, slots, planned, currents, pauses, used) = await PrepareAsync(now, respectPauses: false, only: aspiration.Id, ct);
        anchorHabitsInAll = 0;
        var sourceMinutes = (await db.Habits.Where(h => h.PlanVersionId == cut.SourceVersionId).ToListAsync(ct)).GroupBy(h => h.Title).ToDictionary(g => g.Key, g => g.First().Minutes);
        PlanVersion? restored = null;
        if (planned.Count == 1)
        {
            restored = await ProposeAsync(aspiration, currents.First(p => p.AspirationId == aspiration.Id), slots, used, start, now, null, pauses, ct,
                reshape: habits => Scale(habits, 1, BudgetCuts.RestoreReason, sourceMinutes), reason: BudgetCuts.RestoreReason,
                stamp: v => v.SourceVersionId = cut.Id, why: BudgetCuts.RestoreReason);
        }
        // After the model has phrased it. The restore version and the cut's end mark (RevertOn cleared) are saved with the giving-back,
        // so a retry never restores twice.
        cut.RevertOn = null;
        var delta = (cut.ShareBeforeHours ?? 0) - (cut.ShareAfterHours ?? 0);
        aspiration.ShareHoursPerWeek += delta;
        var user = await db.Users.SingleAsync(ct);
        if (user.TimeBudgetHours is { } hours) user.TimeBudgetHours = hours + delta;
        await db.SaveChangesAsync(ct);
        if (restored is not null) await Plans.Approve(restored.Id, db, clock, jobs, ct);
        log.LogInformation("Time budget cut {PlanVersionId} ended", cut.Id);
    }

    /// <summary>
    /// A cut shortens every Slot habit's sessions by the cut fraction (in steps of five minutes, never under ten); a restore brings each
    /// habit back to the minutes it had in <paramref name="source"/>. Anchor habits are outside the Time budget and stay as they are.
    /// </summary>
    private static List<JsonObject> Scale(List<H> habits, double factor, string code, Dictionary<string, int>? source)
    {
        var changes = new List<JsonObject>();
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            if (h.IsAnchor) continue;
            var minutes = source is null
                ? Math.Max(BudgetCuts.MinSessionMinutes, (int)(Math.Round(h.Minutes * factor / WeeklyRefreshes.MinutesStep) * WeeklyRefreshes.MinutesStep))
                : source.GetValueOrDefault(h.Title, h.Minutes);
            if (minutes >= h.Minutes && source is null) continue;
            if (minutes == h.Minutes) continue;
            habits[i] = h with { Minutes = minutes };
            changes.Add(Change(code, h.Title, "minutes", h.Minutes, minutes));
        }
        return changes;
    }
}
