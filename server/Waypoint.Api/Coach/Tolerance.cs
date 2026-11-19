using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Data;
using Waypoint.Api.Push;

namespace Waypoint.Api.Coach;

/// <summary>
/// Tolerance bands, measured per Aspiration over the last two weeks at the Weekly refresh. Done and partly both
/// count as done; the Coach never decides a band, it only phrases it.
/// </summary>
public static class Tolerance
{
    public enum Band { Absorb, EaseBack, Rethink }

    /// <summary>Absorb at 70% or more; under 70% eases back.</summary>
    public const int AbsorbPercent = 70;
    /// <summary>Under 30% in each of two weeks in a row is a Re-think.</summary>
    public const int RethinkPercent = 30;

    /// <summary>The band for weeks of (done, counted) Actions, oldest first. No Actions counted means no evidence: Absorb.</summary>
    public static Band Measure(IReadOnlyList<(int Done, int Total)> weeks)
    {
        var done = weeks.Sum(w => w.Done);
        var total = weeks.Sum(w => w.Total);
        if (total == 0) return Band.Absorb;
        if (weeks.Count >= 2 && weeks.All(w => w.Total > 0 && w.Done * 100 < w.Total * RethinkPercent)) return Band.Rethink;
        return done * 100 < total * AbsorbPercent ? Band.EaseBack : Band.Absorb;
    }
}

/// <summary>Words the Coach never uses about a miss. Checked in code on every reply about one.</summary>
public static class MissTone
{
    private static readonly System.Text.RegularExpressions.Regex Banned =
        new(@"\b(missed|failed|failure|streaks?)\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    public static bool Violates(string text) => Banned.IsMatch(text);

    public const string Rules =
        "Tone for anything about a miss: one line of acknowledgement and one next step. No guilt, no lessons, no streaks. " +
        "Never use the words \"missed\", \"failed\" or \"streak\". Always leave an easy out.";
}

public static class RethinkPrompt
{
    public const string System =
        "You are the Coach in Waypoint. The user has done very little of one Aspiration for two weeks. Open a short, warm " +
        "conversation: one line that notices it without blame, and one open question about what changed. Do not offer to cut the Plan. " +
        MissTone.Rules;
}

/// <summary>Opens the Re-think conversation for one Aspiration: one Coach message, never a quiet cut of the Plan.</summary>
public sealed class Rethinks(WaypointDb db, JobQueue jobs, ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock)
{
    public async Task OpenAsync(Guid aspirationId, CancellationToken ct)
    {
        var aspiration = await db.Aspirations.FirstOrDefaultAsync(a => a.Id == aspirationId, ct);
        if (aspiration is null) return;
        var choice = options.Value.For(CoachCallKind.Chat);
        var reply = new StringBuilder();
        await foreach (var chunk in model.StreamAsync(new CoachRequest(CoachCallKind.Chat, choice.Provider, choice.Model, RethinkPrompt.System,
            [new CoachTurn("user", $"{{\"aspiration\":\"{aspiration.ShortName.Replace("\"", "'")}\"}}")]), ct))
            reply.Append(chunk);
        var text = reply.ToString().Trim();
        // Late, never faked: no words, or words that blame, fail the job and it retries.
        if (text.Length == 0) throw new CoachUnavailableException("empty_reply");
        if (MissTone.Violates(text)) throw new CoachUnavailableException("reply_tone");

        var message = new ChatMessage
        {
            Id = Guid.NewGuid(), UserId = aspiration.UserId, Role = "coach", Text = text, AspirationId = aspiration.Id,
            Card = "rethink", CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.ChatMessages.Add(message);
        jobs.Add(JobKinds.PushMessage, aspiration.UserId, new Dictionary<string, Guid> { ["chatMessageId"] = message.Id }, aspiration.Id);
        await db.SaveChangesAsync(ct);
    }
}
