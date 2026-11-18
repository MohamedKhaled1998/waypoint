namespace Waypoint.Api.Data;

/// <summary>Keyed on the Google <c>sub</c>, never the email.</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public required string GoogleSub { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Total hours a week given to all active Aspirations; set by the first Interview.</summary>
    public double? TimeBudgetHours { get; set; }
    /// <summary>Quiet, Gentle or Chatty; set by the first Interview.</summary>
    public string? ContactFrequency { get; set; }
    /// <summary>When the per-user block (Slots, Time budget, Contact frequency) was first saved.</summary>
    public DateTime? SetupAt { get; set; }
}

/// <summary>A user-owned row: carries <c>UserId</c>, covered by the global query filter.</summary>
public sealed class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public required string TokenHash { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    /// <summary>Set when the token was rotated or the session signed out.</summary>
    public DateTime? RevokedAt { get; set; }
}

/// <summary>One message in the single Coach thread. User-owned.</summary>
public sealed class ChatMessage
{
    public Guid Id { get; set; }
    /// <summary>Orders the thread; assigned by the database.</summary>
    public long Seq { get; set; }
    public Guid UserId { get; set; }
    /// <summary>"user" or "coach".</summary>
    public required string Role { get; set; }
    public required string Text { get; set; }
    /// <summary>The Aspiration this message is about, if any.</summary>
    public Guid? AspirationId { get; set; }
    /// <summary>Set by the phone on its own messages, so a resend is idempotent.</summary>
    public string? ClientId { get; set; }
    /// <summary>On a Coach reply: the user message it answers.</summary>
    public Guid? InReplyToId { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Set on a card message, e.g. "interview_summary".</summary>
    public string? Card { get; set; }
    /// <summary>The card's data as JSON.</summary>
    public string? CardJson { get; set; }
}

/// <summary>A recurring window the user is free: one day-and-time window. User-owned.</summary>
public sealed class Slot
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Label { get; set; }
    /// <summary>Comma-separated mon..sun.</summary>
    public required string Days { get; set; }
    /// <summary>HH:mm.</summary>
    public required string Start { get; set; }
    public required string End { get; set; }
}

public sealed class Aspiration
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string OwnWords { get; set; }
    public required string ShortName { get; set; }
    /// <summary>coral, teal or amber; one per active Aspiration.</summary>
    public required string Hue { get; set; }
    /// <summary>active, paused, maintenance or closed.</summary>
    public required string Status { get; set; }
    public double ShareHoursPerWeek { get; set; }
    public DateOnly HopedForDate { get; set; }
    public DateOnly? SettleForDate { get; set; }
    public string? SettleForPicture { get; set; }
    /// <summary>gentle_check, quiet_replan or ask_why.</summary>
    public required string OnSkip { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class InterviewStatus
{
    public const string InProgress = "in_progress";
    public const string AwaitingConfirmation = "awaiting_confirmation";
    public const string Confirmed = "confirmed";
}

/// <summary>The Interview for an Aspiration; the Aspiration exists once it is confirmed. User-owned.</summary>
public sealed class Interview
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid? AspirationId { get; set; }
    public required string OwnWords { get; set; }
    public required string Status { get; set; }
    /// <summary>The latest hand-off the Coach produced: partial while in progress, complete once awaiting confirmation or confirmed.</summary>
    public string? HandOffJson { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class InterviewMessage
{
    public long Seq { get; set; }
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid InterviewId { get; set; }
    /// <summary>"user" or "coach".</summary>
    public required string Role { get; set; }
    public required string Text { get; set; }
    public string? ClientId { get; set; }
    public Guid? InReplyToId { get; set; }
    public DateTime CreatedAt { get; set; }
}
