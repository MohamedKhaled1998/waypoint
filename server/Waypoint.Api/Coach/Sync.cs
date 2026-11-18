using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public sealed record MoveRequest(string? ClientId, string? Date, Guid? SlotId);

/// <summary>What the phone pulls after its outbox drains, and the one Plan edit the user may make themselves.</summary>
public static class Sync
{
    public const int PageSize = 500;

    public static void MapSync(this WebApplication app)
    {
        var g = app.MapGroup("/").RequireAuthorization();
        g.MapGet("/sync", Pull);
        g.MapPut("/scheduled-actions/{id:guid}/move", Move);
    }

    /// <summary>
    /// The changes after <paramref name="cursor"/>, one per row (the latest wins), each with the row as it is now or a
    /// deleted mark. The cursor to send next time comes back with them; <c>more</c> says to pull again straight away.
    /// </summary>
    private static async Task<IResult> Pull(long? cursor, WaypointDb db, CancellationToken ct)
    {
        var from = cursor ?? 0;
        var page = await db.Changes.Where(c => c.Seq > from).OrderBy(c => c.Seq).Take(PageSize + 1).ToListAsync(ct);
        var more = page.Count > PageSize;
        if (more) page.RemoveAt(PageSize);
        var next = page.Count == 0 ? from : page[^1].Seq;
        var latest = page.GroupBy(c => (c.Kind, c.EntityId)).Select(grp => grp.OrderBy(c => c.Seq).Last()).OrderBy(c => c.Seq).ToList();

        var actionIds = latest.Where(c => c.Kind == "scheduled_action").Select(c => c.EntityId).ToList();
        var messageIds = latest.Where(c => c.Kind == "chat_message").Select(c => c.EntityId).ToList();
        var milestoneIds = latest.Where(c => c.Kind == "milestone").Select(c => c.EntityId).ToList();
        var versionIds = latest.Where(c => c.Kind == "plan_version").Select(c => c.EntityId).ToList();

        var slots = await db.Slots.ToListAsync(ct);
        var actions = await db.ScheduledActions.Where(a => actionIds.Contains(a.Id)).ToDictionaryAsync(a => a.Id, ct);
        var checkIns = await db.CheckIns.Where(c => actionIds.Contains(c.ScheduledActionId)).ToDictionaryAsync(c => c.ScheduledActionId, ct);
        var messages = await db.ChatMessages.Where(m => messageIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var answered = (await db.ChatMessages.Where(m => m.InReplyToId != null && messageIds.Contains(m.InReplyToId.Value))
            .Select(m => m.InReplyToId!.Value).ToListAsync(ct)).ToHashSet();
        var milestones = await db.Milestones.Where(m => milestoneIds.Contains(m.Id)).ToDictionaryAsync(m => m.Id, ct);
        var versions = await db.PlanVersions.Where(p => versionIds.Contains(p.Id)).ToDictionaryAsync(p => p.Id, ct);

        object? Data(Change c) => c.Kind switch
        {
            "scheduled_action" => actions.TryGetValue(c.EntityId, out var a)
                ? Plans.ActionView(a, slots.FirstOrDefault(s => s.Id == a.SlotId), checkIns.GetValueOrDefault(a.Id)) : null,
            "chat_message" => messages.TryGetValue(c.EntityId, out var m) ? CoachChat.View(m, answered.Contains(m.Id)) : null,
            "milestone" => milestones.TryGetValue(c.EntityId, out var ms)
                ? new Plans.MilestoneView(ms.Id, ms.Date, ms.Title, ms.ReachedWhen, ms.Kind, ms.State) : null,
            "plan_version" => versions.TryGetValue(c.EntityId, out var v)
                ? new { v.Id, v.AspirationId, v.Number, v.Status } : null,
            _ => null,
        };

        var changes = latest.Select(c =>
        {
            var data = c.Deleted ? null : Data(c);
            return new { c.Kind, Id = c.EntityId, Deleted = data is null, Data = data };
        }).ToList();
        return Results.Ok(new
        {
            Cursor = next, More = more, Changes = changes,
            Slots = slots.Select(s => new { s.Id, s.Label, s.Days, s.Start, s.End }),
        });
    }

    private static readonly string[] Weekdays = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];

    /// <summary>The user moves one Action to a date and Slot they choose; the Plan itself is untouched.</summary>
    private static async Task<IResult> Move(Guid id, MoveRequest req, WaypointDb db, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId) || req.ClientId.Length > 64 || req.SlotId is null
            || !DateOnly.TryParseExact(req.Date, "yyyy-MM-dd", out var date))
            return Results.BadRequest();
        var action = await db.ScheduledActions.FirstOrDefaultAsync(a => a.Id == id, ct);
        var slot = await db.Slots.FirstOrDefaultAsync(s => s.Id == req.SlotId, ct);
        if (action is null || slot is null) return Results.NotFound();
        // A resend of any move already applied changes nothing, so a replay can't undo a newer move.
        var applied = (action.MoveClientIds ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!applied.Contains(req.ClientId))
        {
            if (!slot.Days.Split(',', StringSplitOptions.RemoveEmptyEntries).Contains(Weekdays[(int)date.DayOfWeek])) return Results.BadRequest();
            action.Date = date;
            action.SlotId = slot.Id;
            action.MoveClientIds = string.Join(',', applied.TakeLast(9).Append(req.ClientId));
            await db.SaveChangesAsync(ct);
        }
        var checkIn = await db.CheckIns.FirstOrDefaultAsync(c => c.ScheduledActionId == id, ct);
        return Results.Ok(Plans.ActionView(action, slot, checkIn));
    }
}
