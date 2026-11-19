using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// How an Aspiration ends or stops mattering, as the user decides it: maintenance (habits go on, no Milestones, no Projection),
/// a new chapter out of it, reached, and Delete. The statuses are "active", "maintenance" and "closed".
/// </summary>
public sealed record ReachedRequest(bool? KeepUp);

public static class AspirationLifecycle
{
    public const string Maintenance = "maintenance";
    public const string Closed = "closed";
    public const string MaintenanceReason = "maintenance";
    public const string NewChapterReason = "new_chapter";
    private const int MaxActiveAspirations = 3;
    /// <summary>Consistency looks at this many of the last weeks.</summary>
    public const int ConsistencyWeeks = 8;

    public static void MapAspirationLifecycle(this WebApplication app)
    {
        var g = app.MapGroup("/aspirations").RequireAuthorization();
        g.MapPost("/{id:guid}/maintenance", MoveToMaintenance);
        g.MapPost("/{id:guid}/reached", MarkReached);
        g.MapPost("/{id:guid}/new-chapter", NewChapter);
        g.MapDelete("/{id:guid}", Delete);
    }

    /// <summary>
    /// The user confirmed moving to maintenance, which approves the maintenance Plan the Coach would propose: the same habits with no
    /// Milestones. The server builds it, so the Plan is the same whether or not the model is reachable. The Actions already laid out stay.
    /// </summary>
    private static async Task<IResult> MoveToMaintenance(Guid id, WaypointDb db, JobQueue jobs, TimeProvider clock, CancellationToken ct)
    {
        var aspiration = await db.Aspirations.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (aspiration is null) return Results.NotFound();
        if (aspiration.Status == Maintenance) return Results.Ok(new { aspirationId = id, status = Maintenance });
        if (aspiration.Status != "active") return Results.Conflict(new { error = "not_active" });
        var now = clock.GetUtcNow().UtcDateTime;
        var current = await db.PlanVersions.FirstOrDefaultAsync(p => p.AspirationId == id && p.Status == PlanVersionStatus.Approved, ct);
        if (current is null) return Results.Conflict(new { error = "no_plan" });
        var version = new PlanVersion
        {
            Id = Guid.NewGuid(), UserId = aspiration.UserId, AspirationId = id, Status = PlanVersionStatus.Proposed, Rationale = "",
            Reason = MaintenanceReason, Ramp = current.Ramp, AssumptionsJson = current.AssumptionsJson, TwoWeeksJson = "[]",
            StartDate = DateOnly.FromDateTime(now).AddDays(1), CreatedAt = now,
        };
        db.PlanVersions.Add(version);
        foreach (var h in await db.Habits.Where(h => h.PlanVersionId == current.Id).ToListAsync(ct))
            db.Habits.Add(new Habit
            {
                Id = Guid.NewGuid(), UserId = h.UserId, PlanVersionId = version.Id, Title = h.Title, TimesPerWeek = h.TimesPerWeek, Minutes = h.Minutes,
                SlotIds = h.SlotIds, AnchorId = h.AnchorId, TargetMinutes = h.TargetMinutes, FullTimesPerWeek = h.FullTimesPerWeek, PrepFor = h.PrepFor,
                Since = h.Since, ReminderOn = h.ReminderOn, StopOffered = h.StopOffered, StoppedByOffer = h.StoppedByOffer, GuideJson = h.GuideJson,
            });
        await db.SaveChangesAsync(ct);

        var approved = await Plans.Approve(version.Id, db, clock, jobs, ct);
        // Not the status the approval answered with when the Plan had moved on: nothing changed.
        // Approve runs its own transaction, so the undo is here: a refused approval leaves the user's pending proposal as it was
        // and no maintenance version behind.
        if (approved is not Microsoft.AspNetCore.Http.HttpResults.Ok<Plans.PlanVersionView>)
        {
            db.ChangeTracker.Clear();
            await db.PlanVersions.Where(p => p.Id == version.Id && p.Status != PlanVersionStatus.Approved).ExecuteDeleteAsync(ct);
            return approved;
        }
        // A proposal made for the Plan as it was no longer applies.
        await db.PlanVersions.Where(p => p.AspirationId == id && p.Status == PlanVersionStatus.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Replaced), ct);
        aspiration = await db.Aspirations.FirstAsync(a => a.Id == id, ct);
        aspiration.Status = Maintenance;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { aspirationId = id, status = Maintenance, planVersionId = version.Id });
    }

    /// <summary>
    /// Reached: the user keeps it up in maintenance, or closes it. Closing keeps every row as history, but nothing more is asked:
    /// the Actions still to come, a proposal still waiting and the jobs that would speak of it all go.
    /// </summary>
    private static async Task<IResult> MarkReached(Guid id, ReachedRequest req, WaypointDb db, JobQueue jobs, TimeProvider clock, CancellationToken ct)
    {
        if (req.KeepUp is not { } keepUp) return Results.BadRequest();
        if (keepUp) return await MoveToMaintenance(id, db, jobs, clock, ct);
        var aspiration = await db.Aspirations.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (aspiration is null) return Results.NotFound();
        if (aspiration.Status == Closed) return Results.Ok(new { aspirationId = id, status = Closed });

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        await db.PlanVersions.Where(p => p.AspirationId == id && p.Status == PlanVersionStatus.Proposed)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PlanVersionStatus.Replaced), ct);
        // Actions already checked in stay; the ones still to come are removed (and the phone is told).
        var coming = await db.ScheduledActions.Where(a => a.AspirationId == id && a.Date >= today && !db.CheckIns.Any(c => c.ScheduledActionId == a.Id)).ToListAsync(ct);
        db.ScheduledActions.RemoveRange(coming);
        foreach (var a in coming) db.Record("scheduled_action", aspiration.UserId, a.Id);
        db.Jobs.RemoveRange(await db.Jobs.Where(j => j.AspirationId == id && j.State == JobState.Pending).ToListAsync(ct));
        aspiration.Status = Closed;
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { aspirationId = id, status = Closed });
    }

    /// <summary>
    /// Delete: hard-deleted at once, everything under the Aspiration. The Plan versions, Milestones, habits, tasks, Actions, Check-ins and
    /// readings go with it by cascade; the rows that only point at it by ID are removed here. The phone is told so it drops them too.
    /// </summary>
    private static async Task<IResult> Delete(Guid id, WaypointDb db, CancellationToken ct)
    {
        var aspiration = await db.Aspirations.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (aspiration is null) return Results.NotFound();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var versions = await db.PlanVersions.Where(p => p.AspirationId == id).Select(p => p.Id).ToListAsync(ct);
        var actionIds = await db.ScheduledActions.Where(a => a.AspirationId == id).Select(a => a.Id).ToListAsync(ct);
        var milestoneIds = await db.Milestones.Where(m => versions.Contains(m.PlanVersionId)).Select(m => m.Id).ToListAsync(ct);
        // An Anchor belongs to the Aspiration whose habits use it, unless another Aspiration's habits do too.
        var mine = await db.Habits.Where(h => versions.Contains(h.PlanVersionId) && h.AnchorId != null).Select(h => h.AnchorId!.Value).Distinct().ToListAsync(ct);
        var shared = await db.Habits.Where(h => !versions.Contains(h.PlanVersionId) && h.AnchorId != null && mine.Contains(h.AnchorId.Value))
            .Select(h => h.AnchorId!.Value).Distinct().ToListAsync(ct);
        var anchors = mine.Except(shared).ToList();

        var messages = await db.ChatMessages.Where(m => m.AspirationId == id).ToListAsync(ct);
        var messageIds = messages.Select(m => m.Id).ToList();
        messages.AddRange(await db.ChatMessages.Where(m => m.AspirationId != id && m.InReplyToId != null && messageIds.Contains(m.InReplyToId.Value)).ToListAsync(ct));
        db.ChatMessages.RemoveRange(messages);

        var interviews = await db.Interviews.Where(i => i.AspirationId == id).Select(i => i.Id).ToListAsync(ct);
        await db.InterviewMessages.Where(m => interviews.Contains(m.InterviewId)).ExecuteDeleteAsync(ct);
        await db.Interviews.Where(i => i.AspirationId == id).ExecuteDeleteAsync(ct);
        await db.Jobs.Where(j => j.AspirationId == id).ExecuteDeleteAsync(ct);
        await db.Pauses.Where(p => p.AspirationId == id || (p.ScheduledActionId != null && actionIds.Contains(p.ScheduledActionId.Value))).ExecuteDeleteAsync(ct);

        // Habits and Check-ins reach the phone through their Actions and Plan versions, so those tombstones cover them.
        db.Record("aspiration", aspiration.UserId, id);
        foreach (var a in actionIds) db.Record("scheduled_action", aspiration.UserId, a);
        foreach (var m in milestoneIds) db.Record("milestone", aspiration.UserId, m);
        foreach (var v in versions) db.Record("plan_version", aspiration.UserId, v);
        await db.SaveChangesAsync(ct);

        await db.Aspirations.Where(a => a.Id == id).ExecuteDeleteAsync(ct); // cascades to Plan versions, Actions, Check-ins and readings
        await db.Anchors.Where(a => anchors.Contains(a.Id)).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }

    /// <summary>
    /// A new chapter: a short Interview on an Aspiration in maintenance. Confirming it makes the Aspiration active again and drafts new
    /// Milestones. It takes one of the three active places, so it is refused (409) when they are full.
    /// </summary>
    private static async Task<IResult> NewChapter(Guid id, WaypointDb db, CurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        var aspiration = await db.Aspirations.FirstOrDefaultAsync(a => a.Id == id, ct);
        if (aspiration is null) return Results.NotFound();
        if (aspiration.Status != Maintenance) return Results.Conflict(new { error = "not_in_maintenance" });
        // One Interview at a time: an unfinished one is picked back up.
        var open = await db.Interviews.Where(x => x.AspirationId == id && x.Status != InterviewStatus.Confirmed).OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (open is not null) return Results.Ok(await Interviews.ViewAsync(db, open, ct));
        if (await db.Aspirations.CountAsync(a => a.Status == "active", ct) >= MaxActiveAspirations)
            return Results.Conflict(new { error = "too_many_active_aspirations" });
        var interview = new Interview
        {
            Id = Guid.NewGuid(), UserId = me.Id!.Value, AspirationId = id, OwnWords = aspiration.OwnWords, Status = InterviewStatus.InProgress,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Interviews.Add(interview);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/interviews/{interview.Id}", await Interviews.ViewAsync(db, interview, ct));
    }
}
