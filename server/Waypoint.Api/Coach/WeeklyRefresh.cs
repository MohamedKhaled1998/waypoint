using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;
using Waypoint.Api.Push;

namespace Waypoint.Api.Coach;

public static class RefreshPrompt
{
    public const string System = """
        You are the Coach in Waypoint, a personal coaching app. It is the Weekly refresh: the user's next week of one
        Aspiration is already decided by the app. You only put it into words. The first message is JSON with the
        Aspiration's short name, whether the last two weeks were mostly done, and "changes": a list of codes the app
        made (empty when nothing changed). Never change, add or question a decision, and never invent numbers.

        Voice: warm, plain, brief, forgiving, second person, no emojis, no guilt, no streaks.

        Reply with exactly one fenced json block and nothing else:

        ```json
        { "rationale": "two or three sentences on next week and why", "reasons": { "<change code>": "one short sentence" } }
        ```
        Give a reason for every code in "changes".

        When the brief carries "why", the week was not changed by a refresh: "time_budget_change" means the user changed their Time budget in Settings and the app proposes a Plan that fits it;
        "time_budget_cut" means the user told you
        work is heavy and the app proposes a temporary smaller Time budget until a date; "time_budget_restore" means
        that cut has ended and the Plan goes back to what it was. Say that, not a refresh.
        """;
}

/// <summary>
/// The Weekly refresh: when it runs, how the last two weeks are measured, how the Plan ramps and what the next
/// two-week window looks like. All decided here in code; the Coach model only phrases the result.
/// </summary>
public static class WeeklyRefreshes
{
    public static readonly TimeSpan AutoApplyAfter = TimeSpan.FromDays(1);
    /// <summary>A week is mostly done at 80% of its planned minutes.</summary>
    public const double MostlyDone = 0.8;
    public const int MinutesStep = 5;
    public const string Reason = "weekly_refresh";

    /// <summary>The first run of the user's day and time strictly after <paramref name="notBefore"/>.</summary>
    public static DateTime NextRun(User user, DateTime notBefore)
    {
        var time = TimeOnly.TryParseExact(user.RefreshTime, "HH:mm", out var t) ? t : new TimeOnly(18, 0);
        var day = DateOnly.FromDateTime(notBefore);
        for (var i = 0; i < 8; i++, day = day.AddDays(1))
        {
            if (day.DayOfWeek != user.RefreshDay) continue;
            var at = day.ToDateTime(time, DateTimeKind.Utc);
            if (at > notBefore) return at;
        }
        throw new InvalidOperationException("no_next_refresh");
    }

    /// <summary>Books the user's next refresh, once: nothing happens while another is already scheduled (not counting the one just finishing).</summary>
    public static async Task ScheduleNextAsync(WaypointDb db, JobQueue jobs, TimeProvider clock, DateTime notBefore, CancellationToken ct, Guid? finishing = null)
    {
        if (await db.WeeklyRefreshes.AnyAsync(r => r.Status == RefreshStatus.Scheduled && r.Id != finishing, ct)) return;
        var user = await db.Users.SingleAsync(ct);
        var refresh = new WeeklyRefresh { Id = Guid.NewGuid(), UserId = user.Id, RunAt = NextRun(user, notBefore), Status = RefreshStatus.Scheduled };
        db.WeeklyRefreshes.Add(refresh);
        jobs.Add(JobKinds.WeeklyRefresh, user.Id, new Dictionary<string, Guid> { ["refreshId"] = refresh.Id },
            delay: refresh.RunAt - clock.GetUtcNow().UtcDateTime);
    }

    public static void MapWeeklyRefreshes(this WebApplication app)
    {
        var g = app.MapGroup("/weekly-refresh").RequireAuthorization();

        // The latest refresh: the unreported Actions first, then what the Coach proposes.
        g.MapGet("/", async (WaypointDb db, CancellationToken ct) =>
            await db.WeeklyRefreshes.Where(r => r.Status != RefreshStatus.Scheduled).OrderByDescending(r => r.OpenedAt).FirstOrDefaultAsync(ct) is { } r
                ? Results.Ok(await ViewAsync(db, r, ct)) : Results.NotFound());

        // "Looks good": approves every proposal of the refresh.
        g.MapPost("/{id:guid}/accept", async (Guid id, WaypointDb db, TimeProvider clock, JobQueue jobs, CancellationToken ct) =>
        {
            var r = await db.WeeklyRefreshes.FirstOrDefaultAsync(x => x.Id == id && x.Status != RefreshStatus.Scheduled, ct);
            if (r is null) return Results.NotFound();
            if (r.Status == RefreshStatus.Open)
            {
                await ApproveAllAsync(db, clock, jobs, r, autoApplied: false, ct);
                r.Status = RefreshStatus.Accepted;
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(await ViewAsync(db, r, ct));
        });
    }

    internal static async Task ApproveAllAsync(WaypointDb db, TimeProvider clock, JobQueue jobs, WeeklyRefresh refresh, bool autoApplied, CancellationToken ct)
    {
        var ids = await db.PlanVersions.Where(p => p.RefreshId == refresh.Id && p.Status == PlanVersionStatus.Proposed)
            .OrderBy(p => p.CreatedAt).Select(p => p.Id).ToListAsync(ct);
        foreach (var id in ids)
        {
            await Plans.Approve(id, db, clock, jobs, ct);
            if (autoApplied) await db.PlanVersions.Where(p => p.Id == id && p.Status == PlanVersionStatus.Approved)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.AutoApplied, true), ct);
        }
    }

    private static async Task<object> ViewAsync(WaypointDb db, WeeklyRefresh r, CancellationToken ct)
    {
        var opened = DateOnly.FromDateTime(r.OpenedAt ?? r.RunAt);
        var from = opened.AddDays(-6);
        var slots = await db.Slots.ToDictionaryAsync(s => s.Id, ct);
        var pauses = await db.Pauses.ToListAsync(ct);
        // Set-aside Actions count neither as done nor missed, so they are not asked about.
        var anchors = await db.Anchors.ToDictionaryAsync(x => x.Id, ct);
        var unreported = (await db.ScheduledActions.Where(a => a.Date >= from && a.Date <= opened
                && !db.CheckIns.Any(c => c.ScheduledActionId == a.Id))
            .OrderBy(a => a.Date).ThenBy(a => a.Title).ToListAsync(ct)).Where(a => !Pauses.IsSetAside(pauses, a, false)).ToList();
        var proposals = await db.PlanVersions.Where(p => p.RefreshId == r.Id).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var names = await db.Aspirations.ToDictionaryAsync(a => a.Id, ct);
        // Property order is the order the phone shows them: the "did it" list comes first.
        return new
        {
            Unreported = unreported.Select(a => Plans.ActionView(a, a.SlotId is { } sid ? slots.GetValueOrDefault(sid) : null, null, a.AnchorId is { } aid ? anchors.GetValueOrDefault(aid) : null)),
            r.Id, r.Status, r.OpenedAt, r.AutoApplyAt,
            Proposals = proposals.Select(p => new
            {
                VersionId = p.Id, p.AspirationId, ShortName = names[p.AspirationId].ShortName, Hue = names[p.AspirationId].Hue, p.Status, p.AutoApplied,
            }),
        };
    }
}

/// <summary>The refresh job: measures, ramps, lays out the next two weeks and stores one proposal per Aspiration.</summary>
public sealed partial class WeeklyRefreshRunner(WaypointDb db, JobQueue jobs, ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock,
    ILogger<WeeklyRefreshRunner> log)
{
    /// <summary>A habit as the refresh works on it. Anchor habits have no Slots and no minutes of their own: <c>Old</c> carries the rest.</summary>
    private delegate List<JsonObject> Reshape(List<H> habits);

    private sealed record H(Guid OldId, string Title, int Times, int Minutes, List<Guid> SlotIds, Habit Old)
    {
        public bool IsAnchor => Old.AnchorId is not null;
    }

    private int anchorHabitsInAll;

    /// <summary>A refresh falls a day or two short of a whole number of weeks, so "about two weeks" is 12 days.</summary>
    public const int AnchorHeldDays = 12;
    /// <summary>About three weeks (21 days) on target before the Coach offers to stop an Anchor habit's reminder.</summary>
    public const int ReminderOfferDays = 21;

    public async Task RunAsync(Guid refreshId, CancellationToken ct)
    {
        var refresh = await db.WeeklyRefreshes.FirstOrDefaultAsync(r => r.Id == refreshId, ct);
        if (refresh is null || refresh.Status != RefreshStatus.Scheduled) return;
        var now = clock.GetUtcNow().UtcDateTime;
        await Settings.ApplySlotEditsAsync(db, ct); // Slot edits take effect from here
        await db.SaveChangesAsync(ct);
        // An Aspiration paused right now is left alone: nothing to propose until the user is back.
        var (start, slots, planned, currents, pauses, used) = await PrepareAsync(now, respectPauses: true, only: null, ct);

        // Anchor habits held across all Aspirations right now: "about four across all".
        var currentIds = currents.Select(p => p.Id).ToList();
        anchorHabitsInAll = await db.Habits.CountAsync(h => h.AnchorId != null && currentIds.Contains(h.PlanVersionId), ct);

        var made = new List<PlanVersion>();
        foreach (var aspiration in planned)
            made.Add(await ProposeAsync(aspiration, currents.First(p => p.AspirationId == aspiration.Id), slots, used, start, now, refresh.Id, pauses, ct));

        // Eased once every Aspiration under a shared Pause has been proposed, so none of them misses the easier week.
        foreach (var p in pauses.Where(p => planned.Any(a => IsFirstWeekBack(p, a.Id, now)))) p.EasedAt = now;

        refresh.Status = RefreshStatus.Open;
        refresh.OpenedAt = now;
        refresh.AutoApplyAt = now + WeeklyRefreshes.AutoApplyAfter;
        var ids = new Dictionary<string, Guid> { ["refreshId"] = refresh.Id };
        jobs.Add(JobKinds.RefreshApply, refresh.UserId, ids, delay: WeeklyRefreshes.AutoApplyAfter);
        if (made.Count > 0) jobs.Add(JobKinds.RefreshPrompt, refresh.UserId, ids);
        await WeeklyRefreshes.ScheduleNextAsync(db, jobs, clock, now, ct, finishing: refresh.Id);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Weekly refresh {RefreshId} proposed {Count} versions", refresh.Id, made.Count);
    }

    /// <summary>What a new two-week window is laid out from: the Aspirations with a Plan (closest to slipping first) and the Slot minutes already taken.</summary>
    private async Task<(DateOnly Start, Dictionary<Guid, Slot> Slots, List<Aspiration> Planned, List<PlanVersion> Currents, List<Pause> Pauses, Dictionary<(DateOnly, Guid), int> Used)>
        PrepareAsync(DateTime now, bool respectPauses, Guid? only, CancellationToken ct)
    {
        var start = DateOnly.FromDateTime(now).AddDays(1);
        var slots = await db.Slots.ToDictionaryAsync(s => s.Id, ct);

        var aspirations = (await db.Aspirations.Where(a => a.Status == "active").ToListAsync(ct))
            .OrderBy(a => a.SettleForDate ?? DateOnly.MaxValue).ThenBy(a => a.CreatedAt).ToList(); // the one closest to slipping gets the contested Slot
        var currents = await db.PlanVersions.Where(p => p.Status == PlanVersionStatus.Approved).ToListAsync(ct);
        var pauses = await db.Pauses.ToListAsync(ct);
        var planned = aspirations.Where(a => (only is null || a.Id == only) && currents.Any(p => p.AspirationId == a.Id)
            && (!respectPauses || !pauses.Any(p => Pauses.IsActive(p, now) && Pauses.CoversAspiration(p, a.Id)))).ToList();

        // Minutes already taken in each Slot on each day, by what is kept: other Aspirations and Check-ins already made.
        var plannedIds = planned.Select(a => a.Id).ToHashSet();
        var used = new Dictionary<(DateOnly, Guid), int>();
        foreach (var a in await db.ScheduledActions.Where(a => a.SlotId != null && a.Date >= start && (!plannedIds.Contains(a.AspirationId) || db.CheckIns.Any(c => c.ScheduledActionId == a.Id))).ToListAsync(ct))
            used[(a.Date, a.SlotId!.Value)] = used.GetValueOrDefault((a.Date, a.SlotId.Value)) + a.Minutes;
        return (start, slots, planned, currents, pauses, used);
    }

    private static bool IsFirstWeekBack(Pause p, Guid aspirationId, DateTime now) =>
        p.EasedAt is null && p.ScheduledActionId is null && Pauses.CoversAspiration(p, aspirationId)
        && Pauses.EffectiveEnd(p) is { } end && end <= now && Pauses.Days(p, now) >= Pauses.WelcomeMinDays;

    private async Task<PlanVersion> ProposeAsync(Aspiration aspiration, PlanVersion current, Dictionary<Guid, Slot> slots,
        Dictionary<(DateOnly, Guid), int> used, DateOnly start, DateTime now, Guid? refreshId, List<Pause> pauses, CancellationToken ct,
        Reshape? reshape = null, string reason = WeeklyRefreshes.Reason, Action<PlanVersion>? stamp = null, string? why = null)
    {
        // Mostly done: both of the last two weeks reached 80% of their planned minutes.
        var weekOf = async (int back, List<Pause> ps) => await MostlyDoneAsync(aspiration.Id, start.AddDays(-7 * back), start.AddDays(-7 * (back - 1)), ps, ct);
        bool[] mostly = [await weekOf(1, pauses), await weekOf(2, pauses)];

        var oldMilestones = await db.Milestones.Where(m => m.PlanVersionId == current.Id).OrderBy(m => m.Date).ToListAsync(ct);
        var oldHabits = await db.Habits.Where(h => h.PlanVersionId == current.Id).OrderBy(h => h.Title).ToListAsync(ct);
        var oldTasks = await db.PlanTasks.Where(t => t.PlanVersionId == current.Id).OrderBy(t => t.Title).ToListAsync(ct);
        var habits = oldHabits.Select(h => new H(h.Id, h.Title, h.TimesPerWeek, h.Minutes,
            h.SlotIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList(), h)).ToList();

        // First week back from a Pause: one notch lower instead of any ramp; a Catch-up starts in week two.
        var returned = reshape is not null ? [] : pauses.Where(p => IsFirstWeekBack(p, aspiration.Id, now)).ToList();

        // Ramp: one notch while both weeks were mostly done, stopping at the full share.
        var full = aspiration.ShareHoursPerWeek * 60;
        var changes = new List<JsonObject>();
        if (reshape is not null) changes.AddRange(reshape(habits));
        else if (returned.Count > 0)
        {
            var step = EaseOne(habits);
            if (step is not null) changes.Add(step);
        }
        else if (mostly.All(m => m))
        {
            var weekly = habits.Where(h => !h.IsAnchor).Sum(h => h.Times * h.Minutes); // Anchor habits are outside the Time budget
            var step = RampOne(habits, slots, full, weekly);
            if (step is not null) changes.Add(step);
        }

        // Anchor habits: outside the Time budget. A new one joins at most once a refresh, alone; otherwise one ramps by count.
        var anchors = await db.Anchors.ToDictionaryAsync(x => x.Id, ct);
        var today = start.AddDays(-1);
        var (share, band) = Progress.Tolerance(await ReportedAsync(aspiration.Id, today, ct), today);
        var anchorHabits = habits.Where(h => h.IsAnchor).ToList();
        var pending = NextQueued(aspiration, habits);
        if (reshape is null && pending is not null && band == "absorb" && anchorHabits.Count < PlanRules.MaxAnchorHabitsPerAspiration
            && anchorHabitsInAll < PlanRules.MaxAnchorHabitsInAll
            && anchorHabits.All(h => h.Old.Since is { } since && today.DayNumber - since.DayNumber >= AnchorHeldDays))
        {
            var fresh = PlanDrafter.NewHabit(pending, Guid.NewGuid(), aspiration.UserId, Guid.Empty, new(), new(), start, out var anchor);
            db.Anchors.Add(anchor!);
            anchors[anchor!.Id] = anchor;
            habits.Add(new H(Guid.Empty, fresh.Title, fresh.TimesPerWeek, 0, [], fresh));
            anchorHabitsInAll++;
            changes.Add(Change("add_anchor_habit", fresh.Title, "times_per_week", 0, fresh.TimesPerWeek));
        }
        else if (reshape is null && changes.Count == 0)
        {
            foreach (var h in anchorHabits.OrderBy(h => h.Title, StringComparer.Ordinal))
                if (h.Times < (h.Old.FullTimesPerWeek ?? h.Times) && await KeptUpAsync(aspiration.Id, h.Title, today, 2, ct))
                {
                    habits[habits.IndexOf(h)] = h with { Times = h.Times + 1 };
                    changes.Add(Change("ramp_session", h.Title, "times_per_week", h.Times, h.Times + 1));
                    break;
                }
        }

        // Reminders on Anchor habits: offered off once after ~3 weeks on target; back if a habit the user let go of slips.
        var reminders = new Dictionary<string, (bool On, bool Offered, bool ByOffer)>();
        foreach (var h in reshape is null ? anchorHabits : [])
        {
            var o = h.Old;
            if (!o.ReminderOn && o.StoppedByOffer && !await KeptUpAsync(aspiration.Id, h.Title, today, 1, ct))
            {
                reminders[h.Title] = (true, o.StopOffered, false);
                changes.Add(Change("reminder_returns", h.Title, "reminder_on", 0, 1));
            }
            else if (o.ReminderOn && !o.StopOffered && o.Since is { } since && today.DayNumber - since.DayNumber >= ReminderOfferDays
                && await KeptUpAsync(aspiration.Id, h.Title, today, 3, ct))
            {
                reminders[h.Title] = (true, true, false);
                changes.Add(Change("offer_stop_reminder", h.Title, "reminder_on", 1, 0));
            }
        }

        var version = new PlanVersion
        {
            Id = Guid.NewGuid(), UserId = aspiration.UserId, AspirationId = aspiration.Id, Status = PlanVersionStatus.Proposed,
            Rationale = "", Reason = reason, Ramp = current.Ramp, AssumptionsJson = current.AssumptionsJson,
            TwoWeeksJson = "[]", StartDate = start, CreatedAt = now, RefreshId = refreshId,
        };
        stamp?.Invoke(version);
        var milestoneIds = new Dictionary<Guid, Guid>();
        var newMilestones = oldMilestones.Select(m =>
        {
            var copy = new Milestone { Id = Guid.NewGuid(), UserId = m.UserId, PlanVersionId = version.Id, Ref = m.Ref, Date = m.Date, Title = m.Title, ReachedWhen = m.ReachedWhen, Kind = m.Kind, State = m.State };
            milestoneIds[m.Id] = copy.Id;
            return copy;
        }).ToList();

        var habitRows = habits.Select(h => (H: h, New: new Habit
        {
            Id = Guid.NewGuid(), UserId = aspiration.UserId, PlanVersionId = version.Id, Title = h.Title, TimesPerWeek = h.Times, Minutes = h.Minutes,
            SlotIds = string.Join(",", h.SlotIds), AnchorId = h.Old.AnchorId, TargetMinutes = h.Old.TargetMinutes, FullTimesPerWeek = h.Old.FullTimesPerWeek,
            PrepFor = h.Old.PrepFor, Since = h.Old.Since,
            ReminderOn = reminders.TryGetValue(h.Title, out var rs) ? rs.On : h.Old.ReminderOn,
            StopOffered = reminders.TryGetValue(h.Title, out rs) ? rs.Offered : h.Old.StopOffered,
            StoppedByOffer = reminders.TryGetValue(h.Title, out rs) ? rs.ByOffer : h.Old.StoppedByOffer,
        })).ToList();

        // Reminders carry over: a habit's new sessions keep how its latest one was set.
        // Habits are copied into every version with new IDs, so they are matched by title, as the Plan's rules keep titles unique.
        var lastReminder = (await db.ScheduledActions.Where(a => a.AspirationId == aspiration.Id && a.HabitId != null).OrderByDescending(a => a.Date).ToListAsync(ct))
            .GroupBy(a => a.Title).ToDictionary(g => g.Key, g => g.First());

        var window = new JsonArray();
        JsonObject Entry(DateOnly date, Slot? slot, string title, string kind, int minutes, Guid? habitId, Guid? taskId, ScheduledAction? like)
        {
            var o = new JsonObject
            {
                ["date"] = date.ToString("yyyy-MM-dd"), ["action"] = title, ["kind"] = kind, ["minutes"] = minutes,
                ["reminder"] = like?.ReminderKind ?? ReminderKinds.Now,
            };
            if (slot is not null) { o["slot"] = slot.Label; o["slotId"] = slot.Id; }
            if (like?.ReminderKind == ReminderKinds.GetReady) o["lead_minutes"] = like.ReminderLeadMinutes ?? ReminderKinds.DefaultLeadMinutes;
            if (habitId is not null) o["habitId"] = habitId;
            if (taskId is not null) o["taskId"] = taskId;
            return o;
        }

        foreach (var (h, row) in habitRows)
            for (var week = 0; week < 2; week++)
            {
                if (h.IsAnchor)
                {
                    var anchor = anchors[h.Old.AnchorId!.Value];
                    foreach (var date in SpreadOnAnchor(h.Times, anchor, start.AddDays(7 * week)))
                    {
                        var e = Entry(date, null, h.Title, "habit", 0, row.Id, null, lastReminder.GetValueOrDefault(h.Title));
                        e["anchorId"] = anchor.Id;
                        e["anchor"] = anchor.Words;
                        window.Add(e);
                    }
                    continue;
                }
                var days = Spread(h, slots, used, start.AddDays(7 * week), out var chosen);
                foreach (var date in days)
                {
                    var slot = chosen[date];
                    used[(date, slot.Id)] = used.GetValueOrDefault((date, slot.Id)) + h.Minutes;
                    window.Add(Entry(date, slot, h.Title, "habit", h.Minutes, row.Id, null, lastReminder.GetValueOrDefault(h.Title)));
                }
            }

        // Tasks join the window when it reaches them; one already given a day before the window is left to its Check-in.
        var windowEnd = start.AddDays(13);
        var scheduledBefore = (await db.ScheduledActions.Where(a => a.AspirationId == aspiration.Id && a.TaskId != null && a.Date < start)
            .Select(a => a.Title).ToListAsync(ct)).ToHashSet();
        var taskRows = new List<PlanTask>();
        foreach (var t in oldTasks)
        {
            var copy = new PlanTask { Id = Guid.NewGuid(), UserId = t.UserId, PlanVersionId = version.Id, MilestoneId = milestoneIds[t.MilestoneId], Title = t.Title, Minutes = t.Minutes, DueDate = t.DueDate };
            taskRows.Add(copy);
            if (scheduledBefore.Contains(t.Title) || (t.DueDate is { } d && d > windowEnd)) continue;
            if (PlaceTask(t, slots, used, start, windowEnd) is { } at)
            {
                used[(at.Date, at.Slot.Id)] = used.GetValueOrDefault((at.Date, at.Slot.Id)) + t.Minutes;
                window.Add(Entry(at.Date, at.Slot, t.Title, "task", t.Minutes, null, copy.Id, null));
            }
        }
        version.TwoWeeksJson = window.ToJsonString();

        // Words: the model phrases what the app decided.
        var brief = new JsonObject
        {
            ["aspiration"] = aspiration.ShortName,
            ["last_two_weeks_mostly_done"] = new JsonArray(mostly.Select(m => (JsonNode)m).ToArray()),
            ["first_week_back"] = returned.Count > 0,
            ["why"] = why,
            ["return_choice"] = returned.Select(p => p.ReturnChoice).FirstOrDefault(c => c is not null),
            ["at_full_share"] = habits.Where(h => !h.IsAnchor).Sum(h => h.Times * h.Minutes) >= full,
            ["changes"] = new JsonArray(changes.Select(c => (JsonNode)new JsonObject { ["code"] = c["code"]!.GetValue<string>(), ["what"] = c["title"]!.GetValue<string>(), ["field"] = c["field"]!.GetValue<string>() }).ToArray()),
        };
        var (rationale, reasons) = await PhraseAsync(brief, changes.Select(c => c["code"]!.GetValue<string>()).ToList(), ct);
        version.Rationale = rationale;
        foreach (var c in changes) c["reason"] = reasons[c["code"]!.GetValue<string>()];
        version.ChangesJson = new JsonObject
        {
            ["changes"] = new JsonArray(changes.ToArray<JsonNode?>()),
            ["stays"] = new JsonArray(
                oldMilestones.Select(m => (JsonNode)new JsonObject { ["what"] = "milestone", ["title"] = m.Title, ["date"] = m.Date.ToString("yyyy-MM-dd") })
                    .Concat(habits.Where(h => !changes.Any(c => c["title"]!.GetValue<string>() == h.Title))
                        .Select(h => (JsonNode)new JsonObject { ["what"] = "habit", ["title"] = h.Title, ["times_per_week"] = h.Times, ["minutes"] = h.Minutes })).ToArray()),
        }.ToJsonString();

        // At most one pending proposal per Aspiration: this one replaces any other.
        await db.PlanVersions.Where(p => p.AspirationId == aspiration.Id && p.Status == PlanVersionStatus.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Replaced), ct);
        if (reason != Settings.BudgetChangeReason) await Settings.ClearStaleBudgetRequestAsync(db, ct);
        db.PlanVersions.Add(version);
        db.Milestones.AddRange(newMilestones);
        db.Habits.AddRange(habitRows.Select(r => r.New));
        db.PlanTasks.AddRange(taskRows);
        return version;
    }

    /// <summary>The first queued Anchor habit (the Coach's "later" list from the Plan) the Plan does not hold yet.</summary>
    private static JsonObject? NextQueued(Aspiration aspiration, List<H> habits)
    {
        if (aspiration.AnchorQueueJson is null) return null;
        var held = habits.Select(h => h.Title).ToHashSet();
        return (JsonNode.Parse(aspiration.AnchorQueueJson) as JsonArray)?.OfType<JsonObject>().FirstOrDefault(q => HandOff.Str(q, "title") is { } t && !held.Contains(t));
    }

    private async Task<List<Reported>> ReportedAsync(Guid aspirationId, DateOnly today, CancellationToken ct) =>
        (await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.Date > today.AddDays(-14) && a.Date <= today)
            .Select(a => new Reported(a.Date, a.Minutes, db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => c.State).FirstOrDefault(),
                db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => c.MinutesSpent).FirstOrDefault(), a.AnchorId != null)).ToListAsync(ct));

    /// <summary>Each of the last <paramref name="weeks"/> weeks had sessions of this habit and at least 80% of them were done or partly.</summary>
    private async Task<bool> KeptUpAsync(Guid aspirationId, string title, DateOnly today, int weeks, CancellationToken ct)
    {
        foreach (var week in Enumerable.Range(0, weeks))
        {
            var from = today.AddDays(-7 * (week + 1));
            var to = today.AddDays(-7 * week);
            var states = await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.Title == title && a.Date > from && a.Date <= to)
                .Select(a => db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => c.State).FirstOrDefault()).ToListAsync(ct);
            if (states.Count == 0 || states.Count(x => x is CheckInState.Done or CheckInState.Partly) < WeeklyRefreshes.MostlyDone * states.Count) return false;
        }
        return true;
    }

    private async Task<bool> MostlyDoneAsync(Guid aspirationId, DateOnly from, DateOnly toExclusive, List<Pause> pauses, CancellationToken ct)
    {
        var rows = await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.Date >= from && a.Date < toExclusive)
            .Select(a => new { Action = a, a.Minutes, Check = db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => new { c.State, c.MinutesSpent }).FirstOrDefault() })
            .ToListAsync(ct);
        // Set-aside Actions are excluded from both sides.
        rows = rows.Where(r => !Pauses.IsSetAside(pauses, r.Action, r.Check is not null)).ToList();
        var plannedMinutes = rows.Sum(r => r.Minutes);
        if (plannedMinutes == 0) return false; // nothing planned: no evidence of keeping up
        var done = rows.Sum(r => r.Check?.State switch
        {
            CheckInState.Done => r.Minutes,
            CheckInState.Partly => r.Check.MinutesSpent ?? 0,
            _ => 0,
        });
        return done >= WeeklyRefreshes.MostlyDone * plannedMinutes;
    }

    /// <summary>One notch: one more session on a habit that can take it, else a few more minutes on each session. Null at the full share.</summary>
    private static JsonObject? RampOne(List<H> habits, Dictionary<Guid, Slot> slots, double full, int weekly)
    {
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            if (h.IsAnchor) continue;
            var days = h.SlotIds.Where(slots.ContainsKey).SelectMany(id => slots[id].Days.Split(',')).Distinct().Count();
            if (h.Times >= days || weekly + h.Minutes > full) continue;
            habits[i] = h with { Times = h.Times + 1 };
            return Change("ramp_session", h.Title, "times_per_week", h.Times, h.Times + 1);
        }
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            if (h.IsAnchor) continue;
            var room = h.SlotIds.Where(slots.ContainsKey).Select(id => (int)PlanRules.Room(slots[id])).DefaultIfEmpty(0).Min();
            var more = Math.Min(WeeklyRefreshes.MinutesStep, Math.Min((int)((full - weekly) / Math.Max(h.Times, 1)), room - h.Minutes));
            if (more < 1) continue;
            habits[i] = h with { Minutes = h.Minutes + more };
            return Change("ramp_minutes", h.Title, "minutes", h.Minutes, h.Minutes + more);
        }
        return null;
    }

    /// <summary>One notch down for the first week back: one session fewer on a habit that meets more than once, else a few minutes shorter.</summary>
    private static JsonObject? EaseOne(List<H> habits)
    {
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            if (h.Times <= 1) continue;
            habits[i] = h with { Times = h.Times - 1 };
            return Change("ease_back_return", h.Title, "times_per_week", h.Times, h.Times - 1);
        }
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            var less = Math.Min(WeeklyRefreshes.MinutesStep, h.Minutes - 10);
            if (less < 1) continue;
            habits[i] = h with { Minutes = h.Minutes - less };
            return Change("ease_back_return", h.Title, "minutes", h.Minutes, h.Minutes - less);
        }
        return null;
    }

    private static JsonObject Change(string code, string title, string field, int before, int after) =>
        new() { ["code"] = code, ["what"] = "habit", ["title"] = title, ["field"] = field, ["before"] = before, ["after"] = after };

    /// <summary>The days of one week a habit meets, spread evenly, at most one session a day, each in a Slot with room.</summary>
    private static List<DateOnly> Spread(H h, Dictionary<Guid, Slot> slots, Dictionary<(DateOnly, Guid), int> used, DateOnly weekStart, out Dictionary<DateOnly, Slot> chosen)
    {
        chosen = [];
        var days = new List<DateOnly>();
        for (var d = 0; d < 7; d++)
        {
            var date = weekStart.AddDays(d);
            var slot = h.SlotIds.Where(slots.ContainsKey).Select(id => slots[id])
                .FirstOrDefault(s => s.Days.Split(',').Contains(PlanRules.Day(date)) && used.GetValueOrDefault((date, s.Id)) + h.Minutes <= PlanRules.Room(s));
            if (slot is null) continue;
            days.Add(date);
            chosen[date] = slot;
        }
        if (days.Count <= h.Times) return days;
        return Enumerable.Range(0, h.Times).Select(i => days[i * days.Count / h.Times]).ToList();
    }

    /// <summary>The days of one week an Anchor habit meets: on days the Anchor applies, spread evenly.</summary>
    private static List<DateOnly> SpreadOnAnchor(int times, Anchor anchor, DateOnly weekStart)
    {
        var applies = anchor.Days.Split(',', StringSplitOptions.RemoveEmptyEntries);
        var days = Enumerable.Range(0, 7).Select(weekStart.AddDays).Where(d => applies.Length == 0 || applies.Contains(PlanRules.Day(d))).ToList();
        return days.Count <= times ? days : Enumerable.Range(0, times).Select(i => days[i * days.Count / times]).ToList();
    }

    private static (DateOnly Date, Slot Slot)? PlaceTask(PlanTask t, Dictionary<Guid, Slot> slots, Dictionary<(DateOnly, Guid), int> used, DateOnly start, DateOnly end)
    {
        (DateOnly, Slot)? first = null;
        for (var date = start; date <= end; date = date.AddDays(1))
            foreach (var s in slots.Values.OrderBy(s => s.Start))
            {
                if (!s.Days.Split(',').Contains(PlanRules.Day(date)) || used.GetValueOrDefault((date, s.Id)) + t.Minutes > PlanRules.Room(s)) continue;
                if (t.DueDate is not { } due || date <= due) return (date, s);
                first ??= (date, s);
            }
        return first;
    }

    private async Task<(string Rationale, Dictionary<string, string> Reasons)> PhraseAsync(JsonObject brief, List<string> codes, CancellationToken ct)
    {
        var choice = options.Value.For(CoachCallKind.WeeklyRefresh);
        var reply = new StringBuilder();
        await foreach (var chunk in model.StreamAsync(new CoachRequest(CoachCallKind.WeeklyRefresh, choice.Provider, choice.Model,
            RefreshPrompt.System, [new CoachTurn("user", brief.ToJsonString())]), ct))
            reply.Append(chunk);
        var text = reply.ToString();
        var from = text.IndexOf('{');
        var to = text.LastIndexOf('}');
        JsonObject? parsed = null;
        try { if (from >= 0 && to > from) parsed = JsonNode.Parse(text[from..(to + 1)]) as JsonObject; } catch (JsonException) { }
        var rationale = parsed is null ? null : HandOff.Str(parsed, "rationale");
        var reasons = (parsed?["reasons"] as JsonObject)?.ToDictionary(p => p.Key, p => p.Value?.GetValue<string>() ?? "") ?? [];
        // Late, never faked: a reply without words for every change fails the job, which retries.
        if (string.IsNullOrWhiteSpace(rationale) || codes.Any(c => !reasons.TryGetValue(c, out var r) || string.IsNullOrWhiteSpace(r)))
            throw new CoachUnavailableException("refresh_reply_invalid");
        return (rationale, reasons);
    }
}

/// <summary>Applies an unanswered refresh a day later, silently.</summary>
public sealed class RefreshApplier(WaypointDb db, JobQueue jobs, TimeProvider clock)
{
    public async Task ApplyAsync(Guid refreshId, CancellationToken ct)
    {
        var refresh = await db.WeeklyRefreshes.FirstOrDefaultAsync(r => r.Id == refreshId, ct);
        if (refresh is null || refresh.Status != RefreshStatus.Open) return;
        var pending = await db.PlanVersions.AnyAsync(p => p.RefreshId == refreshId && p.Status == PlanVersionStatus.Proposed, ct);
        await WeeklyRefreshes.ApproveAllAsync(db, clock, jobs, refresh, autoApplied: true, ct);
        refresh.Status = pending ? RefreshStatus.AutoApplied : RefreshStatus.Accepted;
        await db.SaveChangesAsync(ct);
    }
}
