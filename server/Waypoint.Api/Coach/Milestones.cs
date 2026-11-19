using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// When a Milestone counts as reached, and what follows: the Milestone's date, the ability test task, "I've got it",
/// "nearly there" and its two ways out. State changes are decided here in code; the Coach only phrases them.
/// </summary>
public sealed class MilestoneService(WaypointDb db, JobQueue jobs, TimeProvider clock)
{
    public const int TestTaskMinutes = 30;
    /// <summary>The test task goes into the last week before the date.</summary>
    public const int TestWindowDays = 7;
    public static readonly int[] ExtensionWeeks = [1, 2];
    public const string Upcoming = "upcoming", Reached = "reached", NearlyThere = "nearly_there";

    public static DateTime DueAt(DateOnly date) => date.ToDateTime(new TimeOnly(8, 0), DateTimeKind.Utc);

    /// <summary>Books what a freshly approved first Plan needs for each Milestone: its date, and for ability ones the test task.</summary>
    public void Schedule(Milestone m, Guid aspirationId)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = new Dictionary<string, Guid> { ["milestoneId"] = m.Id };
        jobs.Add(JobKinds.MilestoneDue, m.UserId, ids, aspirationId, Max(DueAt(m.Date) - now));
        if (m.Kind == "ability")
            jobs.Add(JobKinds.MilestoneTestTask, m.UserId, ids, aspirationId, Max(DueAt(m.Date.AddDays(-TestWindowDays)) - now));
    }

    private static TimeSpan Max(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : t;

    /// <summary>The Milestone in the Aspiration's current Plan version that a job or request means (a later version copies it with a new ID).</summary>
    public async Task<(Milestone? Milestone, Guid AspirationId)> CurrentAsync(Guid milestoneId, CancellationToken ct)
    {
        var any = await db.Milestones.Where(m => m.Id == milestoneId)
            .Join(db.PlanVersions, m => m.PlanVersionId, p => p.Id, (m, p) => new { m.Ref, p.AspirationId }).FirstOrDefaultAsync(ct);
        if (any is null) return (null, Guid.Empty);
        var current = await db.PlanVersions.Where(p => p.AspirationId == any.AspirationId && p.Status == PlanVersionStatus.Approved).Select(p => p.Id).FirstOrDefaultAsync(ct);
        var m2 = await db.Milestones.FirstOrDefaultAsync(m => m.PlanVersionId == current && m.Ref == any.Ref, ct);
        return (m2, any.AspirationId);
    }

    public record Work(int Planned, int Done)
    {
        public bool Enough => Planned > 0 && Done >= WeeklyRefreshes.MostlyDone * Planned;
    }

    /// <summary>Minutes planned and done for the Aspiration between the previous Milestone's date and this one's, up to today. From Check-ins only.</summary>
    public async Task<Work> WorkAsync(Milestone m, Guid aspirationId, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var previous = await db.Milestones.Where(x => x.PlanVersionId == m.PlanVersionId && x.Date < m.Date).Select(x => (DateOnly?)x.Date).MaxAsync(ct);
        var from = previous ?? DateOnly.MinValue;
        // Only days that are over count, and a one-off test task is not planned work.
        var to = m.Date < today ? m.Date : today.AddDays(-1);
        var rows = await db.ScheduledActions.Where(a => a.AspirationId == aspirationId && a.Date > from && a.Date <= to && a.TestForMilestoneRef == null)
            .Select(a => new { a.Minutes, Check = db.CheckIns.Where(c => c.ScheduledActionId == a.Id).Select(c => new { c.State, c.MinutesSpent }).FirstOrDefault() })
            .ToListAsync(ct);
        return new Work(rows.Sum(r => r.Minutes), rows.Sum(r => r.Check?.State switch
        {
            CheckInState.Done => r.Minutes,
            CheckInState.Partly => r.Check.MinutesSpent ?? 0,
            _ => 0,
        }));
    }

    public void Reach(Milestone m, Guid aspirationId)
    {
        m.State = Reached;
        m.ReachedAt = clock.GetUtcNow().UtcDateTime;
        m.NotReachedAt = null;
        jobs.Add(JobKinds.MilestoneNote, m.UserId, new Dictionary<string, Guid> { ["milestoneId"] = m.Id }, aspirationId);
    }

    /// <summary>At the Milestone's date: an effort Milestone whose work is done is reached; every other one gets asked.</summary>
    public async Task DueAsync(Guid milestoneId, CancellationToken ct)
    {
        var (m, aspirationId) = await CurrentAsync(milestoneId, ct);
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        // Extended, answered or already reached: this date's job has nothing to do.
        if (m is null || m.State != Upcoming || m.Date > today || m.AskedAt is not null) return;
        if (m.Kind == "effort" && (await WorkAsync(m, aspirationId, ct)).Enough) Reach(m, aspirationId);
        else
        {
            m.AskedAt = clock.GetUtcNow().UtcDateTime;
            jobs.Add(JobKinds.MilestoneNote, m.UserId, new Dictionary<string, Guid> { ["milestoneId"] = m.Id }, aspirationId);
        }
        await db.SaveChangesAsync(ct);
    }

    /// <summary>The one-off test task of an ability Milestone, on the first Slot day with room in the last week before the date.</summary>
    public async Task PlaceTestTaskAsync(Guid milestoneId, CancellationToken ct)
    {
        var (m, aspirationId) = await CurrentAsync(milestoneId, ct);
        if (m is null || m.State != Upcoming || m.Kind != "ability") return;
        if (await db.ScheduledActions.AnyAsync(a => a.AspirationId == aspirationId && a.TestForMilestoneRef == m.Ref && a.Date > m.Date.AddDays(-TestWindowDays - 1), ct)) return;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var first = new[] { today.AddDays(1), m.Date.AddDays(-(TestWindowDays - 1)) }.Max();
        var slots = (await db.Slots.OrderBy(s => s.Start).ToListAsync(ct));
        var taken = await db.ScheduledActions.Where(a => a.Date >= first && a.Date <= m.Date).Select(a => new { a.Date, a.SlotId, a.Minutes }).ToListAsync(ct);
        for (var date = first; date <= m.Date; date = date.AddDays(1))
            foreach (var slot in slots)
            {
                var minutes = (int)Math.Min(TestTaskMinutes, PlanRules.Room(slot));
                var used = taken.Where(t => t.Date == date && t.SlotId == slot.Id).Sum(t => t.Minutes);
                if (!slot.Days.Split(',').Contains(PlanRules.Day(date)) || used + minutes > PlanRules.Room(slot)) continue;
                db.ScheduledActions.Add(new ScheduledAction
                {
                    Id = Guid.NewGuid(), UserId = m.UserId, AspirationId = aspirationId, PlanVersionId = m.PlanVersionId, SlotId = slot.Id, Date = date,
                    Title = $"Test: {m.Title}", Minutes = minutes, TestForMilestoneRef = m.Ref,
                });
                await db.SaveChangesAsync(ct);
                return;
            }
    }
}

public static class Milestones
{
    private static object View(Milestone m) => new { m.Id, m.Date, m.Title, m.ReachedWhen, m.Kind, m.State, m.ReachedAt };

    public sealed record ExtendRequest(int? Weeks);
    public sealed record LowerBarRequest(string? ReachedWhen);

    public static void MapMilestones(this WebApplication app)
    {
        var g = app.MapGroup("/milestones").RequireAuthorization();

        // "I've got it" early, or "yes" to "Did you reach it?". The Ahead rule is judged later, at the refresh, as for any Milestone.
        g.MapPost("/{id:guid}/reached", async (Guid id, WaypointDb db, MilestoneService svc, CancellationToken ct) =>
        {
            var (m, aspirationId) = await svc.CurrentAsync(id, ct);
            if (m is null) return Results.NotFound();
            if (m.State != MilestoneService.Reached)
            {
                svc.Reach(m, aspirationId);
                await db.SaveChangesAsync(ct);
            }
            return Results.Ok(View(m));
        });

        // "No" at the date. With the work done it is "nearly there"; without, a Re-plan is offered. Never a failure.
        g.MapPost("/{id:guid}/not-reached", async (Guid id, WaypointDb db, MilestoneService svc, JobQueue jobs, TimeProvider clock, CancellationToken ct) =>
        {
            var (m, aspirationId) = await svc.CurrentAsync(id, ct);
            if (m is null) return Results.NotFound();
            if (m.State == MilestoneService.Reached) return Results.Conflict(new { error = "already_reached" });
            if (m.State == MilestoneService.NearlyThere || m.NotReachedAt is not null) return Results.Ok(View(m));
            if (m.AskedAt is null && m.Date > DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime)) return Results.Conflict(new { error = "not_due" });
            if ((await svc.WorkAsync(m, aspirationId, ct)).Enough) m.State = MilestoneService.NearlyThere;
            else m.NotReachedAt = clock.GetUtcNow().UtcDateTime;
            jobs.Add(JobKinds.MilestoneNote, m.UserId, new Dictionary<string, Guid> { ["milestoneId"] = m.Id }, aspirationId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(View(m));
        });

        // A 1-2 week extension: the date moves out, so the work remaining to it grows and the Projection moves with it.
        g.MapPost("/{id:guid}/extend", async (Guid id, ExtendRequest req, WaypointDb db, MilestoneService svc, CancellationToken ct) =>
        {
            if (req.Weeks is not { } weeks || !MilestoneService.ExtensionWeeks.Contains(weeks)) return Results.BadRequest();
            var (m, aspirationId) = await svc.CurrentAsync(id, ct);
            if (m is null) return Results.NotFound();
            if (m.State != MilestoneService.NearlyThere) return Results.Conflict(new { error = "not_nearly_there" });
            var target = await db.Aspirations.Where(a => a.Id == aspirationId).Select(a => a.SettleForDate ?? a.HopedForDate).FirstAsync(ct);
            var date = m.Date.AddDays(7 * weeks);
            if (date > target) return Results.Conflict(new { error = "past_settle_for" });
            // The Milestones keep their order: a later one is never overtaken.
            if (await db.Milestones.AnyAsync(x => x.PlanVersionId == m.PlanVersionId && x.Id != m.Id && x.Date > m.Date && x.Date <= date, ct))
                return Results.Conflict(new { error = "past_next_milestone" });
            m.Date = date;
            m.NotReachedAt = null;
            m.State = MilestoneService.Upcoming;
            m.AskedAt = null;
            svc.Schedule(m, aspirationId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(View(m));
        });

        // The other way out: the bar comes down to what the user did, and the Milestone counts as reached.
        g.MapPost("/{id:guid}/lower-bar", async (Guid id, LowerBarRequest req, WaypointDb db, MilestoneService svc, CancellationToken ct) =>
        {
            var line = req.ReachedWhen?.Trim();
            if (string.IsNullOrEmpty(line) || line.Length > 500) return Results.BadRequest();
            var (m, aspirationId) = await svc.CurrentAsync(id, ct);
            if (m is null) return Results.NotFound();
            if (m.State != MilestoneService.NearlyThere) return Results.Conflict(new { error = "not_nearly_there" });
            m.ReachedWhen = line;
            svc.Reach(m, aspirationId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(View(m));
        });
    }
}

public static class MilestonePrompt
{
    public const string System = """
        You are the Coach in Waypoint, a personal coaching app. The first message is JSON from the app about one
        Milestone; the app has already decided what happened. "say" tells you what to write. Reply with one or two
        short plain sentences in the Coach's voice, nothing else, no emojis, no guilt.

        - celebrate: ONE line celebrating that the Milestone is reached. Speak of the work done.
        - ask: ask "Did you reach it?" about the "reached_when" line. Make clear either answer is fine.
        - nearly_there: they did the work and are close. Say so, and say they can have one or two more weeks, or lower
          the bar; they pick. Never call it a failure.
        - re_plan: they did not reach it. No blame: say the Plan will bend around where they are.

        Never invent facts or numbers, and never promise an outcome.
        """;
}

/// <summary>Writes the Coach's line for a Milestone's new state, with the card the phone shows. Late if the model fails, never faked.</summary>
public sealed class MilestoneNotes(WaypointDb db, MilestoneService svc, JobQueue jobs, ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock)
{
    public async Task WriteAsync(Guid milestoneId, CancellationToken ct)
    {
        var (m, aspirationId) = await svc.CurrentAsync(milestoneId, ct);
        if (m is null) return;
        var (say, card, data) = m switch
        {
            { State: MilestoneService.Reached } => ("celebrate", "milestone_reached", new JsonObject()),
            { State: MilestoneService.NearlyThere } => ("nearly_there", "nearly_there", new JsonObject
            {
                ["options"] = new JsonArray("extend_1_week", "extend_2_weeks", "lower_bar"),
                ["extensionWeeks"] = new JsonArray(MilestoneService.ExtensionWeeks.Select(w => (JsonNode)w).ToArray()),
            }),
            { NotReachedAt: not null } => ("re_plan", "re_plan_offer", new JsonObject()),
            { AskedAt: not null } => ("ask", "milestone_ask", new JsonObject { ["options"] = new JsonArray("yes", "no") }),
            _ => ((string?)null, "", new JsonObject()),
        };
        if (say is null) return;
        var text = await PhraseAsync(CoachCallKind.MilestoneNote, MilestonePrompt.System, new JsonObject
        {
            ["say"] = say, ["milestone"] = m.Title, ["reached_when"] = m.ReachedWhen, ["kind"] = m.Kind,
        }, ct);
        data["milestoneId"] = m.Id;
        data["title"] = m.Title;
        data["reachedWhen"] = m.ReachedWhen;
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(), UserId = m.UserId, Role = "coach", Text = text, AspirationId = aspirationId, Card = card, CardJson = data.ToJsonString(),
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        });
        // At a Milestone the Plan's Measure is read too: the Coach asks for it in chat.
        if (m.State == MilestoneService.Reached && m.HasMeasure)
            jobs.Add(JobKinds.MeasureAsk, m.UserId, new Dictionary<string, Guid> { ["aspirationId"] = aspirationId }, aspirationId);
        await db.SaveChangesAsync(ct);
    }

    internal async Task<string> PhraseAsync(CoachCallKind kind, string system, JsonObject brief, CancellationToken ct)
    {
        var choice = options.Value.For(kind);
        var reply = new StringBuilder();
        await foreach (var chunk in model.StreamAsync(new CoachRequest(kind, choice.Provider, choice.Model, system, [new CoachTurn("user", brief.ToJsonString())]), ct))
            reply.Append(chunk);
        if (reply.ToString().Trim().Length == 0) throw new CoachUnavailableException("empty_reply");
        return reply.ToString().Trim();
    }
}
