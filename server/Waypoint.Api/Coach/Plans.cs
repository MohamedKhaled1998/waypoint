using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public static class Plans
{
    public static void MapPlans(this WebApplication app)
    {
        var g = app.MapGroup("/").RequireAuthorization();

        // Per Aspiration: is a Plan being drafted, which proposal waits, which version is current.
        g.MapGet("/plans", async (WaypointDb db, CancellationToken ct) =>
        {
            var aspirations = await db.Aspirations.Where(a => a.Status != "closed").OrderBy(a => a.CreatedAt).ToListAsync(ct);
            var versions = await db.PlanVersions.Where(p => p.Status == PlanVersionStatus.Proposed || p.Status == PlanVersionStatus.Approved)
                .Select(p => new { p.Id, p.AspirationId, p.Status, p.ApprovedAt }).ToListAsync(ct);
            var jobs = await db.Jobs.Where(j => j.Kind == JobKinds.Plan && j.AspirationId != null)
                .Select(j => new { j.AspirationId, j.State, j.CreatedAt }).ToListAsync(ct);
            return Results.Ok(aspirations.Select(a => new
            {
                AspirationId = a.Id, a.ShortName, a.Hue,
                Drafting = jobs.Any(j => j.AspirationId == a.Id && j.State == JobState.Pending),
                // The Coach gave up drafting: the phone says the Plan is late and offers a retry. No Coach-voice line.
                DraftLate = !jobs.Any(j => j.AspirationId == a.Id && j.State == JobState.Pending)
                    && jobs.Where(j => j.AspirationId == a.Id).OrderByDescending(j => j.CreatedAt).FirstOrDefault()?.State == JobState.Failed,
                PendingVersionId = versions.FirstOrDefault(v => v.AspirationId == a.Id && v.Status == PlanVersionStatus.Proposed)?.Id,
                CurrentVersionId = versions.FirstOrDefault(v => v.AspirationId == a.Id && v.Status == PlanVersionStatus.Approved)?.Id,
                // The Coach thread's proposal card says when the Plan in force was approved.
                CurrentApprovedAt = versions.FirstOrDefault(v => v.AspirationId == a.Id && v.Status == PlanVersionStatus.Approved)?.ApprovedAt,
            }));
        });

        g.MapGet("/plan-versions/{id:guid}", async (Guid id, WaypointDb db, CancellationToken ct) =>
            await db.PlanVersions.FirstOrDefaultAsync(p => p.Id == id, ct) is { } v
                ? Results.Ok(await ViewAsync(db, v, ct)) : Results.NotFound());

        g.MapPost("/plan-versions/{id:guid}/approve", Approve);

        // After the Coach gave up on a draft: ask for a new Plan. One pending job at a time.
        g.MapPost("/aspirations/{id:guid}/plan-retry", async (Guid id, WaypointDb db, CurrentUser me, JobQueue jobs, CancellationToken ct) =>
        {
            if (!await db.Aspirations.AnyAsync(a => a.Id == id && a.Status != "closed", ct)) return Results.NotFound();
            if (!await db.Jobs.AnyAsync(j => j.Kind == JobKinds.Plan && j.AspirationId == id && j.State == JobState.Pending, ct))
                await jobs.EnqueueAsync(JobKinds.Plan, me.Id!.Value, new Dictionary<string, Guid> { ["aspirationId"] = id }, id, ct: ct);
            return Results.Accepted();
        });

        g.MapPut("/scheduled-actions/{id:guid}/reminder", SetReminder);
        g.MapPut("/habits/{id:guid}/reminder", SetHabitReminder);
        g.MapGet("/scheduled-actions", async (WaypointDb db, CancellationToken ct) =>
        {
            var slots = await db.Slots.ToDictionaryAsync(s => s.Id, ct);
            var anchors = await db.Anchors.ToDictionaryAsync(x => x.Id, ct);
            var checkIns = await db.CheckIns.ToDictionaryAsync(c => c.ScheduledActionId, ct);
            var onSkip = await db.Aspirations.ToDictionaryAsync(a => a.Id, a => a.OnSkip, ct);
            var pauses = await db.Pauses.ToListAsync(ct);
            return Results.Ok((await db.ScheduledActions.OrderBy(a => a.Date).ThenBy(a => a.Title).ToListAsync(ct))
                .Select(a => ActionView(a, a.SlotId is { } sid ? slots.GetValueOrDefault(sid) : null, checkIns.GetValueOrDefault(a.Id),
                    onSkip: onSkip.GetValueOrDefault(a.AspirationId), anchor: a.AnchorId is { } aid ? anchors.GetValueOrDefault(aid) : null,
                    setAside: Pauses.IsSetAside(pauses, a, checkIns.ContainsKey(a.Id)))));
        });
    }

    /// <summary>
    /// The Aspiration's reaction to a skip rides along so the phone can word its Follow-up in that tone. An Anchor Action sits at its
    /// Anchor's usual time ("SlotStart") with no Slot, so the phone's timeline and reminders treat it like any other.
    /// </summary>
    public static object ActionView(ScheduledAction a, Slot? slot, CheckIn? checkIn, string? onSkip = null, Anchor? anchor = null, bool setAside = false) => new
    {
        a.Id, a.AspirationId, a.PlanVersionId, a.Date, SlotId = a.SlotId, Slot = slot?.Label, SlotStart = slot?.Start ?? anchor?.Time, SlotEnd = slot?.End,
        a.Title, a.Minutes, Kind = a.HabitId is not null ? "habit" : a.TaskId is not null ? "task" : "catch_up", a.HabitId, a.TargetMinutes, a.AtRisk,
        Anchor = anchor is null ? null : new { anchor.Id, anchor.Words, anchor.Time },
        ReminderKind = a.ReminderKind, ReminderLeadMinutes = a.ReminderLeadMinutes, ReminderText = a.ReminderText, ReminderOn = a.ReminderOn, OnSkip = onSkip,
        CheckIn = checkIn is null ? null : CheckIns.View(checkIn),
        SetAside = setAside,
        Guide = a.GuideJson is null ? null : JsonNode.Parse(a.GuideJson),
    };

    internal static async Task<IResult> Approve(Guid id, WaypointDb db, TimeProvider clock, JobQueue jobs, CancellationToken ct)
    {
        var version = await db.PlanVersions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (version is null) return Results.NotFound();
        if (version.Status == PlanVersionStatus.Approved) return Results.Ok(await ViewAsync(db, version, ct));
        if (version.Status != PlanVersionStatus.Proposed) return await StaleAsync(db, version, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        if (today > version.StartDate && !DateProposals.IsDates(version.Reason))
        {
            // The two weeks it planned have started, so its dates no longer fit: replace it with a fresh draft.
            var replaced = await db.PlanVersions.Where(p => p.Id == id && p.Status == PlanVersionStatus.Proposed)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Replaced), ct);
            if (replaced == 0) return await StaleAsync(db, await RefreshAsync(db, version, ct), ct);
            // A stale refresh just waits for the next one; only a first Plan is drafted again.
            if (version.Reason == "first_plan" && !await db.Jobs.AnyAsync(j => j.Kind == JobKinds.Plan && j.AspirationId == version.AspirationId && j.State == JobState.Pending, ct))
                await jobs.EnqueueAsync(JobKinds.Plan, version.UserId, new Dictionary<string, Guid> { ["aspirationId"] = version.AspirationId }, version.AspirationId, ct: ct);
            return Results.Conflict(new { error = "stale", reason = "window_started", newer = (PlanVersionView?)null });
        }

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Only one approve wins: the status flips from Proposed in a single conditional update.
        var won = await db.PlanVersions.Where(p => p.Id == id && p.Status == PlanVersionStatus.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Approved).SetProperty(p => p.ApprovedAt, now), ct);
        if (won == 0)
        {
            await tx.RollbackAsync(ct);
            version = await RefreshAsync(db, version, ct);
            return version.Status == PlanVersionStatus.Approved ? Results.Ok(await ViewAsync(db, version, ct)) : await StaleAsync(db, version, ct);
        }
        // A dates proposal is made on the Plan of its day; copy the Plan as it is now, in case a refresh was approved since.
        // Only the winner gets here, and the tx undoes the win if the dates it was made from have moved.
        if (DateProposals.IsDates(version.Reason) && !await DateProposals.RebaseAsync(db, version, today, ct))
        {
            await tx.RollbackAsync(ct);
            await db.PlanVersions.Where(p => p.Id == id && p.Status == PlanVersionStatus.Proposed)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Replaced), ct);
            return await StaleAsync(db, await RefreshAsync(db, version, ct), ct);
        }
        var supersededIds = await db.PlanVersions.Where(p => p.AspirationId == version.AspirationId && p.Id != id && p.Status == PlanVersionStatus.Approved)
            .Select(p => p.Id).ToListAsync(ct);
        await db.PlanVersions.Where(p => supersededIds.Contains(p.Id))
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Superseded), ct);
        foreach (var old in supersededIds) db.Record("plan_version", version.UserId, old);
        version = await RefreshAsync(db, version, ct);
        version.Number = (await db.PlanVersions.Where(p => p.AspirationId == version.AspirationId).MaxAsync(p => p.Number, ct) ?? 0) + 1;

        // A refresh rolls the window: what it planned from its start on replaces what was planned, except Actions already checked in.
        var kept = new HashSet<(DateOnly, string)>();
        if (RollsWindow(version.Reason))
        {
            var future = await db.ScheduledActions.Where(a => a.AspirationId == version.AspirationId && a.Date >= version.StartDate && a.PlanVersionId != id && a.TestForMilestoneRef == null).ToListAsync(ct);
            var checkedIn = await db.CheckIns.Where(c => future.Select(a => a.Id).Contains(c.ScheduledActionId)).Select(c => c.ScheduledActionId).ToListAsync(ct);
            foreach (var a in future)
                if (checkedIn.Contains(a.Id)) kept.Add((a.Date, a.Title));
                else db.ScheduledActions.Remove(a);
        }
        var versionHabits = await db.Habits.Where(h => h.PlanVersionId == id).ToListAsync(ct);
        var targets = versionHabits.Where(h => h.TargetMinutes != null).ToDictionary(h => h.Id, h => h.TargetMinutes);
        var offHabits = versionHabits.Where(h => !h.ReminderOn).Select(h => h.Id).ToHashSet();
        var habitGuides = await db.Habits.Where(h => h.PlanVersionId == id && h.GuideJson != null).ToDictionaryAsync(h => h.Id, h => h.GuideJson, ct);
        var taskGuides = await db.PlanTasks.Where(t => t.PlanVersionId == id && t.GuideJson != null).ToDictionaryAsync(t => t.Id, t => t.GuideJson, ct);
        var added = new List<ScheduledAction>();
        foreach (var a in JsonNode.Parse(version.TwoWeeksJson)!.AsArray().Select(a => a!.AsObject()))
        {
            if (kept.Contains((PlanRules.ParseDate(HandOff.Str(a, "date"))!.Value, HandOff.Str(a, "action")!))) continue;
            // Linked by the IDs the draft stored, never by title or label.
            added.Add(new ScheduledAction
            {
                Id = Guid.NewGuid(), UserId = version.UserId, AspirationId = version.AspirationId, PlanVersionId = id,
                HabitId = Ref(a, "habitId"), TaskId = Ref(a, "taskId"), SlotId = Ref(a, "slotId"), AnchorId = Ref(a, "anchorId"),
                TargetMinutes = Ref(a, "habitId") is { } hid ? targets.GetValueOrDefault(hid) : null,
                ReminderOn = Ref(a, "habitId") is { } hid2 ? !offHabits.Contains(hid2) : true,
                Date = PlanRules.ParseDate(HandOff.Str(a, "date"))!.Value, Title = HandOff.Str(a, "action")!,
                Minutes = (int)HandOff.Num(a, "minutes")!.Value,
                ReminderKind = HandOff.Str(a, "reminder") == ReminderKinds.GetReady ? ReminderKinds.GetReady : ReminderKinds.Now,
                ReminderLeadMinutes = HandOff.Str(a, "reminder") == ReminderKinds.GetReady ? LeadFrom(a) : null,
                GuideJson = Ref(a, "habitId") is { } habitId && habitGuides.TryGetValue(habitId, out var hg) ? hg
                    : Ref(a, "taskId") is { } tid && taskGuides.TryGetValue(tid, out var tg) ? tg : null,
            });
        }
        // Referral cards for the Guides this version carries: shown once approved, and only if the referral is new.
        var approvedGuides = (await db.Habits.Where(h => h.PlanVersionId == id && h.GuideJson != null).Select(h => new { h.Title, h.GuideJson }).ToListAsync(ct))
            .Concat(await db.PlanTasks.Where(t => t.PlanVersionId == id && t.GuideJson != null).Select(t => new { t.Title, t.GuideJson }).ToListAsync(ct))
            .Select(g => new KeyValuePair<string, JsonObject>(g.Title, JsonNode.Parse(g.GuideJson!)!.AsObject()));
        await GuideReferrals.PostIfNewAsync(db, clock, version.AspirationId, approvedGuides, ct);
        db.ScheduledActions.AddRange(added);
        // Several Aspirations share the Slots: places are settled across all of them, never ranked by the user.
        await Scheduling.ResolveAsync(db, version.AspirationId, version.StartDate, added, ct);
        // The reminder text is written after approval, so a slow or failing model never holds up the Plan.
        jobs.Add(JobKinds.ReminderText, version.UserId, new Dictionary<string, Guid> { ["planVersionId"] = id }, version.AspirationId);
        if (BudgetCuts.IsCut(version.Reason)) await BudgetCuts.OnApprovedAsync(db, jobs, clock, version, ct);
        if (version.Reason == Settings.BudgetChangeReason) await BudgetChangeApprovedAsync(db, version, ct);
        // Every approved version re-books its upcoming Milestones' dates (and ability test tasks); jobs for the Milestones it replaces go.
        var milestones = await db.Milestones.Where(m => m.PlanVersionId == id).OrderBy(m => m.Date).ToListAsync(ct);
        var milestoneService = new MilestoneService(db, jobs, clock);
        var stale = await db.Jobs.Where(j => j.AspirationId == version.AspirationId && j.State == JobState.Pending
            && (j.Kind == JobKinds.MilestoneDue || j.Kind == JobKinds.MilestoneTestTask)).ToListAsync(ct);
        db.Jobs.RemoveRange(stale);
        foreach (var m in milestones.Where(m => m.State == MilestoneService.Upcoming)) milestoneService.Schedule(m, version.AspirationId);
        if (version.Reason == "first_plan")
            await Silence.TouchAsync(db, jobs, clock, ct); // the user just approved their first Plan: silence counts from here
        if (version.Reason == "first_plan")
        {
            // The first approved Plan books the first Weekly refresh, after the Plan's first day, and a Measure is read at Plan acceptance.
            if (milestones.FirstOrDefault(m => m.MeasureStart is not null) is { } measured)
                Measures.Add(db, clock, version.UserId, version.AspirationId, measured.MeasureStart!.Value, today, MeasureSources.PlanAccepted, null, null);
            await WeeklyRefreshes.ScheduleNextAsync(db, jobs, clock, version.StartDate.ToDateTime(new TimeOnly(23, 59, 59), DateTimeKind.Utc), ct);
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(await ViewAsync(db, version, ct));
    }

    /// <summary>An approved Time budget change moves the Aspiration's share, and the budget to what the user asked for.</summary>
    private static async Task BudgetChangeApprovedAsync(WaypointDb db, PlanVersion version, CancellationToken ct)
    {
        var aspiration = await db.Aspirations.FirstAsync(a => a.Id == version.AspirationId, ct);
        if (version.ShareAfterHours is { } share) aspiration.ShareHoursPerWeek = share;
        var user = await db.Users.SingleAsync(ct);
        if (user.RequestedTimeBudgetHours is { } hours)
        {
            if (user.TimeBudgetHours is > 0 and var old && old != hours) await Settings.ScaleUnplannedSharesAsync(db, hours / old, ct);
            user.TimeBudgetHours = hours;
        }
        // The request is done once no other Aspiration's proposal of it still waits for a tap.
        if (!await db.PlanVersions.AnyAsync(p => p.Reason == Settings.BudgetChangeReason && p.Status == PlanVersionStatus.Proposed && p.Id != version.Id, ct))
            user.RequestedTimeBudgetHours = null;
    }

    /// <summary>
    /// Every version after the first Plan lays out its window anew, from its start on, keeping only Actions already checked in.
    /// Date proposals carry no window of their own, so they leave the scheduled Actions alone.
    /// </summary>
    private static bool RollsWindow(string reason) => reason is not ("first_plan" or AspirationLifecycle.MaintenanceReason or DateProposals.Later or DateProposals.Earlier);

    private static int LeadFrom(JsonObject a)
    {
        var lead = HandOff.Num(a, "lead_minutes");
        return lead is > 0 and <= ReminderKinds.MaxLeadMinutes ? (int)lead.Value : ReminderKinds.DefaultLeadMinutes;
    }

    public sealed record ReminderRequest(string? Kind, int? LeadMinutes, bool? On);

    /// <summary>The user edits one Action's reminder: its kind and lead time, or switches it off.</summary>
    private static async Task<IResult> SetReminder(Guid id, ReminderRequest req, WaypointDb db, CancellationToken ct)
    {
        if (req.On is null || req.Kind is not (ReminderKinds.Now or ReminderKinds.GetReady)) return Results.BadRequest();
        if (req.Kind == ReminderKinds.GetReady && req.LeadMinutes is not (> 0 and <= ReminderKinds.MaxLeadMinutes)) return Results.BadRequest();
        var action = await db.ScheduledActions.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (action is null) return Results.NotFound();
        action.ReminderKind = req.Kind;
        action.ReminderLeadMinutes = req.Kind == ReminderKinds.GetReady ? req.LeadMinutes : null;
        action.ReminderOn = req.On.Value;
        await db.SaveChangesAsync(ct);
        var slot = await db.Slots.FirstOrDefaultAsync(s => s.Id == action.SlotId, ct);
        var anchor = await db.Anchors.FirstOrDefaultAsync(x => x.Id == action.AnchorId, ct);
        var checkIn = await db.CheckIns.FirstOrDefaultAsync(c => c.ScheduledActionId == id, ct);
        return Results.Ok(ActionView(action, slot, checkIn, anchor: anchor, setAside: await Pauses.IsSetAsideAsync(db, action, checkIn is not null, ct)));
    }

    public sealed record HabitReminderRequest(bool? On, bool? FromOffer);

    /// <summary>
    /// The user switches a habit's reminder on or off: the habit in the current Plan (and any pending proposal) and its coming Actions.
    /// Taking the Coach's offer to stop an Anchor habit's reminder marks it, so a refresh may bring it back if the habit slips.
    /// </summary>
    private static async Task<IResult> SetHabitReminder(Guid id, HabitReminderRequest req, WaypointDb db, TimeProvider clock, CancellationToken ct)
    {
        if (req.On is null) return Results.BadRequest();
        var habit = await db.Habits.FirstOrDefaultAsync(h => h.Id == id, ct);
        if (habit is null) return Results.NotFound();
        var version = await db.PlanVersions.FirstAsync(p => p.Id == habit.PlanVersionId, ct);
        var live = await db.PlanVersions.Where(p => p.AspirationId == version.AspirationId
            && (p.Status == PlanVersionStatus.Approved || p.Status == PlanVersionStatus.Proposed)).Select(p => p.Id).ToListAsync(ct);
        foreach (var h in await db.Habits.Where(h => live.Contains(h.PlanVersionId) && h.Title == habit.Title).ToListAsync(ct))
        {
            h.ReminderOn = req.On.Value;
            h.StoppedByOffer = !req.On.Value && req.FromOffer == true && h.AnchorId is not null;
            if (req.FromOffer == true) h.StopOffered = true;
        }
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        foreach (var a in await db.ScheduledActions.Where(a => a.AspirationId == version.AspirationId && a.Title == habit.Title && a.Date >= today).ToListAsync(ct))
            a.ReminderOn = req.On.Value;
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }

    /// <summary>An ID the draft stored, whether it was written as a Guid or as text.</summary>
    private static Guid? Ref(JsonObject a, string name) =>
        a[name] is JsonValue v && (v.TryGetValue<Guid>(out var g) || (v.TryGetValue<string>(out var s) && Guid.TryParse(s, out g))) ? g : null;

    private static async Task<PlanVersion> RefreshAsync(WaypointDb db, PlanVersion v, CancellationToken ct)
    {
        await db.Entry(v).ReloadAsync(ct);
        return v;
    }

    /// <summary>The version was replaced or superseded: reject, and show the newer one.</summary>
    private static async Task<IResult> StaleAsync(WaypointDb db, PlanVersion version, CancellationToken ct)
    {
        var newer = await db.PlanVersions.Where(p => p.AspirationId == version.AspirationId
                && (p.Status == PlanVersionStatus.Proposed || p.Status == PlanVersionStatus.Approved))
            .OrderByDescending(p => p.CreatedAt).FirstOrDefaultAsync(ct);
        return Results.Conflict(new { error = "stale", reason = "replaced", newer = newer is null ? null : await ViewAsync(db, newer, ct) });
    }

    public sealed record MeasureView(string Name, string? Unit, double? Start, double? HopedFor);
    public sealed record MilestoneView(Guid Id, DateOnly Date, string Title, string ReachedWhen, string Kind, string State, MeasureView? Measure = null);
    public sealed record HabitView(Guid Id, string Title, int TimesPerWeek, int Minutes, IEnumerable<string?> Slots,
        object? Anchor = null, int? TargetMinutes = null, string? PrepFor = null, bool ReminderOn = true, JsonNode? Guide = null);
    public sealed record TaskView(Guid Id, string Title, int Minutes, DateOnly? Due, string? Milestone, JsonNode? Guide = null);
    public sealed record PlanVersionView(Guid Id, Guid AspirationId, string ShortName, string Hue, int? Number, string Status, string Reason,
        string Rationale, string Ramp, JsonNode? Assumptions, DateOnly StartDate, IEnumerable<MilestoneView> Milestones,
        IEnumerable<HabitView> Habits, IEnumerable<TaskView> Tasks, JsonNode? TwoWeeks, Guid? RefreshId, JsonNode? Changes, bool AutoApplied, int? ReplacesNumber, JsonNode? GuideLog = null);

    private static async Task<PlanVersionView> ViewAsync(WaypointDb db, PlanVersion v, CancellationToken ct)
    {
        var aspiration = await db.Aspirations.FirstAsync(a => a.Id == v.AspirationId, ct);
        var slots = await db.Slots.ToDictionaryAsync(s => s.Id, s => s.Label, ct);
        var milestones = await db.Milestones.Where(m => m.PlanVersionId == v.Id).OrderBy(m => m.Date).ToListAsync(ct);
        var habits = await db.Habits.Where(h => h.PlanVersionId == v.Id).ToListAsync(ct);
        var anchors = await db.Anchors.ToDictionaryAsync(x => x.Id, ct);
        var tasks = await db.PlanTasks.Where(t => t.PlanVersionId == v.Id).OrderBy(t => t.DueDate).ToListAsync(ct);
        return new PlanVersionView(v.Id, v.AspirationId, aspiration.ShortName, aspiration.Hue, v.Number, v.Status, v.Reason, v.Rationale, v.Ramp,
            JsonNode.Parse(v.AssumptionsJson), v.StartDate,
            milestones.Select(m => new MilestoneView(m.Id, m.Date, m.Title, m.ReachedWhen, m.Kind, m.State, m.HasMeasure ? new MeasureView(m.MeasureName!, m.MeasureUnit, m.MeasureStart, m.MeasureHopedFor) : null)).ToList(),
            habits.Select(h => new HabitView(h.Id, h.Title, h.TimesPerWeek, h.Minutes,
                h.SlotIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => slots.GetValueOrDefault(Guid.Parse(s))).ToList(),
                h.AnchorId is { } aid && anchors.TryGetValue(aid, out var an) ? new { an.Id, an.Words, an.Time, Days = an.Days.Split(',', StringSplitOptions.RemoveEmptyEntries) } : null,
                h.TargetMinutes, h.PrepFor, h.ReminderOn, h.GuideJson is null ? null : JsonNode.Parse(h.GuideJson))).ToList(),
            tasks.Select(t => new TaskView(t.Id, t.Title, t.Minutes, t.DueDate, milestones.FirstOrDefault(m => m.Id == t.MilestoneId)?.Title, t.GuideJson is null ? null : JsonNode.Parse(t.GuideJson))).ToList(),
            JsonNode.Parse(v.TwoWeeksJson), v.RefreshId, v.ChangesJson is null ? null : JsonNode.Parse(v.ChangesJson), v.AutoApplied,
            // What a proposal would replace: that version stays in the Plan's history.
            v.Status == PlanVersionStatus.Proposed
                ? await db.PlanVersions.Where(p => p.AspirationId == v.AspirationId && p.Status == PlanVersionStatus.Approved).Select(p => p.Number).FirstOrDefaultAsync(ct)
                : null,
            v.GuideLogJson is null ? null : JsonNode.Parse(v.GuideLogJson));
    }
}
