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

        Voice: warm, plain, brief, forgiving, second person, no emojis, no guilt, no streaks. When a change is about a
        skip, give one line of acknowledgement and one next step, and never use the words "missed", "failed" or "streak".

        Reply with exactly one fenced json block and nothing else:

        ```json
        { "rationale": "two or three sentences on next week and why", "reasons": { "<change code>": "one short sentence" } }
        ```
        Give a reason for every code in "changes".
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
        var unreported = await db.ScheduledActions.Where(a => a.Date >= from && a.Date <= opened
                && !db.CheckIns.Any(c => c.ScheduledActionId == a.Id))
            .OrderBy(a => a.Date).ThenBy(a => a.Title).ToListAsync(ct);
        var proposals = await db.PlanVersions.Where(p => p.RefreshId == r.Id).OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var names = await db.Aspirations.ToDictionaryAsync(a => a.Id, ct);
        // Property order is the order the phone shows them: the "did it" list comes first.
        return new
        {
            Unreported = unreported.Select(a => Plans.ActionView(a, slots.GetValueOrDefault(a.SlotId), null)),
            r.Id, r.Status, r.OpenedAt, r.AutoApplyAt,
            Proposals = proposals.Select(p => new
            {
                VersionId = p.Id, p.AspirationId, ShortName = names[p.AspirationId].ShortName, Hue = names[p.AspirationId].Hue, p.Status, p.AutoApplied,
            }),
        };
    }
}

/// <summary>The refresh job: measures, ramps, lays out the next two weeks and stores one proposal per Aspiration.</summary>
public sealed class WeeklyRefreshRunner(WaypointDb db, JobQueue jobs, ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock,
    ILogger<WeeklyRefreshRunner> log)
{
    private sealed record H(Guid OldId, string Title, int Times, int Minutes, List<Guid> SlotIds);

    public async Task RunAsync(Guid refreshId, CancellationToken ct)
    {
        var refresh = await db.WeeklyRefreshes.FirstOrDefaultAsync(r => r.Id == refreshId, ct);
        if (refresh is null || refresh.Status != RefreshStatus.Scheduled) return;
        var now = clock.GetUtcNow().UtcDateTime;
        var start = DateOnly.FromDateTime(now).AddDays(1);
        var slots = await db.Slots.ToDictionaryAsync(s => s.Id, ct);

        var aspirations = (await db.Aspirations.Where(a => a.Status == "active").ToListAsync(ct))
            .OrderBy(a => a.SettleForDate ?? DateOnly.MaxValue).ThenBy(a => a.CreatedAt).ToList(); // the one closest to slipping gets the contested Slot
        var currents = await db.PlanVersions.Where(p => p.Status == PlanVersionStatus.Approved).ToListAsync(ct);
        var planned = aspirations.Where(a => currents.Any(p => p.AspirationId == a.Id)).ToList();

        // Minutes already taken in each Slot on each day, by what the refresh keeps: other Aspirations and Check-ins already made.
        var plannedIds = planned.Select(a => a.Id).ToHashSet();
        var used = new Dictionary<(DateOnly, Guid), int>();
        foreach (var a in await db.ScheduledActions.Where(a => a.Date >= start && (!plannedIds.Contains(a.AspirationId) || db.CheckIns.Any(c => c.ScheduledActionId == a.Id))).ToListAsync(ct))
            used[(a.Date, a.SlotId)] = used.GetValueOrDefault((a.Date, a.SlotId)) + a.Minutes;

        var made = new List<PlanVersion>();
        foreach (var aspiration in planned)
            made.Add(await ProposeAsync(aspiration, currents.First(p => p.AspirationId == aspiration.Id), slots, used, start, now, refresh.Id, ct));

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

    private async Task<PlanVersion> ProposeAsync(Aspiration aspiration, PlanVersion current, Dictionary<Guid, Slot> slots,
        Dictionary<(DateOnly, Guid), int> used, DateOnly start, DateTime now, Guid refreshId, CancellationToken ct)
    {
        // Mostly done: both of the last two weeks reached 80% of their planned minutes.
        var weekOf = async (int back) => await MostlyDoneAsync(aspiration.Id, start.AddDays(-7 * back), start.AddDays(-7 * (back - 1)), ct);
        bool[] mostly = [await weekOf(1), await weekOf(2)];

        var oldMilestones = await db.Milestones.Where(m => m.PlanVersionId == current.Id).OrderBy(m => m.Date).ToListAsync(ct);
        var oldHabits = await db.Habits.Where(h => h.PlanVersionId == current.Id).OrderBy(h => h.Title).ToListAsync(ct);
        var oldTasks = await db.PlanTasks.Where(t => t.PlanVersionId == current.Id).OrderBy(t => t.Title).ToListAsync(ct);
        var habits = oldHabits.Select(h => new H(h.Id, h.Title, h.TimesPerWeek, h.Minutes,
            h.SlotIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList())).ToList();

        var full = aspiration.ShareHoursPerWeek * 60;
        var changes = new List<JsonObject>();
        var band = Tolerance.Measure([await ToleranceWeekAsync(aspiration.Id, start.AddDays(-14), start.AddDays(-7), start, ct),
            await ToleranceWeekAsync(aspiration.Id, start.AddDays(-7), start, start, ct)]);

        // Three Partly in a row on one habit: a shorter version, so the Action fits.
        var offers = new List<JsonObject>();
        var shortened = new HashSet<string>();
        for (var i = 0; i < habits.Count; i++)
            if (await ShorterAsync(aspiration.Id, habits[i], start, ct) is { } shorter)
            {
                changes.Add(Change("shorter_version", habits[i].Title, "minutes", habits[i].Minutes, shorter));
                shortened.Add(habits[i].Title);
                habits[i] = habits[i] with { Minutes = shorter };
            }

        // Ease back: one notch down, the mirror of the ramp. A Re-think leaves the Plan alone and opens a conversation instead.
        if (band == Tolerance.Band.EaseBack && StepDown(habits, shortened) is { } down) changes.Add(down);
        if (band == Tolerance.Band.Rethink)
            jobs.Add(JobKinds.Rethink, aspiration.UserId, new Dictionary<string, Guid> { ["aspirationId"] = aspiration.Id }, aspiration.Id);

        // Ramp: one notch while both weeks were mostly done, stopping at the full share.
        if (mostly.All(m => m) && shortened.Count == 0)
        {
            var weekly = habits.Sum(h => h.Times * h.Minutes);
            var step = RampOne(habits, slots, full, weekly);
            if (step is not null) changes.Add(step);
        }

        var version = new PlanVersion
        {
            Id = Guid.NewGuid(), UserId = aspiration.UserId, AspirationId = aspiration.Id, Status = PlanVersionStatus.Proposed,
            Rationale = "", Reason = WeeklyRefreshes.Reason, Ramp = current.Ramp, AssumptionsJson = current.AssumptionsJson,
            TwoWeeksJson = "[]", StartDate = start, CreatedAt = now, RefreshId = refreshId,
        };
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
            SlotIds = string.Join(",", h.SlotIds),
        })).ToList();

        // Reminders carry over: a habit's new sessions keep how its latest one was set.
        // Habits are copied into every version with new IDs, so they are matched by title, as the Plan's rules keep titles unique.
        var lastReminder = (await db.ScheduledActions.Where(a => a.AspirationId == aspiration.Id && a.HabitId != null).OrderByDescending(a => a.Date).ToListAsync(ct))
            .GroupBy(a => a.Title).ToDictionary(g => g.Key, g => g.First());

        var window = new JsonArray();
        JsonObject Entry(DateOnly date, Slot slot, string title, string kind, int minutes, Guid? habitId, Guid? taskId, ScheduledAction? like)
        {
            var o = new JsonObject
            {
                ["date"] = date.ToString("yyyy-MM-dd"), ["slot"] = slot.Label, ["slotId"] = slot.Id, ["action"] = title, ["kind"] = kind, ["minutes"] = minutes,
                ["reminder"] = like?.ReminderKind ?? ReminderKinds.Now,
            };
            if (like?.ReminderKind == ReminderKinds.GetReady) o["lead_minutes"] = like.ReminderLeadMinutes ?? ReminderKinds.DefaultLeadMinutes;
            if (habitId is not null) o["habitId"] = habitId;
            if (taskId is not null) o["taskId"] = taskId;
            return o;
        }

        foreach (var (h, row) in habitRows)
            for (var week = 0; week < 2; week++)
            {
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
            var copy = new PlanTask { Id = Guid.NewGuid(), UserId = t.UserId, PlanVersionId = version.Id, MilestoneId = milestoneIds[t.MilestoneId], Title = t.Title, Minutes = t.Minutes, DueDate = t.DueDate };
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
            ["at_full_share"] = habits.Sum(h => h.Times * h.Minutes) >= full,
            ["changes"] = new JsonArray(changes.Concat(offers).Select(c => (JsonNode)new JsonObject { ["code"] = c["code"]!.GetValue<string>(), ["what"] = c["title"]!.GetValue<string>(), ["field"] = c["field"]!.GetValue<string>() }).ToArray()),
        };
        var (rationale, reasons) = await PhraseAsync(brief, changes.Concat(offers).Select(c => c["code"]!.GetValue<string>()).Distinct().ToList(), ct);
        version.Rationale = rationale;
        foreach (var c in changes.Concat(offers)) c["reason"] = reasons[c["code"]!.GetValue<string>()];
        version.ChangesJson = new JsonObject
        {
            ["changes"] = new JsonArray(changes.ToArray<JsonNode?>()),
            ["offers"] = new JsonArray(offers.ToArray<JsonNode?>()),
            ["stays"] = new JsonArray(
                oldMilestones.Select(m => (JsonNode)new JsonObject { ["what"] = "milestone", ["title"] = m.Title, ["date"] = m.Date.ToString("yyyy-MM-dd") })
                    .Concat(habits.Where(h => !changes.Any(c => c["title"]!.GetValue<string>() == h.Title))
                        .Select(h => (JsonNode)new JsonObject { ["what"] = "habit", ["title"] = h.Title, ["times_per_week"] = h.Times, ["minutes"] = h.Minutes })).ToArray()),
        }.ToJsonString();

        // At most one pending proposal per Aspiration: this one replaces any other.
        await db.PlanVersions.Where(p => p.AspirationId == aspiration.Id && p.Status == PlanVersionStatus.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Replaced), ct);
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
    private async Task<(int Done, int Total)> ToleranceWeekAsync(Guid aspirationId, DateOnly from, DateOnly to, DateOnly refreshStart, CancellationToken ct)
    {
        var rows = await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.Date >= from && a.Date < to)
            .Select(a => new { a.Date, State = db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => c.State).FirstOrDefault() })
            .ToListAsync(ct);
        var counted = rows.Where(r => r.State is not null || r.Date < refreshStart.AddDays(-7)).ToList();
        return (counted.Count(r => r.State is CheckInState.Done or CheckInState.Partly), counted.Count);
    }

    /// <summary>One notch down, mirroring <see cref="RampOne"/>: a few minutes off a session, else one session fewer. Null when nothing can step down.</summary>
    private static JsonObject? StepDown(List<H> habits, HashSet<string> skip)
    {
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            if (skip.Contains(h.Title) || h.Minutes - WeeklyRefreshes.MinutesStep < WeeklyRefreshes.MinSessionMinutes) continue;
            habits[i] = h with { Minutes = h.Minutes - WeeklyRefreshes.MinutesStep };
            return Change("ease_back", h.Title, "minutes", h.Minutes, h.Minutes - WeeklyRefreshes.MinutesStep);
        }
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            if (skip.Contains(h.Title) || h.Times <= 1) continue;
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

    private async Task<bool> MostlyDoneAsync(Guid aspirationId, DateOnly from, DateOnly toExclusive, CancellationToken ct)
    {
        var rows = await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.Date >= from && a.Date < toExclusive)
            .Select(a => new { a.Minutes, Check = db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => new { c.State, c.MinutesSpent }).FirstOrDefault() })
            .ToListAsync(ct);
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
            var days = h.SlotIds.Where(slots.ContainsKey).SelectMany(id => slots[id].Days.Split(',')).Distinct().Count();
            if (h.Times >= days || weekly + h.Minutes > full) continue;
            habits[i] = h with { Times = h.Times + 1 };
            return Change("ramp_session", h.Title, "times_per_week", h.Times, h.Times + 1);
        }
        for (var i = 0; i < habits.Count; i++)
        {
            var h = habits[i];
            var room = h.SlotIds.Where(slots.ContainsKey).Select(id => (int)PlanRules.Room(slots[id])).DefaultIfEmpty(0).Min();
            var more = Math.Min(WeeklyRefreshes.MinutesStep, Math.Min((int)((full - weekly) / Math.Max(h.Times, 1)), room - h.Minutes));
            if (more < 1) continue;
            habits[i] = h with { Minutes = h.Minutes + more };
            return Change("ramp_minutes", h.Title, "minutes", h.Minutes, h.Minutes + more);
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
        if (MissTone.Violates(rationale) || reasons.Values.Any(MissTone.Violates)) throw new CoachUnavailableException("reply_tone");
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
