using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public sealed record PauseRequest(string? ClientId, Guid? AspirationId, Guid? ActionId, string? EndsOn, bool? Open);
public sealed record ReturnRequest(string? Choice);

/// <summary>
/// Pause: a stretch the user steps back from one Aspiration, one Action or everything. It starts at once with no approval;
/// the Actions inside it are set aside (neither done nor missed).
/// </summary>
public static class Pauses
{
    public const string CatchUp = "catch_up";
    public const string ShiftDates = "shift_dates";
    public const string WelcomeBackCard = "welcome_back";
    /// <summary>A Pause up to this long suggests Catch-up on the way back; a longer one suggests shifting the dates.</summary>
    public const int CatchUpMaxDays = 14;
    /// <summary>A Pause has to cover at least this many days to earn a "welcome back" and an easier first week; "Not today" doesn't.</summary>
    public const int WelcomeMinDays = 2;

    public static void MapPauses(this WebApplication app)
    {
        var g = app.MapGroup("/").RequireAuthorization();
        g.MapGet("/pauses", async (WaypointDb db, TimeProvider clock, CancellationToken ct) =>
        {
            var now = clock.GetUtcNow().UtcDateTime;
            return Results.Ok((await db.Pauses.OrderBy(p => p.StartsAt).ToListAsync(ct)).Where(p => IsActive(p, now)).Select(View));
        });
        g.MapPost("/pauses", Start);
        g.MapPost("/pauses/{id:guid}/resume", Resume);
        // The phone may resume a Pause it started offline, before it knew the server's ID: it names it by its own client ID.
        g.MapPost("/pauses/by-client/{clientId}/resume", async (string clientId, WaypointDb db, JobQueue jobs, TimeProvider clock, CancellationToken ct) =>
            await db.Pauses.Where(p => p.ClientId == clientId).Select(p => p.Id).FirstOrDefaultAsync(ct) is var id && id != Guid.Empty
                ? await Resume(id, db, jobs, clock, ct) : Results.NotFound());
        g.MapPost("/pauses/{id:guid}/return", Return);
        g.MapPost("/me/seen", async (WaypointDb db, JobQueue jobs, TimeProvider clock, CancellationToken ct) =>
        {
            await Silence.TouchAsync(db, jobs, clock, ct);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
    }

    /// <summary>When the Pause stops: its own end or the moment the user resumed, whichever came first.</summary>
    public static DateTime? EffectiveEnd(Pause p) =>
        p.EndsAt is { } e && p.ResumedAt is { } r ? (e < r ? e : r) : p.EndsAt ?? p.ResumedAt;

    public static bool IsActive(Pause p, DateTime now) => p.StartsAt <= now && (EffectiveEnd(p) is not { } end || end > now);

    /// <summary>Does it hide Actions of this Aspiration: a Pause of everything or of that Aspiration, not of one Action.</summary>
    public static bool CoversAspiration(Pause p, Guid aspirationId) =>
        p.ScheduledActionId is null && (p.AspirationId is null || p.AspirationId == aspirationId);

    /// <summary>Days the Pause ran (or runs so far).</summary>
    public static int Days(Pause p, DateTime now) =>
        (DateOnly.FromDateTime(EffectiveEnd(p) is { } e && e < now ? e : now).DayNumber - DateOnly.FromDateTime(p.StartsAt).DayNumber);

    /// <summary>A scheduled Action is set aside when a Pause covers its day and it has no Check-in.</summary>
    public static bool IsSetAside(IEnumerable<Pause> pauses, ScheduledAction a, bool hasCheckIn)
    {
        if (hasCheckIn) return false;
        foreach (var p in pauses)
        {
            if (DateOnly.FromDateTime(p.StartsAt) > a.Date) continue;
            if (EffectiveEnd(p) is { } end && DateOnly.FromDateTime(end) <= a.Date) continue;
            if (p.ScheduledActionId is { } one ? one == a.Id : CoversAspiration(p, a.AspirationId)) return true;
        }
        return false;
    }

    public static async Task<bool> IsSetAsideAsync(WaypointDb db, ScheduledAction a, bool hasCheckIn, CancellationToken ct) =>
        !hasCheckIn && IsSetAside(await db.Pauses.ToListAsync(ct), a, false);

    /// <summary>The Pauses covering an Aspiration right now (not one Action).</summary>
    public static async Task<bool> IsPausedNowAsync(WaypointDb db, DateTime now, Guid? aspirationId, CancellationToken ct) =>
        (await db.Pauses.ToListAsync(ct)).Any(p => IsActive(p, now) && p.ScheduledActionId is null && (aspirationId is null ? p.AspirationId is null : CoversAspiration(p, aspirationId.Value)));

    public static object View(Pause p) => new
    {
        p.Id, p.ClientId, p.AspirationId, ActionId = p.ScheduledActionId,
        StartsAt = DateTime.SpecifyKind(p.StartsAt, DateTimeKind.Utc),
        EndsAt = p.EndsAt is { } e ? DateTime.SpecifyKind(e, DateTimeKind.Utc) : (DateTime?)null,
        p.StartedBy, p.ReturnChoice,
    };

    private static async Task<IResult> Start(PauseRequest req, WaypointDb db, CurrentUser me, JobQueue jobs, TimeProvider clock, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.ClientId) || req.ClientId.Length > 64) return Results.BadRequest();
        var same = await db.Pauses.FirstOrDefaultAsync(p => p.ClientId == req.ClientId, ct);
        if (same is not null) return Results.Ok(View(same));
        if ((req.Open == true && req.EndsOn is not null) || (req.AspirationId is not null && req.ActionId is not null)) return Results.BadRequest();

        var now = clock.GetUtcNow().UtcDateTime;
        if (!TryEnd(req.Open == true, req.EndsOn, now, out var endsAt)) return Results.BadRequest();
        if (req.AspirationId is { } aid && !await db.Aspirations.AnyAsync(a => a.Id == aid, ct)) return Results.NotFound();
        if (req.ActionId is { } sid && !await db.ScheduledActions.AnyAsync(a => a.Id == sid, ct)) return Results.NotFound();

        var pause = new Pause
        {
            Id = Guid.NewGuid(), UserId = me.Id!.Value, ClientId = req.ClientId, AspirationId = req.AspirationId, ScheduledActionId = req.ActionId,
            StartsAt = now, EndsAt = endsAt, StartedBy = PauseStarters.User,
        };
        db.Pauses.Add(pause);
        await StartAsync(db, jobs, pause, ct);
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(pause));
    }

    /// <summary>"Not today" ends at midnight; a last day ends after that day; open ends only when the user resumes.</summary>
    public static bool TryEnd(bool open, string? endsOn, DateTime now, out DateTime? endsAt)
    {
        var today = DateOnly.FromDateTime(now);
        endsAt = today.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        if (open) endsAt = null;
        else if (endsOn is not null)
        {
            if (!DateOnly.TryParseExact(endsOn, "yyyy-MM-dd", out var last) || last < today) return false;
            endsAt = last.AddDays(1).ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        }
        return true;
    }

    /// <summary>The user's return (a Check-in, a chat message or an app open) ends a Pause the Coach started, and queues the one welcome back.</summary>
    public static async Task EndCoachPausesAsync(WaypointDb db, JobQueue jobs, DateTime now, CancellationToken ct)
    {
        foreach (var p in (await db.Pauses.ToListAsync(ct)).Where(p => p.StartedBy == PauseStarters.Coach && p.ScheduledActionId is null && IsActive(p, now)))
        {
            p.ResumedAt = now;
            await RecordCoveredAsync(db, p, ct);
            jobs.Add(JobKinds.WelcomeBack, p.UserId, new Dictionary<string, Guid> { ["pauseId"] = p.Id });
        }
    }

    /// <summary>Marks the Actions it hides as changed for the phone, and books the way back when it has an end.</summary>
    public static async Task StartAsync(WaypointDb db, JobQueue jobs, Pause pause, CancellationToken ct)
    {
        await RecordCoveredAsync(db, pause, ct);
        if (pause.EndsAt is { } end && pause.ScheduledActionId is null)
            jobs.Add(JobKinds.WelcomeBack, pause.UserId, new Dictionary<string, Guid> { ["pauseId"] = pause.Id }, delay: end - pause.StartsAt);
    }

    private static async Task RecordCoveredAsync(WaypointDb db, Pause pause, CancellationToken ct)
    {
        var from = DateOnly.FromDateTime(pause.StartsAt);
        foreach (var a in (await db.ScheduledActions.Where(a => a.Date >= from).ToListAsync(ct)).Where(a => pause.ScheduledActionId is { } one ? one == a.Id : CoversAspiration(pause, a.AspirationId)))
            db.Record("scheduled_action", pause.UserId, a.Id);
    }

    private static async Task<IResult> Resume(Guid id, WaypointDb db, JobQueue jobs, TimeProvider clock, CancellationToken ct)
    {
        var pause = await db.Pauses.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pause is null) return Results.NotFound();
        var now = clock.GetUtcNow().UtcDateTime;
        if (IsActive(pause, now))
        {
            pause.ResumedAt = now;
            await RecordCoveredAsync(db, pause, ct);
            if (pause.ScheduledActionId is null)
                jobs.Add(JobKinds.WelcomeBack, pause.UserId, new Dictionary<string, Guid> { ["pauseId"] = pause.Id });
            await Silence.TouchAsync(db, jobs, clock, ct);
            await db.SaveChangesAsync(ct);
        }
        return Results.Ok(View(pause));
    }

    /// <summary>The user's answer to the "welcome back": Catch-up or shift the dates.</summary>
    private static async Task<IResult> Return(Guid id, ReturnRequest req, WaypointDb db, CancellationToken ct)
    {
        if (req.Choice is not (CatchUp or ShiftDates)) return Results.BadRequest();
        var pause = await db.Pauses.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (pause is null) return Results.NotFound();
        pause.ReturnChoice = req.Choice;
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(pause));
    }
}
