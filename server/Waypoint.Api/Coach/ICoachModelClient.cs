namespace Waypoint.Api.Coach;

/// <summary>The kinds of Coach call; the provider and model for each are server configuration.</summary>
public enum CoachCallKind
{
    Interview, Plan, RePlan, WeeklyRefresh, Chat, Guide,
    ReminderText, CheckInReplyNoNote, CheckInReplyWithNote, SilenceNudge,
}

public sealed record CoachTurn(string Role, string Text);

/// <summary>One model call. <c>System</c> is the stable part (cached); never carries the user's name or email.</summary>
public sealed record CoachRequest(CoachCallKind Kind, string Provider, string Model, string System, IReadOnlyList<CoachTurn> Turns);

/// <summary>The one client every Coach call goes through.</summary>
public interface ICoachModelClient
{
    IAsyncEnumerable<string> StreamAsync(CoachRequest request, CancellationToken ct);
}

/// <summary>The provider could not answer. Carries no provider text, so it is safe to log.</summary>
/// <summary>An exception whose <see cref="Reason"/> is a code with no user text, safe to log.</summary>
public interface ISafeReason { string Reason { get; } }

public sealed class CoachUnavailableException(string reason) : Exception(reason), ISafeReason
{
    public string Reason => reason;
}
