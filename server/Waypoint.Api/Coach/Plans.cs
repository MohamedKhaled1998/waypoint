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
                .Select(p => new { p.Id, p.AspirationId, p.Status }).ToListAsync(ct);
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
                await jobs.EnqueueAsync(JobKinds.Plan, me.Id!.Value, new Dictionary<string, Guid> { ["aspirationId"] = id }, id, ct);
            return Results.Accepted();
        });

        g.MapGet("/scheduled-actions", async (WaypointDb db, CancellationToken ct) =>
        {
            var slots = await db.Slots.ToDictionaryAsync(s => s.Id, s => s.Label, ct);
            return Results.Ok((await db.ScheduledActions.OrderBy(a => a.Date).ThenBy(a => a.Title).ToListAsync(ct)).Select(a => new
            {
                a.Id, a.AspirationId, a.PlanVersionId, a.Date, Slot = slots.GetValueOrDefault(a.SlotId), a.Title, a.Minutes,
            }));
        });
    }

    private static async Task<IResult> Approve(Guid id, WaypointDb db, TimeProvider clock, JobQueue jobs, CancellationToken ct)
    {
        var version = await db.PlanVersions.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (version is null) return Results.NotFound();
        if (version.Status == PlanVersionStatus.Approved) return Results.Ok(await ViewAsync(db, version, ct));
        if (version.Status != PlanVersionStatus.Proposed) return await StaleAsync(db, version, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var today = DateOnly.FromDateTime(now);
        if (today > version.StartDate)
        {
            // The two weeks it planned have started, so its dates no longer fit: replace it with a fresh draft.
            var replaced = await db.PlanVersions.Where(p => p.Id == id && p.Status == PlanVersionStatus.Proposed)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Replaced), ct);
            if (replaced == 0) return await StaleAsync(db, await RefreshAsync(db, version, ct), ct);
            if (!await db.Jobs.AnyAsync(j => j.Kind == JobKinds.Plan && j.AspirationId == version.AspirationId && j.State == JobState.Pending, ct))
                await jobs.EnqueueAsync(JobKinds.Plan, version.UserId, new Dictionary<string, Guid> { ["aspirationId"] = version.AspirationId }, version.AspirationId, ct);
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
        await db.PlanVersions.Where(p => p.AspirationId == version.AspirationId && p.Id != id && p.Status == PlanVersionStatus.Approved)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Superseded), ct);
        version = await RefreshAsync(db, version, ct);
        version.Number = (await db.PlanVersions.Where(p => p.AspirationId == version.AspirationId).MaxAsync(p => p.Number, ct) ?? 0) + 1;

        foreach (var a in JsonNode.Parse(version.TwoWeeksJson)!.AsArray().Select(a => a!.AsObject()))
        {
            // Linked by the IDs the draft stored, never by title or label.
            db.ScheduledActions.Add(new ScheduledAction
            {
                Id = Guid.NewGuid(), UserId = version.UserId, AspirationId = version.AspirationId, PlanVersionId = id,
                HabitId = Ref(a, "habitId"), TaskId = Ref(a, "taskId"), SlotId = Ref(a, "slotId")!.Value,
                Date = PlanRules.ParseDate(HandOff.Str(a, "date"))!.Value, Title = HandOff.Str(a, "action")!,
                Minutes = (int)HandOff.Num(a, "minutes")!.Value,
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Results.Ok(await ViewAsync(db, version, ct));
    }

    private static Guid? Ref(JsonObject a, string name) => Guid.TryParse(HandOff.Str(a, name), out var g) ? g : null;

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

    public sealed record MilestoneView(Guid Id, DateOnly Date, string Title, string ReachedWhen, string Kind, string State);
    public sealed record HabitView(Guid Id, string Title, int TimesPerWeek, int Minutes, IEnumerable<string?> Slots);
    public sealed record TaskView(Guid Id, string Title, int Minutes, DateOnly? Due, string? Milestone);
    public sealed record PlanVersionView(Guid Id, Guid AspirationId, string ShortName, string Hue, int? Number, string Status, string Reason,
        string Rationale, string Ramp, JsonNode? Assumptions, DateOnly StartDate, IEnumerable<MilestoneView> Milestones,
        IEnumerable<HabitView> Habits, IEnumerable<TaskView> Tasks, JsonNode? TwoWeeks);

    private static async Task<PlanVersionView> ViewAsync(WaypointDb db, PlanVersion v, CancellationToken ct)
    {
        var aspiration = await db.Aspirations.FirstAsync(a => a.Id == v.AspirationId, ct);
        var slots = await db.Slots.ToDictionaryAsync(s => s.Id, s => s.Label, ct);
        var milestones = await db.Milestones.Where(m => m.PlanVersionId == v.Id).OrderBy(m => m.Date).ToListAsync(ct);
        var habits = await db.Habits.Where(h => h.PlanVersionId == v.Id).ToListAsync(ct);
        var tasks = await db.PlanTasks.Where(t => t.PlanVersionId == v.Id).OrderBy(t => t.DueDate).ToListAsync(ct);
        return new PlanVersionView(v.Id, v.AspirationId, aspiration.ShortName, aspiration.Hue, v.Number, v.Status, v.Reason, v.Rationale, v.Ramp,
            JsonNode.Parse(v.AssumptionsJson), v.StartDate,
            milestones.Select(m => new MilestoneView(m.Id, m.Date, m.Title, m.ReachedWhen, m.Kind, m.State)).ToList(),
            habits.Select(h => new HabitView(h.Id, h.Title, h.TimesPerWeek, h.Minutes,
                h.SlotIds.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => slots.GetValueOrDefault(Guid.Parse(s))).ToList())).ToList(),
            tasks.Select(t => new TaskView(t.Id, t.Title, t.Minutes, t.DueDate, milestones.FirstOrDefault(m => m.Id == t.MilestoneId)?.Title)).ToList(),
            JsonNode.Parse(v.TwoWeeksJson));
    }
}
