using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public sealed record CheckInRequest(Guid? ScheduledActionId, string? ClientId, string? State, int? Minutes, string[]? Reasons, string? Note, DateTime? TappedAt);

public static class CheckIns
{
    /// <summary>The Coach's reply waits this long, so a quick Undo cancels it.</summary>
    public static readonly TimeSpan ReplyDelay = TimeSpan.FromSeconds(5);
    public const string EchoCard = "check_in";

    public static object View(CheckIn c) => new
    {
        c.Id, c.ScheduledActionId, c.State, Minutes = c.MinutesSpent,
        Reasons = (c.Reasons ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries),
        c.Note, TappedAt = DateTime.SpecifyKind(c.TappedAt, DateTimeKind.Utc),
    };

    public static void MapCheckIns(this WebApplication app)
    {
        var g = app.MapGroup("/").RequireAuthorization();
        g.MapPut("/check-ins", Put);
        g.MapDelete("/scheduled-actions/{id:guid}/check-in", Undo);
    }

    private static string EchoText(string state, int? minutes, string title) => state switch
    {
        CheckInState.Done => $"Done · {title}",
        CheckInState.Partly => $"Partly · {minutes} min · {title}",
        _ => $"Skipped · {title}",
    };

    private static string EchoJson(string state, int? minutes, string title) =>
        JsonSerializer.Serialize(new { state, minutes, title }, JsonSerializerOptions.Web);

    /// <summary>The chip codes the app offers; free text belongs in the note.</summary>
    public static readonly HashSet<string> ReasonCodes = new()
    {
        "great", "ok", "hard", "tired", "no_time", "interrupted", "unwell", "hurt", "not_feeling_it", "something_else", "not_today",
    };
    public const int MaxReasons = 6;
    public const int MaxNote = 1000;

    private static async Task<IResult> Put(CheckInRequest req, WaypointDb db, CurrentUser me, JobQueue jobs, TimeProvider clock, CancellationToken ct)
    {
        try { return await PutOnce(req, db, me, jobs, clock, ct); }
        catch (DbUpdateException)
        {
            // A concurrent PUT won the unique index: reload and take the "existing" path.
            db.ChangeTracker.Clear();
            return await PutOnce(req, db, me, jobs, clock, ct);
        }
    }

    private static async Task<IResult> PutOnce(CheckInRequest req, WaypointDb db, CurrentUser me, JobQueue jobs, TimeProvider clock, CancellationToken ct)
    {
        if (req.Reasons is not null && (req.Reasons.Length > MaxReasons || req.Reasons.Any(r => r is null || !ReasonCodes.Contains(r)))
            || req.Note is { Length: > MaxNote })
            return Results.BadRequest();
        if (req.ScheduledActionId is null || string.IsNullOrWhiteSpace(req.ClientId) || req.ClientId.Length > 64
            || !CheckInState.IsValid(req.State) || req.TappedAt is null
            || (req.State == CheckInState.Partly && req.Minutes is not > 0)
            || req.Minutes is < 0 or > 1440)
            return Results.BadRequest();

        // A resend of a tap already stored changes nothing.
        var same = await db.CheckIns.FirstOrDefaultAsync(c => c.ClientId == req.ClientId, ct);
        if (same is not null) return Results.Ok(View(same));

        var action = await db.ScheduledActions.FirstOrDefaultAsync(a => a.Id == req.ScheduledActionId, ct);
        if (action is null) return Results.NotFound();

        var tapped = req.TappedAt.Value.ToUniversalTime();
        var minutes = req.State == CheckInState.Partly ? req.Minutes : null;
        var reasons = req.Reasons is { Length: > 0 } ? string.Join(',', req.Reasons.Distinct()) : null;
        var note = string.IsNullOrWhiteSpace(req.Note) ? null : req.Note.Trim();
        var existing = await db.CheckIns.FirstOrDefaultAsync(c => c.ScheduledActionId == action.Id, ct);

        if (existing is not null)
        {
            // Changed twice keeps the latest tap: an older tap arriving late is ignored.
            if (tapped <= existing.TappedAt) return Results.Ok(View(existing));
            var echo = await db.ChatMessages.FirstAsync(m => m.Id == existing.EchoMessageId, ct);
            echo.ClientId = req.ClientId;
            echo.Text = EchoText(req.State!, minutes, action.Title);
            echo.CardJson = EchoJson(req.State!, minutes, action.Title);
            existing.ClientId = req.ClientId;
            existing.State = req.State!;
            existing.MinutesSpent = minutes;
            existing.Reasons = reasons;
            existing.Note = note;
            existing.TappedAt = tapped;
            // The old reply answered the old state: it goes, and the new state gets its own.
            db.ChatMessages.RemoveRange(await db.ChatMessages.Where(m => m.InReplyToId == echo.Id).ToListAsync(ct));
            await db.SaveChangesAsync(ct);
            if (!await db.Jobs.AnyAsync(j => j.Kind == JobKinds.CheckInReply && j.State == JobState.Pending
                    && j.PayloadJson.Contains(existing.Id.ToString()), ct))
                await jobs.EnqueueAsync(JobKinds.CheckInReply, me.Id!.Value, new Dictionary<string, Guid> { ["checkInId"] = existing.Id }, action.AspirationId, ReplyDelay, ct);
            return Results.Ok(View(existing));
        }

        var message = new ChatMessage
        {
            Id = Guid.NewGuid(), UserId = me.Id!.Value, Role = "user", Text = EchoText(req.State!, minutes, action.Title),
            AspirationId = action.AspirationId, ClientId = req.ClientId, CreatedAt = clock.GetUtcNow().UtcDateTime,
            Card = EchoCard, CardJson = EchoJson(req.State!, minutes, action.Title),
        };
        var checkIn = new CheckIn
        {
            Id = Guid.NewGuid(), UserId = me.Id.Value, ScheduledActionId = action.Id, ClientId = req.ClientId, State = req.State!,
            MinutesSpent = minutes, Reasons = reasons, Note = note, TappedAt = tapped, EchoMessageId = message.Id,
        };
        db.ChatMessages.Add(message);
        db.CheckIns.Add(checkIn);
        jobs.Add(JobKinds.CheckInReply, me.Id.Value, new Dictionary<string, Guid> { ["checkInId"] = checkIn.Id }, action.AspirationId, ReplyDelay);
        await db.SaveChangesAsync(ct);
        return Results.Ok(View(checkIn));
    }

    /// <summary>Undo: the Check-in, its echo in the chat and any reply to it go. A reply job still pending finds nothing and does nothing.</summary>
    private static async Task<IResult> Undo(Guid id, WaypointDb db, CancellationToken ct)
    {
        var checkIn = await db.CheckIns.FirstOrDefaultAsync(c => c.ScheduledActionId == id, ct);
        if (checkIn is null) return Results.NoContent();
        var messages = await db.ChatMessages.Where(m => m.Id == checkIn.EchoMessageId || m.InReplyToId == checkIn.EchoMessageId).ToListAsync(ct);
        db.ChatMessages.RemoveRange(messages);
        db.CheckIns.Remove(checkIn);
        await db.SaveChangesAsync(ct);
        return Results.NoContent();
    }
}

/// <summary>Writes the Coach's reply to a Check-in: the light model with no note, the heavy one with a note.</summary>
public sealed class CheckInReplies(WaypointDb db, ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock)
{
    private const int WindowSize = 20;

    public const string SystemPrompt =
        "You are the Coach in Waypoint. The user just checked in on an Action. Reply in one or two warm, plain sentences, " +
        "without blame or pressure. Never invent facts about the user's progress. " + MissTone.Rules;

    /// <summary>What a skip is answered with first, by the Aspiration's chosen reaction.</summary>
    public static string SkipReaction(string onSkip) => onSkip switch
    {
        "ask_why" => "The user wants the Coach to ask what got in the way: acknowledge the skip in one line, then ask what got in the way, with an easy out.",
        "quiet_replan" => "The user wants the Coach to quietly adjust: acknowledge the skip in one line and say you will quietly fit the Plan around it, without asking anything.",
        _ => "The user wants the Coach to gently check in: acknowledge the skip in one line, then gently check in on how they are, with an easy out.",
    };

    public async Task ReplyAsync(Guid checkInId, CancellationToken ct)
    {
        var checkIn = await db.CheckIns.FirstOrDefaultAsync(c => c.Id == checkInId, ct);
        if (checkIn is null) return; // undone
        var echo = await db.ChatMessages.FirstOrDefaultAsync(m => m.Id == checkIn.EchoMessageId, ct);
        if (echo is null || await db.ChatMessages.AnyAsync(m => m.InReplyToId == echo.Id, ct)) return;

        var action = await db.ScheduledActions.FirstOrDefaultAsync(a => a.Id == checkIn.ScheduledActionId, ct);
        var skipped = checkIn.State == CheckInState.Skipped;
        var system = SystemPrompt;
        MakeUpOffer? offer = null;
        if (skipped && action is not null)
        {
            var aspiration = await db.Aspirations.FirstOrDefaultAsync(a => a.Id == action.AspirationId, ct);
            system += " " + SkipReaction(aspiration?.OnSkip ?? "gentle_check");
            offer = await MakeUps.FindAsync(db, action, clock, ct);
            system += offer is null
                ? " There is nothing to move: the next step is simply the next planned session. Do not offer to reschedule."
                : $" The app found a free Slot on {offer.Date:dddd} ({offer.SlotLabel}): your one next step is to offer moving the session there, if they want.";
        }

        var kind = checkIn.Note is null ? CoachCallKind.CheckInReplyNoNote : CoachCallKind.CheckInReplyWithNote;
        var choice = options.Value.For(kind);
        var window = await db.ChatMessages.Where(m => m.Seq <= echo.Seq).OrderByDescending(m => m.Seq).Take(WindowSize)
            .OrderBy(m => m.Seq).ToListAsync(ct);
        var turns = window.SkipWhile(m => m.Role != "user").Select(m => new CoachTurn(m.Role == "user" ? "user" : "assistant", m.Text)).ToList();
        if (checkIn.Note is not null) turns[^1] = turns[^1] with { Text = $"{turns[^1].Text}\n{checkIn.Note}" };

        var reply = new StringBuilder();
        await foreach (var chunk in model.StreamAsync(new CoachRequest(kind, choice.Provider, choice.Model, system, turns), ct))
            reply.Append(chunk);
        if (reply.Length == 0) throw new CoachUnavailableException("empty_reply");
        // The Coach is late rather than blaming: a reply about a miss that does fails the job, which retries.
        if (checkIn.State != CheckInState.Done && MissTone.Violates(reply.ToString())) throw new CoachUnavailableException("reply_tone");

        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(), UserId = echo.UserId, Role = "coach", Text = reply.ToString(), AspirationId = echo.AspirationId,
            InReplyToId = echo.Id, CreatedAt = clock.GetUtcNow().UtcDateTime,
            Card = offer is null ? null : "make_up",
            CardJson = offer is null ? null : JsonSerializer.Serialize(new { scheduledActionId = offer.ScheduledActionId, date = offer.Date.ToString("yyyy-MM-dd"), slotId = offer.SlotId }, JsonSerializerOptions.Web),
        });
        await db.SaveChangesAsync(ct);
    }
}
