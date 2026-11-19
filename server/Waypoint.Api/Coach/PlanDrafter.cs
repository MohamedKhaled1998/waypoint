using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// The Plan job: asks the Coach for a Plan, checks it against the hard rules, and stores it as a proposed version.
/// A draft that breaks a rule goes back to the Coach once with the problem codes; if it still fails the job fails and
/// is retried later. Runs as the job's user, so the query filter applies.
/// </summary>
public sealed class PlanDrafter(WaypointDb db, JobQueue jobs, ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock,
    ILogger<PlanDrafter> log)
{
    public async Task DraftAsync(Guid aspirationId, CancellationToken ct)
    {
        var aspiration = await db.Aspirations.FirstOrDefaultAsync(a => a.Id == aspirationId, ct)
            ?? throw new InvalidOperationException("aspiration_missing");
        var interview = await db.Interviews.Where(i => i.AspirationId == aspirationId && i.HandOffJson != null)
            .OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("interview_missing");
        var slots = await db.Slots.OrderBy(s => s.Start).ToListAsync(ct);
        var user = await db.Users.SingleAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var start = today.AddDays(1);

        var handOff = JsonNode.Parse(interview.HandOffJson!)!.AsObject();
        var brief = BriefFor(today, start, slots, user, handOff);
        var otherAnchors = await OtherAnchorsAsync(aspirationId, ct);
        await DraftFromAsync(aspiration, brief, CoachCallKind.Plan, PlanPrompt.System, "first_plan", slots, start, now, otherAnchors, ct);
    }

    /// <summary>A Re-plan the user asked for in chat: the Coach redraws the Plan around what they said, and it waits as a proposal.</summary>
    public async Task RePlanAsync(Guid chatMessageId, Guid aspirationId, CancellationToken ct)
    {
        var aspiration = await db.Aspirations.FirstOrDefaultAsync(a => a.Id == aspirationId && a.Status == "active", ct);
        var message = await db.ChatMessages.FirstOrDefaultAsync(m => m.Id == chatMessageId && m.Role == "user", ct);
        var current = await db.PlanVersions.FirstOrDefaultAsync(p => p.AspirationId == aspirationId && p.Status == PlanVersionStatus.Approved, ct);
        if (aspiration is null || message is null || current is null) return; // nothing to re-plan any more
        var interview = await db.Interviews.Where(i => i.AspirationId == aspirationId && i.HandOffJson != null)
            .OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("interview_missing");
        var slots = await db.Slots.OrderBy(s => s.Start).ToListAsync(ct);
        var user = await db.Users.SingleAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        var start = today.AddDays(1);

        var brief = BriefFor(today, start, slots, user, JsonNode.Parse(interview.HandOffJson!)!.AsObject());
        var milestones = await db.Milestones.Where(m => m.PlanVersionId == current.Id).OrderBy(m => m.Date).ToListAsync(ct);
        var habits = await db.Habits.Where(h => h.PlanVersionId == current.Id).OrderBy(h => h.Title).ToListAsync(ct);
        var slotLabels = slots.ToDictionary(s => s.Id, s => s.Label);
        brief["current_plan"] = new JsonObject
        {
            ["rationale"] = current.Rationale,
            ["milestones"] = new JsonArray(milestones.Select(m => (JsonNode)new JsonObject { ["title"] = m.Title, ["date"] = m.Date.ToString("yyyy-MM-dd"), ["kind"] = m.Kind, ["reached_when"] = m.ReachedWhen }).ToArray()),
            ["habits"] = new JsonArray(habits.Select(h => (JsonNode)new JsonObject
            {
                ["title"] = h.Title, ["times_per_week"] = h.TimesPerWeek, ["minutes"] = h.Minutes,
                ["slots"] = new JsonArray(h.SlotIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(id => (JsonNode)slotLabels.GetValueOrDefault(Guid.Parse(id), "")).ToArray()),
            }).ToArray()),
        };
        brief["change_request"] = message.Text; // free text: only in the model call, never in the job or the logs
        var otherAnchors = await OtherAnchorsAsync(aspirationId, ct);
        await DraftFromAsync(aspiration, brief, CoachCallKind.RePlan, PlanPrompt.System + PlanPrompt.RePlan, RePlanReason, slots, start, now, otherAnchors, ct);
    }

    public const string RePlanReason = "re_plan";

    private async Task<int> OtherAnchorsAsync(Guid aspirationId, CancellationToken ct) =>
        // Anchor habits the user's other Aspirations already hold count toward "about four across all".
        await db.Habits.CountAsync(h => h.AnchorId != null
            && db.PlanVersions.Any(p => p.Id == h.PlanVersionId && p.Status == PlanVersionStatus.Approved && p.AspirationId != aspirationId), ct);

    private static JsonObject BriefFor(DateOnly today, DateOnly start, List<Slot> slots, User user, JsonObject handOff)
    {
        return new JsonObject
        {
            ["today"] = today.ToString("yyyy-MM-dd"),
            ["start"] = start.ToString("yyyy-MM-dd"),
            ["user"] = new JsonObject
            {
                ["slots"] = new JsonArray(slots.Select(s => (JsonNode)new JsonObject
                {
                    ["label"] = s.Label, ["days"] = new JsonArray(s.Days.Split(',').Select(d => (JsonNode)d).ToArray()),
                    ["start"] = s.Start, ["end"] = s.End,
                }).ToArray()),
                ["time_budget_hours_per_week"] = user.TimeBudgetHours,
            },
            ["aspiration"] = handOff["aspiration"]?.DeepClone(),
        };
    }

    private async Task DraftFromAsync(Aspiration aspiration, JsonObject brief, CoachCallKind kind, string system, string reason, List<Slot> slots,
        DateOnly start, DateTime now, int otherAnchors, CancellationToken ct)
    {
        var aspirationId = aspiration.Id;
        var choice = options.Value.For(kind);
        var turns = new List<CoachTurn> { new("user", brief.ToJsonString()) };
        JsonObject? plan = null;
        List<string> problems = [];
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var full = new StringBuilder();
            await foreach (var chunk in model.StreamAsync(
                new CoachRequest(kind, choice.Provider, choice.Model, system, turns), ct))
                full.Append(chunk);
            if (full.Length == 0) throw new CoachUnavailableException("empty_reply");

            plan = ParseJson(full.ToString());
            problems = PlanRules.Validate(plan, slots, start, aspiration.SettleForDate, otherAnchors);
            if (problems.Count == 0) break;
            log.LogInformation("Plan draft for Aspiration {AspirationId} rejected on attempt {Attempt}: {Problems}",
                aspirationId, attempt, string.Join(",", problems));
            turns.Add(new CoachTurn("assistant", full.ToString()));
            turns.Add(new CoachTurn("user", PlanPrompt.Rejected(problems)));
        }
        if (problems.Count > 0) throw new PlanProblemException(problems);

        await StoreAsync(aspiration, plan!, slots, start, now, reason, ct);
    }

    private async Task StoreAsync(Aspiration aspiration, JsonObject plan, List<Slot> slots, DateOnly start, DateTime now, string reason, CancellationToken ct)
    {
        // At most one pending proposal per Aspiration: a new one replaces the old.
        // Conditional, so a proposal approved a moment ago is not overwritten.
        await db.PlanVersions.Where(p => p.AspirationId == aspiration.Id && p.Status == PlanVersionStatus.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Replaced), ct);
        await Settings.ClearStaleBudgetRequestAsync(db, ct);

        var byLabel = slots.GroupBy(s => s.Label).ToDictionary(g => g.Key, g => g.First());
        var habitIds = new Dictionary<string, Guid>();
        var taskIds = new Dictionary<string, Guid>();
        var version = new PlanVersion
        {
            Id = Guid.NewGuid(), UserId = aspiration.UserId, AspirationId = aspiration.Id, Status = PlanVersionStatus.Proposed,
            Rationale = HandOff.Str(plan, "rationale")!, Reason = reason, Ramp = HandOff.Str(plan, "ramp") ?? "",
            AssumptionsJson = (plan["assumptions"] as JsonArray ?? []).ToJsonString(),
            TwoWeeksJson = "[]", StartDate = start, CreatedAt = now,
        };
        db.PlanVersions.Add(version);

        var milestoneIds = new Dictionary<string, Guid>();
        foreach (var m in plan["milestones"]!.AsArray().Select(m => m!.AsObject()))
        {
            var id = Guid.NewGuid();
            milestoneIds[HandOff.Str(m, "id")!] = id;
            db.Milestones.Add(new Milestone
            {
                Id = id, UserId = aspiration.UserId, PlanVersionId = version.Id, Ref = HandOff.Str(m, "id")!,
                Date = PlanRules.ParseDate(HandOff.Str(m, "date"))!.Value, Title = HandOff.Str(m, "title")!,
                ReachedWhen = HandOff.Str(m, "reached_when")!, Kind = HandOff.Str(m, "kind")!, State = "upcoming",
            });
        }
        var anchorIds = new Dictionary<string, Guid>();
        foreach (var h in plan["habits"]!.AsArray().Select(h => h!.AsObject()))
        {
            var habitId = habitIds[HandOff.Str(h, "title")!] = Guid.NewGuid();
            db.Habits.Add(NewHabit(h, habitId, aspiration.UserId, version.Id, byLabel, anchorIds, start, out var anchor));
            if (anchor is not null) db.Anchors.Add(anchor);
        }
        // Anchor habits held back wait in the queue: one is added at a time, at a refresh.
        if (reason == "first_plan")
            aspiration.AnchorQueueJson = plan["anchor_later"] is JsonArray { Count: > 0 } later ? later.ToJsonString() : null;
        foreach (var t in plan["tasks"]!.AsArray().Select(t => t!.AsObject()))
        {
            var taskId = taskIds[HandOff.Str(t, "title")!] = Guid.NewGuid();
            db.PlanTasks.Add(new PlanTask
            {
                Id = taskId, UserId = aspiration.UserId, PlanVersionId = version.Id,
                MilestoneId = milestoneIds[HandOff.Str(t, "milestone")!], Title = HandOff.Str(t, "title")!,
                Minutes = (int)HandOff.Num(t, "minutes")!.Value, DueDate = PlanRules.ParseDate(HandOff.Str(t, "due")),
            });
        }

        // The window keeps its own references: approving links each Action by ID, never by title or label again.
        var window = new JsonArray();
        foreach (var a in plan["first_two_weeks"]!.AsArray().Select(a => a!.AsObject()))
        {
            var copy = (JsonObject)a.DeepClone();
            var title = HandOff.Str(a, "action")!;
            if (HandOff.Str(a, "anchor") is { } words) copy["anchorId"] = anchorIds[words];
            else copy["slotId"] = byLabel[HandOff.Str(a, "slot")!].Id;
            if (HandOff.Str(a, "kind") == "habit" && habitIds.TryGetValue(title, out var hid)) copy["habitId"] = hid;
            if (HandOff.Str(a, "kind") == "task" && taskIds.TryGetValue(title, out var tid)) copy["taskId"] = tid;
            window.Add(copy);
        }
        version.TwoWeeksJson = window.ToJsonString();

        BudgetCuts.ProposalCard(db, jobs, aspiration, version, now);
        await db.SaveChangesAsync(ct);
        log.LogInformation("Plan version {PlanVersionId} proposed for Aspiration {AspirationId}", version.Id, aspiration.Id);
    }

    /// <summary>
    /// A habit row from the Coach's habit object. An Anchor habit also creates its Anchor (the user confirms it by approving the Plan);
    /// <paramref name="anchorIds"/> remembers each Anchor by its words so the window's Actions find it.
    /// </summary>
    internal static Habit NewHabit(JsonObject h, Guid id, Guid userId, Guid versionId, Dictionary<string, Slot> byLabel,
        Dictionary<string, Guid> anchorIds, DateOnly since, out Anchor? created)
    {
        created = null;
        var habit = new Habit
        {
            Id = id, UserId = userId, PlanVersionId = versionId, Title = HandOff.Str(h, "title")!,
            TimesPerWeek = (int)HandOff.Num(h, "times_per_week")!.Value, Minutes = 0, SlotIds = "", Since = since,
            PrepFor = HandOff.Str(h, "prep_for"),
        };
        if (h["anchor"] is JsonObject a)
        {
            var words = HandOff.Str(a, "words")!;
            var anchor = created = new Anchor
            {
                Id = Guid.NewGuid(), UserId = userId, Words = words, Time = HandOff.Str(a, "time")!,
                Days = string.Join(",", (a["days"] as JsonArray ?? []).Select(d => d!.GetValue<string>())),
            };
            anchorIds[words] = anchor.Id;
            habit.AnchorId = anchor.Id;
            habit.TargetMinutes = HandOff.Num(h, "target_minutes") is { } t ? (int)t : null;
            habit.FullTimesPerWeek = HandOff.Num(h, "full_times_per_week") is { } f ? (int)f : habit.TimesPerWeek;
        }
        else
        {
            habit.Minutes = (int)HandOff.Num(h, "minutes")!.Value;
            habit.SlotIds = string.Join(",", h["slots"]!.AsArray().Select(s => byLabel[s!.GetValue<string>()].Id));
        }
        return habit;
    }

    /// <summary>The first JSON object in a reply, with or without a code fence around it.</summary>
    private static JsonObject? ParseJson(string reply)
    {
        var from = reply.IndexOf('{');
        var to = reply.LastIndexOf('}');
        if (from < 0 || to <= from) return null;
        try { return JsonNode.Parse(reply[from..(to + 1)]) as JsonObject; }
        catch (JsonException) { return null; }
    }
}
