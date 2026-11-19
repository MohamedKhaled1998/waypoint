using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public enum UnpromptedKind
{
    /// <summary>A silence nudge (including the auto-Pause line): counts, but ignores Quiet's spacing.</summary>
    SilenceNudge,
    /// <summary>The one "welcome back" after a Pause.</summary>
    WelcomeBack,
    /// <summary>The Coach reaching out about an Aspiration, e.g. "how did the run go?".</summary>
    CoachCheckIn,
}

/// <summary>
/// What an unprompted sender wants to say. <paramref name="ValidUntil"/> is when the message stops being worth sending
/// (null: never stale); a message held through quiet hours past it is dropped.
/// </summary>
public sealed record UnpromptedRequest(UnpromptedKind Kind, Guid? AspirationId, DateTime? ValidUntil = null);

public abstract record GateDecision
{
    private GateDecision() { }
    /// <summary>May go now. Also the permit the messenger needs to post.</summary>
    public sealed record Send : GateDecision;
    /// <summary>May go later: try again at <paramref name="Until"/>.</summary>
    public sealed record Hold(DateTime Until) : GateDecision;
    /// <summary>Never: stale by the time it could go.</summary>
    public sealed record Drop : GateDecision;
}

/// <summary>
/// The one place that decides whether an unprompted Coach message may go now, later, or not at all: Contact frequency
/// across all Aspirations, then quiet hours. Replies, reminders and the weekly refresh prompt never come through here.
/// </summary>
public sealed class ContactGate(WaypointDb db, TimeProvider clock)
{
    public static readonly TimeSpan PriorityRetry = TimeSpan.FromMinutes(1);

    private sealed record Limit(int Cap, TimeSpan Window, TimeSpan Spacing);

    /// <summary>Quiet: a few times a week; Gentle: up to once a day; Chatty: up to three a day. Not yet chosen counts as Gentle.</summary>
    private static Limit LimitFor(string? frequency) => frequency switch
    {
        "Quiet" => new(3, TimeSpan.FromDays(7), TimeSpan.FromDays(2)),
        "Chatty" => new(3, TimeSpan.FromDays(1), TimeSpan.Zero),
        _ => new(1, TimeSpan.FromDays(1), TimeSpan.Zero),
    };

    /// <summary>Kinds of job whose message is waiting to be sent, and so competes for a slot.</summary>
    private static readonly string[] CompetingJobs = [JobKinds.WelcomeBack];

    public async Task<GateDecision> DecideAsync(UnpromptedRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.Users.SingleAsync(ct);
        var limit = LimitFor(user.ContactFrequency);

        var sent = await db.ChatMessages.Where(m => m.Unprompted && m.CreatedAt > now - limit.Window)
            .OrderBy(m => m.CreatedAt).Select(m => m.CreatedAt).ToListAsync(ct);

        // When the cap and the spacing would next allow it.
        var earliest = now;
        if (sent.Count >= limit.Cap) earliest = Max(earliest, sent[sent.Count - limit.Cap] + limit.Window);
        var spaced = request.Kind != UnpromptedKind.SilenceNudge && limit.Spacing > TimeSpan.Zero;
        if (spaced && sent.Count > 0) earliest = Max(earliest, sent[^1] + limit.Spacing);

        var at = AfterQuietHours(earliest, user.QuietStart, user.QuietEnd);
        if (request.ValidUntil is { } valid && at > valid) return new GateDecision.Drop();
        if (at > now) return new GateDecision.Hold(at);

        // A slot is free. If other messages are waiting for it, the Aspiration closest to its Settle-for line goes first.
        var slotsLeft = spaced ? 1 : limit.Cap - sent.Count;
        var mine = await RankAsync(request.AspirationId, ct);
        var waiting = await db.Jobs.Where(j => CompetingJobs.Contains(j.Kind) && j.State == JobState.Pending && j.NextRunAt <= now)
            .Select(j => j.AspirationId).ToListAsync(ct);
        var ahead = 0;
        foreach (var other in waiting)
            if (await RankAsync(other, ct) < mine) ahead++;
        if (ahead >= slotsLeft) return new GateDecision.Hold(now + PriorityRetry);
        return new GateDecision.Send();
    }

    /// <summary>Lower goes first: no Aspiration (the whole person), then the earliest Settle-for date, then picture-only.</summary>
    private async Task<DateOnly> RankAsync(Guid? aspirationId, CancellationToken ct)
    {
        if (aspirationId is null) return DateOnly.MinValue;
        var date = await db.Aspirations.Where(a => a.Id == aspirationId).Select(a => a.SettleForDate).FirstOrDefaultAsync(ct);
        return date ?? DateOnly.MaxValue;
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    /// <summary>The first moment at or after <paramref name="t"/> that is outside quiet hours (UTC, may wrap midnight).</summary>
    public static DateTime AfterQuietHours(DateTime t, string start, string end)
    {
        if (!TimeOnly.TryParse(start, out var s) || !TimeOnly.TryParse(end, out var e) || s == e) return t;
        var tod = TimeOnly.FromDateTime(t);
        var inside = s < e ? tod >= s && tod < e : tod >= s || tod < e;
        if (!inside) return t;
        var day = DateOnly.FromDateTime(t);
        if (s > e && tod >= s) day = day.AddDays(1); // evening part of an overnight span ends tomorrow morning
        return day.ToDateTime(e, DateTimeKind.Utc);
    }
}
