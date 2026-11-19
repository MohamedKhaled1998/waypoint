using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;
using Waypoint.Api.Push;

namespace Waypoint.Api.Coach;

/// <summary>Silence: no Check-in and no app open. The ladder is 3 days, 7 days, then 14 days.</summary>
public static class Silence
{
    public static readonly TimeSpan FirstNudge = TimeSpan.FromDays(3);
    public static readonly TimeSpan SecondNudge = TimeSpan.FromDays(7);
    public static readonly TimeSpan AutoPause = TimeSpan.FromDays(14);

    /// <summary>The user did something: silence counts from now, and the next check moves to the first rung.</summary>
    public static async Task TouchAsync(WaypointDb db, JobQueue jobs, TimeProvider clock, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        user.LastActiveAt = now;
        user.SilenceStage = 0;
        await Pauses.EndCoachPausesAsync(db, jobs, now, ct);
        var pending = await db.Jobs.Where(j => j.Kind == JobKinds.SilenceCheck && j.State == JobState.Pending).ToListAsync(ct);
        if (pending.Count == 0)
            jobs.Add(JobKinds.SilenceCheck, user.Id, new Dictionary<string, Guid> { ["userId"] = user.Id }, delay: FirstNudge);
        foreach (var j in pending) j.NextRunAt = now + FirstNudge;
    }
}

public sealed class SilenceChecker(WaypointDb db, JobQueue jobs, CoachMessenger messenger, ICoachModelClient model, IOptions<CoachOptions> options,
    TimeProvider clock, CurrentUser me)
{
    public const string Card = "silence";

    private const string System = """
        You are the Coach in Waypoint, a personal coaching app. The user has been quiet for a while. The first message is
        JSON with "stage": 1 is a gentle nudge after a few days; 2 is a second nudge after about a week that offers a
        Pause (tell them it starts the moment they ask, and costs them nothing); 3 means the app has just started a Pause
        of everything and you say once, in a line or two, "I'm here when you're ready". Warm, plain, brief, no guilt,
        no streaks, no emojis, never mention missed Actions or numbers.
        """;

    public async Task CheckAsync(CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(ct);
        var now = clock.GetUtcNow().UtcDateTime;
        // Nothing to nudge about until a first Plan is approved, which books the check again.
        if (!await db.PlanVersions.AnyAsync(p => p.Status == PlanVersionStatus.Approved, ct)) return;
        var last = user.LastActiveAt ?? user.CreatedAt;
        var silent = now - last;
        var target = silent >= Silence.AutoPause ? 3 : silent >= Silence.SecondNudge ? 2 : silent >= Silence.FirstNudge ? 1 : 0;

        if (target > user.SilenceStage)
        {
            var pauses = await db.Pauses.ToListAsync(ct);
            var whole = pauses.Where(p => Pauses.IsActive(p, now) && p.ScheduledActionId is null && p.AspirationId is null).ToList();
            if (whole.Any(p => p.StartedBy == PauseStarters.User))
            {
                user.SilenceStage = target; // the user stepped back themselves: the Coach stays quiet
            }
            else
            {
                if (target == 3 && whole.Count == 0)
                {
                    var pause = new Pause { Id = Guid.NewGuid(), UserId = user.Id, ClientId = "silence-" + Guid.NewGuid().ToString("N"), StartsAt = now, StartedBy = PauseStarters.Coach };
                    db.Pauses.Add(pause);
                    await Pauses.StartAsync(db, jobs, pause, ct);
                    await db.SaveChangesAsync(ct); // the Pause stands even if the words come late
                }
                var text = await PhraseAsync(target, ct);
                user.SilenceStage = target;
                await messenger.PostUnpromptedAsync(null, text, ct, Card, JsonSerializer.Serialize(new { stage = target }, JsonSerializerOptions.Web));
            }
        }

        // The next rung, counted from the last activity.
        var next = user.SilenceStage switch { 0 => Silence.FirstNudge, 1 => Silence.SecondNudge, 2 => Silence.AutoPause, _ => (TimeSpan?)null };
        if (next is { } rung)
            jobs.Add(JobKinds.SilenceCheck, user.Id, new Dictionary<string, Guid> { ["userId"] = user.Id },
                delay: last + rung - now > TimeSpan.Zero ? last + rung - now : TimeSpan.FromHours(1));
        await db.SaveChangesAsync(ct);
    }

    private async Task<string> PhraseAsync(int stage, CancellationToken ct)
    {
        var choice = options.Value.For(CoachCallKind.SilenceNudge);
        var reply = new StringBuilder();
        await foreach (var chunk in model.StreamAsync(new CoachRequest(CoachCallKind.SilenceNudge, choice.Provider, choice.Model, System,
            [new CoachTurn("user", new JsonObject { ["stage"] = stage }.ToJsonString())]), ct))
            reply.Append(chunk);
        // Late, never faked: no reply means the job retries, with no canned line in the Coach's voice.
        if (reply.Length == 0) throw new CoachUnavailableException("empty_reply");
        return reply.ToString();
    }
}
