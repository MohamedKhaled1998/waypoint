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

        When the JSON has "progress", the app has judged where the user stands: "state" (too_early, on_track, slipping,
        ahead, past_settle_for), "previous_state", "pace" in words and the projected window in words. Add "standing": ONE
        short sentence in the Coach's voice, saying the state change ("say": "state_change") or where they stand
        ("say": "where_you_stand"). Speak of effort and time, never percentages or odds, and never of the outcome itself.
        Never decide a state or a date yourself; say only what "progress" holds.

        Voice: warm, plain, brief, forgiving, second person, no emojis, no guilt, no streaks. When a change is about a
        skip, give one line of acknowledgement and one next step, and never use the words "missed", "failed" or "streak".

        Reply with exactly one fenced json block and nothing else:

        ```json
        { "rationale": "two or three sentences on next week and why", "reasons": { "<change code>": "one short sentence" }, "standing": "only when progress is given" }
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
    /// <summary>Easing back never shortens a session below this.</summary>
    public const int MinSessionMinutes = 15;
    /// <summary>Catch-up spreads over at most this many weeks, and adds at most this share on top of the Aspiration's share.</summary>
    public const int CatchUpWeeks = 3;
    public const double CatchUpOverShare = 0.2;
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
        g.MapPost("/{id:guid}/accept", async (Guid id, AcceptRequest? req, WaypointDb db, TimeProvider clock, JobQueue jobs, CancellationToken ct) =>
        {
            var r = await db.WeeklyRefreshes.FirstOrDefaultAsync(x => x.Id == id && x.Status != RefreshStatus.Scheduled, ct);
            if (r is null) return Results.NotFound();
            if (r.Status == RefreshStatus.Open)
            {
                if (req?.CatchUp == false) await ShiftDatesInsteadAsync(db, r, ct);
                await ApproveAllAsync(db, clock, jobs, r, autoApplied: false, ct);
                r.Status = RefreshStatus.Accepted;
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(await ViewAsync(db, r, ct));
        });
    }

    public sealed record AcceptRequest(bool? CatchUp);

    /// <summary>"Just shift the dates": the refresh's Catch-up Actions go, and what they covered waits as an offer to move dates.</summary>
    private static async Task ShiftDatesInsteadAsync(WaypointDb db, WeeklyRefresh refresh, CancellationToken ct)
    {
        foreach (var v in await db.PlanVersions.Where(p => p.RefreshId == refresh.Id && p.Status == PlanVersionStatus.Proposed).ToListAsync(ct))
        {
            var window = JsonNode.Parse(v.TwoWeeksJson)!.AsArray();
            foreach (var e in window.Where(e => e!["kind"]!.GetValue<string>() == "catch_up").ToList()) window.Remove(e);
            v.TwoWeeksJson = window.ToJsonString();
            if (v.ChangesJson is null || JsonNode.Parse(v.ChangesJson) is not JsonObject changes) continue;
            var list = (JsonArray)changes["changes"]!;
            var offers = (JsonArray)changes["offers"]!;
            foreach (var c in list.Where(c => c!["code"]!.GetValue<string>() == "catch_up").ToList())
            {
                list.Remove(c);
                offers.Add(new JsonObject
                {
                    ["code"] = "shift_dates", ["what"] = "milestones", ["title"] = c!["title"]!.GetValue<string>(), ["field"] = "offer",
                    ["minutes"] = c["minutes"]!.GetValue<int>() + c["leftover"]!.GetValue<int>(), ["reason"] = c["reason"]!.GetValue<string>(),
                });
            }
            v.ChangesJson = changes.ToJsonString();
        }
        await db.SaveChangesAsync(ct);
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
            Unreported = unreported.Select(a => Plans.ActionView(a, a.SlotId is { } sid ? slots.GetValueOrDefault(sid) : null, null, anchor: a.AnchorId is { } aid ? anchors.GetValueOrDefault(aid) : null)),
            r.Id, r.Status, r.OpenedAt, r.AutoApplyAt,
            Proposals = proposals.Select(p => new
            {
                VersionId = p.Id, p.AspirationId, ShortName = names[p.AspirationId].ShortName, Hue = names[p.AspirationId].Hue, p.Status, p.AutoApplied,
            }),
        };
    }
}

/// <summary>The refresh job: measures, ramps, lays out the next two weeks and stores one proposal per Aspiration.</summary>
public sealed partial class WeeklyRefreshRunner(WaypointDb db, JobQueue jobs, ProgressCalculator progress, ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock,
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
        {
            var note = await JudgeAsync(aspiration, start, now, refresh.Id, ct);
            made.Add(await ProposeAsync(aspiration, currents.First(p => p.AspirationId == aspiration.Id), slots, used, start, now, refresh.Id, pauses, ct, note: note));
        }

        // Eased once every Aspiration under a shared Pause has been proposed, so none of them misses the easier week.
        foreach (var p in pauses.Where(p => planned.Any(a => IsFirstWeekBack(p, a.Id, now)))) p.EasedAt = now;

        refresh.Status = RefreshStatus.Open;
        refresh.OpenedAt = now;
        refresh.AutoApplyAt = now + WeeklyRefreshes.AutoApplyAfter;
        var ids = new Dictionary<string, Guid> { ["refreshId"] = refresh.Id };
        jobs.Add(JobKinds.RefreshApply, refresh.UserId, ids, delay: WeeklyRefreshes.AutoApplyAfter);
        if (made.Count > 0) jobs.Add(JobKinds.RefreshPrompt, refresh.UserId, ids);
        // A Measure is read about every four weeks: the Coach asks in chat, and the reply is saved as a reading.
        foreach (var a in planned)
        {
            var current = currents.First(p => p.AspirationId == a.Id);
            if (!await db.Milestones.AnyAsync(m => m.PlanVersionId == current.Id && m.MeasureName != null, ct)) continue;
            var last = await db.MeasureReadings.Where(r => r.AspirationId == a.Id).Select(r => (DateOnly?)r.Date).MaxAsync(ct);
            if (last is null || DateOnly.FromDateTime(now).DayNumber - last.Value.DayNumber >= MeasureMath.AskAfterDays)
                jobs.Add(JobKinds.MeasureAsk, refresh.UserId, new Dictionary<string, Guid> { ["aspirationId"] = a.Id }, a.Id);
        }
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
        foreach (var a in await db.ScheduledActions.Where(a => a.SlotId != null && a.Date >= start && (!plannedIds.Contains(a.AspirationId) || a.TestForMilestoneRef != null || db.CheckIns.Any(c => c.ScheduledActionId == a.Id))).ToListAsync(ct))
            used[(a.Date, a.SlotId!.Value)] = used.GetValueOrDefault((a.Date, a.SlotId.Value)) + a.Minutes;
        return (start, slots, planned, currents, pauses, used);
    }

    private static bool IsFirstWeekBack(Pause p, Guid aspirationId, DateTime now) =>
        p.EasedAt is null && p.ScheduledActionId is null && Pauses.CoversAspiration(p, aspirationId)
        && Pauses.EffectiveEnd(p) is { } end && end <= now && Pauses.Days(p, now) >= Pauses.WelcomeMinDays;

    private async Task<PlanVersion> ProposeAsync(Aspiration aspiration, PlanVersion current, Dictionary<Guid, Slot> slots,
        Dictionary<(DateOnly, Guid), int> used, DateOnly start, DateTime now, Guid? refreshId, List<Pause> pauses, CancellationToken ct,
        Reshape? reshape = null, string reason = WeeklyRefreshes.Reason, Action<PlanVersion>? stamp = null, string? why = null, ProgressNote? note = null)
    {
        // Mostly done: both of the last two weeks reached 80% of their planned minutes.
        var weekOf = async (int back, List<Pause> ps) => await MostlyDoneAsync(aspiration.Id, start.AddDays(-7 * back), start.AddDays(-7 * (back - 1)), ps, ct);
        bool[] mostly = [await weekOf(1, pauses), await weekOf(2, pauses)];

        var oldMilestones = await db.Milestones.Where(m => m.PlanVersionId == current.Id).OrderBy(m => m.Date).ToListAsync(ct);
        var oldHabits = await db.Habits.Where(h => h.PlanVersionId == current.Id).OrderBy(h => h.Title).ToListAsync(ct);
        var oldTasks = await db.PlanTasks.Where(t => t.PlanVersionId == current.Id).OrderBy(t => t.Title).ToListAsync(ct);
        var habits = oldHabits.Select(h => new H(h.Id, h.Title, h.TimesPerWeek, h.Minutes,
            h.SlotIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList(), h)).ToList();

        var full = aspiration.ShareHoursPerWeek * 60;
        var changes = new List<JsonObject>();
        var band = Tolerance.Measure([await ToleranceWeekAsync(aspiration.Id, start.AddDays(-14), start.AddDays(-7), start, pauses, ct),
            await ToleranceWeekAsync(aspiration.Id, start.AddDays(-7), start, start, pauses, ct)]);

        // First week back from a Pause: one notch lower instead of any ramp or ease-back; a Catch-up starts in week two.
        // A reshape (a Time budget cut or its restore) is the whole change, so none of the refresh's own steps apply.
        var returned = reshape is not null ? [] : pauses.Where(p => IsFirstWeekBack(p, aspiration.Id, now)).ToList();

        // Three Partly in a row on one habit: a shorter version, so the Action fits.
        var offers = new List<JsonObject>();
        var shortened = new HashSet<string>();
        for (var i = 0; i < habits.Count && reshape is null; i++)
            if (await ShorterAsync(aspiration.Id, habits[i], start, ct) is { } shorter)
            {
                changes.Add(Change("shorter_version", habits[i].Title, "minutes", habits[i].Minutes, shorter));
                shortened.Add(habits[i].Title);
                habits[i] = habits[i] with { Minutes = shorter };
            }

        if (reshape is not null) changes.AddRange(reshape(habits));
        else if (returned.Count > 0)
        {
            if (EaseOne(habits) is { } step) changes.Add(step);
        }
        else
        {
            // Ease back: one notch down, the mirror of the ramp. A Re-think leaves the Plan alone and opens a conversation instead.
            if (band == Tolerance.Band.EaseBack && StepDown(habits, shortened) is { } down) changes.Add(down);
            if (band == Tolerance.Band.Rethink)
                jobs.Add(JobKinds.Rethink, aspiration.UserId, new Dictionary<string, Guid> { ["aspirationId"] = aspiration.Id }, aspiration.Id);
        }

        // Ramp: one notch while both weeks were mostly done, stopping at the full share.
        if (reshape is null && returned.Count == 0 && mostly.All(m => m) && shortened.Count == 0)
        {
            var weekly = habits.Where(h => !h.IsAnchor).Sum(h => h.Times * h.Minutes); // Anchor habits are outside the Time budget
            var step = RampOne(habits, slots, full, weekly);
            if (step is not null) changes.Add(step);
        }

        // Anchor habits: outside the Time budget. A new one joins at most once a refresh, alone; otherwise one ramps by count.
        var anchors = await db.Anchors.ToDictionaryAsync(x => x.Id, ct);
        var today = start.AddDays(-1);
        var (share, anchorBand) = Progress.Tolerance(await ReportedAsync(aspiration.Id, today, ct), today);
        var anchorHabits = habits.Where(h => h.IsAnchor).ToList();
        var pending = NextQueued(aspiration, habits);
        if (reshape is null && pending is not null && anchorBand == "absorb" && anchorHabits.Count < PlanRules.MaxAnchorHabitsPerAspiration
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
            var copy = new Milestone { Id = Guid.NewGuid(), UserId = m.UserId, PlanVersionId = version.Id, Ref = m.Ref, Date = m.Date, Title = m.Title, ReachedWhen = m.ReachedWhen, Kind = m.Kind, State = m.State,
                ReachedAt = m.ReachedAt, AskedAt = m.AskedAt, NotReachedAt = m.NotReachedAt,
                MeasureName = m.MeasureName, MeasureUnit = m.MeasureUnit, MeasureStart = m.MeasureStart, MeasureHopedFor = m.MeasureHopedFor };
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
            GuideJson = h.Old.GuideJson,
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

        // Tasks join the window when it reaches them; one already given a day before the window is left to its Check-in,
        // unless it was missed: then it moves to the next free Slot, and slipping twice offers to split it.
        var windowEnd = start.AddDays(13);
        var givenBefore = await db.ScheduledActions.Where(a => a.AspirationId == aspiration.Id && a.TaskId != null && a.Date < start)
            .Select(a => new { a.Title, a.Date, State = db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => c.State).FirstOrDefault() })
            .ToListAsync(ct);
        // Unreported only counts as a miss once the "did it" list has passed.
        bool Missed(DateOnly date, string? state) => state == CheckInState.Skipped || (state is null && date < start.AddDays(-7));
        var taskRows = new List<PlanTask>();
        foreach (var t in oldTasks)
        {
            var copy = new PlanTask { Id = Guid.NewGuid(), UserId = t.UserId, PlanVersionId = version.Id, MilestoneId = milestoneIds[t.MilestoneId], Title = t.Title, Minutes = t.Minutes, DueDate = t.DueDate, GuideJson = t.GuideJson };
            taskRows.Add(copy);
            var given = givenBefore.Where(g => g.Title == t.Title).OrderBy(g => g.Date).ToList();
            var slips = given.Count(g => Missed(g.Date, g.State));
            var latestMissed = given.Count > 0 && Missed(given[^1].Date, given[^1].State);
            if ((given.Count > 0 && !latestMissed) || (t.DueDate is { } d && d > windowEnd)) continue;
            if (PlaceTask(t, slots, used, start, windowEnd) is { } at)
            {
                used[(at.Date, at.Slot.Id)] = used.GetValueOrDefault((at.Date, at.Slot.Id)) + t.Minutes;
                window.Add(Entry(at.Date, at.Slot, t.Title, "task", t.Minutes, null, copy.Id, null));
                if (latestMissed)
                {
                    changes.Add(new JsonObject
                    {
                        ["code"] = "task_moved", ["what"] = "task", ["title"] = t.Title, ["field"] = "date",
                        ["before"] = given[^1].Date.ToString("yyyy-MM-dd"), ["after"] = at.Date.ToString("yyyy-MM-dd"),
                    });
                    if (slips >= 2) offers.Add(new JsonObject { ["code"] = "split_task", ["what"] = "task", ["title"] = t.Title, ["field"] = "offer" });
                }
            }
        }

        // Shortfall of the week just ended (habit minutes skipped or cut short; tasks move instead). Catch-up only while
        // absorbing; while easing back or re-thinking the shortfall moves dates instead, as a proposal.
        var shortfalls = (await db.ScheduledActions.Where(a => a.AspirationId == aspiration.Id && a.HabitId != null && a.Date >= start.AddDays(-7) && a.Date < start)
                .Select(a => new { a.Title, a.Minutes, Check = db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => new { c.State, c.MinutesSpent }).FirstOrDefault() })
                .ToListAsync(ct))
            .Where(a => a.Check is not null)
            .GroupBy(a => a.Title)
            .ToDictionary(g => g.Key, g => g.Sum(a => a.Check!.State switch
            {
                CheckInState.Skipped => a.Minutes,
                CheckInState.Partly => Math.Max(0, a.Minutes - (a.Check.MinutesSpent ?? 0)),
                _ => 0,
            }));
        var shortfall = shortfalls.Values.Sum();
        if (shortfall > 0 && band == Tolerance.Band.Absorb)
        {
            var weekMinutes = Enumerable.Range(0, WeeklyRefreshes.CatchUpWeeks).Select(w => window.Sum(e =>
                PlanRules.ParseDate(e!["date"]!.GetValue<string>()) is { } d && d >= start.AddDays(7 * w) && d < start.AddDays(7 * w + 7) ? e["minutes"]!.GetValue<int>() : 0)).ToArray();
            var left = shortfall;
            foreach (var (h, row) in habitRows.Where(r => shortfalls.GetValueOrDefault(r.H.Title) > 0))
            {
                var mine = Math.Min(left, shortfalls[h.Title]);
                var placed = 0;
                // The window holds two weeks of concrete Actions, so Catch-up spreads over those, never over the ~3-week limit.
                for (var w = 0; w < 2 && mine - placed > 0; w++)
                {
                    var weekCap = (int)Math.Floor(full * (1 + WeeklyRefreshes.CatchUpOverShare)) - weekMinutes[w];
                    var target = Math.Min(weekCap, (int)Math.Ceiling((mine - placed) / (double)(2 - w)));
                    for (var d = 0; d < 7 && target > 0 && mine - placed > 0; d++)
                    {
                        var date = start.AddDays(7 * w + d);
                        foreach (var slot in h.SlotIds.Where(slots.ContainsKey).Select(id => slots[id]).Where(sl => sl.Days.Split(',').Contains(PlanRules.Day(date))))
                        {
                            var amount = Math.Min(Math.Min(target, mine - placed), (int)PlanRules.Room(slot) - used.GetValueOrDefault((date, slot.Id)));
                            if (amount < WeeklyRefreshes.MinutesStep) continue;
                            used[(date, slot.Id)] = used.GetValueOrDefault((date, slot.Id)) + amount;
                            window.Add(Entry(date, slot, $"{h.Title} (catch-up)", "catch_up", amount, null, null, lastReminder.GetValueOrDefault(h.Title)));
                            weekMinutes[w] += amount;
                            target -= amount;
                            placed += amount;
                            break; // one catch-up a day
                        }
                    }
                }
                if (placed > 0)
                    changes.Add(new JsonObject
                    {
                        ["code"] = "catch_up", ["what"] = "habit", ["title"] = h.Title, ["field"] = "minutes", ["minutes"] = placed,
                        ["leftover"] = shortfalls[h.Title] - placed, ["alternative"] = "shift_dates",
                    });
                left -= shortfalls[h.Title];
            }
            var unplaced = shortfall - changes.Where(c => c["code"]!.GetValue<string>() == "catch_up").Sum(c => c["minutes"]!.GetValue<int>());
            if (unplaced > 0) offers.Add(new JsonObject { ["code"] = "shift_dates", ["what"] = "milestones", ["title"] = aspiration.ShortName, ["field"] = "offer", ["minutes"] = unplaced });
        }
        else if (shortfall > 0)
            offers.Add(new JsonObject { ["code"] = "shift_dates", ["what"] = "milestones", ["title"] = aspiration.ShortName, ["field"] = "offer", ["minutes"] = shortfall });
        version.TwoWeeksJson = window.ToJsonString();

        // Words: the model phrases what the app decided.
        var brief = new JsonObject
        {
            ["aspiration"] = aspiration.ShortName,
            ["last_two_weeks_mostly_done"] = new JsonArray(mostly.Select(m => (JsonNode)m).ToArray()),
            ["tolerance"] = band.ToString(),
            ["first_week_back"] = returned.Count > 0,
            ["why"] = why,
            ["return_choice"] = returned.Select(p => p.ReturnChoice).FirstOrDefault(c => c is not null),
            ["at_full_share"] = habits.Where(h => !h.IsAnchor).Sum(h => h.Times * h.Minutes) >= full,
            ["changes"] = new JsonArray(changes.Concat(offers).Select(c => (JsonNode)new JsonObject { ["code"] = c["code"]!.GetValue<string>(), ["what"] = c["title"]!.GetValue<string>(), ["field"] = c["field"]!.GetValue<string>() }).ToArray()),
        };
        // Only a refresh judges progress; a Time budget cut or its restore says nothing about where the user stands.
        var say = note is { Say: true };
        if (say) brief["progress"] = note!.Brief;
        var (rationale, reasons, standing) = await PhraseAsync(brief, changes.Concat(offers).Select(c => c["code"]!.GetValue<string>()).Distinct().ToList(), say, ct);
        version.Rationale = rationale;
        if (say)
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(), UserId = aspiration.UserId, Role = "coach", Text = standing!, AspirationId = aspiration.Id, CreatedAt = now,
            });
        foreach (var c in changes.Concat(offers)) c["reason"] = reasons[c["code"]!.GetValue<string>()];
        changes.AddRange(await RefreshGuidesAsync(aspiration, habitRows.Select(r => r.New).ToList(), taskRows, mostly, ct));
        version.ChangesJson = new JsonObject
        {
            ["changes"] = new JsonArray(changes.ToArray<JsonNode?>()),
            ["offers"] = new JsonArray(offers.ToArray<JsonNode?>()),
            ["stays"] = new JsonArray(
                oldMilestones.Select(m => (JsonNode)new JsonObject { ["what"] = "milestone", ["title"] = m.Title, ["date"] = m.Date.ToString("yyyy-MM-dd") })
                    .Concat(habits.Where(h => !changes.Any(c => c["what"]!.GetValue<string>() == "habit" && c["title"]!.GetValue<string>() == h.Title))
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

    /// <summary>
    /// Done and partly Actions against Actions counted in [from, to). Unreported ones in the week that just ended are
    /// still on the "did it" list, so they are left out; older unreported ones count as not done.
    /// </summary>
    private async Task<(int Done, int Total)> ToleranceWeekAsync(Guid aspirationId, DateOnly from, DateOnly to, DateOnly refreshStart, List<Pause> pauses,
        CancellationToken ct)
    {
        var rows = await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.Date >= from && a.Date < to)
            .Select(a => new { Action = a, a.Date, State = db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => c.State).FirstOrDefault() })
            .ToListAsync(ct);
        // Set aside by a Pause never counts, either way.
        var counted = rows.Where(r => !Pauses.IsSetAside(pauses, r.Action, r.State is not null))
            .Where(r => r.State is not null || r.Date < refreshStart.AddDays(-7)).ToList();
        return (counted.Count(r => r.State is CheckInState.Done or CheckInState.Partly), counted.Count);
    }

    /// <summary>One notch down, mirroring <see cref="RampOne"/>: a few minutes off a session, else one session fewer. Null when nothing can step down.</summary>
    private static JsonObject? StepDown(List<H> habits, HashSet<string> skip)
    {
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            if (h.IsAnchor || skip.Contains(h.Title) || h.Minutes - WeeklyRefreshes.MinutesStep < WeeklyRefreshes.MinSessionMinutes) continue;
            habits[i] = h with { Minutes = h.Minutes - WeeklyRefreshes.MinutesStep };
            return Change("ease_back", h.Title, "minutes", h.Minutes, h.Minutes - WeeklyRefreshes.MinutesStep);
        }
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            if (h.IsAnchor || skip.Contains(h.Title) || h.Times <= 1) continue;
            habits[i] = h with { Times = h.Times - 1 };
            return Change("ease_back", h.Title, "times_per_week", h.Times, h.Times - 1);
        }
        return null;
    }

    /// <summary>The shorter session to offer when the habit's last three sessions were all Partly; null otherwise.</summary>
    private async Task<int?> ShorterAsync(Guid aspirationId, H habit, DateOnly start, CancellationToken ct)
    {
        var lastThree = await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.HabitId != null && a.Title == habit.Title && a.Date < start)
            .OrderByDescending(a => a.Date)
            .Select(a => db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => new { c.State, c.MinutesSpent }).FirstOrDefault())
            .Take(3).ToListAsync(ct);
        if (lastThree.Count < 3 || lastThree.Any(c => c?.State != CheckInState.Partly)) return null;
        var average = lastThree.Average(c => c!.MinutesSpent ?? 0);
        var rounded = (int)Math.Round(average / WeeklyRefreshes.MinutesStep) * WeeklyRefreshes.MinutesStep;
        var shorter = Math.Min(habit.Minutes - WeeklyRefreshes.MinutesStep, Math.Max(WeeklyRefreshes.MinSessionMinutes, rounded));
        return shorter >= WeeklyRefreshes.MinSessionMinutes && shorter < habit.Minutes ? shorter : null;
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

    private sealed record ProgressNote(bool Say, JsonObject Brief);

    /// <summary>Step 3 of the refresh: Pace and Projection, then the state, stored for the next refresh to compare with.</summary>
    private async Task<ProgressNote> JudgeAsync(Aspiration a, DateOnly start, DateTime now, Guid refreshId, CancellationToken ct)
    {
        var snap = await progress.ComputeAsync(a, start, ct);
        var number = snap.Readings + 1;
        var tooEarly = number <= ProgressMath.TooEarlyRefreshes;
        var middleOver = !tooEarly && snap is { Next: { } next, NextProjection: { } np } && np.Middle > next.Date;
        var lateEarly = !tooEarly && snap is { Next: { } n2, NextProjection: { } p2 } && p2.Late.AddDays(7) <= n2.Date;
        var pastSettle = !tooEarly && a.SettleForDate is { } settle && snap.FinalProjection is { } fp && fp.Early > settle;
        var state = ProgressMath.Judge(number, middleOver, lateEarly, pastSettle, snap.Latest);

        var changed = snap.Latest is not null && snap.Latest.State != state;
        var recent = await db.ProgressReadings.AnyAsync(r => r.AspirationId == a.Id && r.Spoke && r.At > now.AddDays(-MonthlyAfterDays), ct);
        var monthly = !changed && !tooEarly && !recent;
        db.ProgressReadings.Add(new ProgressReading
        {
            Id = Guid.NewGuid(), UserId = a.UserId, AspirationId = a.Id, RefreshId = refreshId, PlanVersionId = snap.Current?.Id, At = now, Pace = snap.Pace,
            State = state, MiddleOver = middleOver, LateEarly = lateEarly, PastSettleFor = pastSettle, Spoke = changed || monthly,
        });
        var brief = new JsonObject
        {
            ["say"] = changed ? "state_change" : "where_you_stand",
            ["state"] = state,
            ["previous_state"] = snap.Latest?.State,
            ["pace"] = ProgressMath.PaceWords(snap.Pace),
            ["next_milestone"] = snap.Next?.Title,
        };
        if (!tooEarly && snap.NextProjection is { } proj)
        {
            brief["projected_from"] = $"{ProgressMath.PartOfMonth(proj.Early)} {proj.Early:MMMM}";
            brief["projected_to"] = $"{ProgressMath.PartOfMonth(proj.Late)} {proj.Late:MMMM}";
        }
        return new ProgressNote(changed || monthly, brief);
    }

    private const int MonthlyAfterDays = 28;

    /// <summary>
    /// "Here's what I changed" for Guides: the Coach progresses each Guide by a small step, and only changes that pass
    /// <see cref="GuideRules"/> (against the Guide they replace) are kept, with the Coach's reason. A reply the Coach can't
    /// give throws, so the refresh is late rather than silent about Guides.
    /// </summary>
    private async Task<List<JsonObject>> RefreshGuidesAsync(Aspiration aspiration, List<Habit> habits, List<PlanTask> tasks, bool[] mostly, CancellationToken ct)
    {
        var current = habits.Where(h => h.GuideJson != null).Select(h => (h.Title, Set: (Action<string>)(g => h.GuideJson = g), Json: h.GuideJson!))
            .Concat(tasks.Where(t => t.GuideJson != null).Select(t => (t.Title, Set: (Action<string>)(g => t.GuideJson = g), Json: t.GuideJson!))).ToList();
        if (current.Count == 0) return [];

        var interview = await db.Interviews.Where(i => i.AspirationId == aspiration.Id && i.HandOffJson != null).OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct);
        var about = GuideRules.About(interview?.HandOffJson);
        var brief = new JsonObject
        {
            ["mode"] = "refresh",
            ["domain_picture"] = (JsonNode.Parse(interview?.HandOffJson ?? "{}")?["aspiration"]?["domain_picture"])?.DeepClone(),
            ["last_two_weeks_mostly_done"] = new JsonArray(mostly.Select(m => (JsonNode)m).ToArray()),
            ["guides"] = new JsonArray(current.Select(c => (JsonNode)new JsonObject { ["action"] = c.Title, ["guide"] = JsonNode.Parse(c.Json) }).ToArray()),
        };
        var choice = options.Value.For(CoachCallKind.Guide);
        var reply = new StringBuilder();
        await foreach (var chunk in model.StreamAsync(new CoachRequest(CoachCallKind.Guide, choice.Provider, choice.Model, GuidePrompt.System,
            [new CoachTurn("user", brief.ToJsonString())]), ct))
            reply.Append(chunk);
        if (GuideRules.ParseObject(reply.ToString())?["guides"] is not JsonArray list) throw new CoachUnavailableException("guide_reply_invalid");

        var made = new List<JsonObject>();
        var accepted = new HashSet<string>();
        foreach (var item in list.OfType<JsonObject>())
        {
            var title = HandOff.Str(item, "action");
            var reason = HandOff.Str(item, "reason");
            var was = current.FirstOrDefault(c => c.Title == title);
            if (was.Title is null || string.IsNullOrWhiteSpace(reason) || accepted.Contains(title!)) continue;
            var before = JsonNode.Parse(was.Json)!.AsObject();
            var problems = GuideRules.Validate(item, about, before);
            if (problems.Count > 0)
            {
                log.LogInformation("Guide change for Aspiration {AspirationId} dropped: {Problems}", aspiration.Id, string.Join(",", problems));
                continue;
            }
            var next = GuideRules.Normalise(item);
            if (next.ToJsonString() == before.ToJsonString()) continue;
            // The referral is kept as it was unless something new happened; a changed one shows as a new card.
            was.Set(next.ToJsonString());
            accepted.Add(title!);
            made.Add(new JsonObject { ["code"] = "guide_changed", ["what"] = "guide", ["title"] = title, ["reason"] = reason!.Trim() });
        }
        // A changed referral shows as a card when the proposal is approved (see Plans.Approve), not while it is drafted.
        return made;
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

    private async Task<(string Rationale, Dictionary<string, string> Reasons, string? Standing)> PhraseAsync(JsonObject brief, List<string> codes, bool needStanding, CancellationToken ct)
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
        var standing = parsed is null ? null : HandOff.Str(parsed, "standing");
        if (string.IsNullOrWhiteSpace(rationale) || codes.Any(c => !reasons.TryGetValue(c, out var r) || string.IsNullOrWhiteSpace(r))
            || (needStanding && string.IsNullOrWhiteSpace(standing)))
            throw new CoachUnavailableException("refresh_reply_invalid");
        if (MissTone.Violates(rationale) || reasons.Values.Any(MissTone.Violates)) throw new CoachUnavailableException("reply_tone");
        return (rationale, reasons, standing);
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
