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

public static class JobState
{
    public const string Pending = "pending";
    public const string Done = "done";
    public const string Failed = "failed";
}

/// <summary>
/// A unit of background work. The payload holds row IDs only, never free text. System queue: the runner reads it
/// across users and then acts as the job's user, so every user-owned row it touches stays behind the query filter.
/// </summary>
public sealed class Job
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public required string Kind { get; set; }
    /// <summary>The Aspiration a Plan job drafts for; null for kinds that have none.</summary>
    public Guid? AspirationId { get; set; }
    /// <summary>A JSON object whose values are all row IDs.</summary>
    public required string PayloadJson { get; set; }
    public required string State { get; set; }
    public int Attempts { get; set; }
    public DateTime NextRunAt { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class PlanVersionStatus
{
    public const string Proposed = "proposed";
    public const string Approved = "approved";
    /// <summary>A proposal the server replaced with a newer one before it was approved.</summary>
    public const string Replaced = "replaced";
    /// <summary>An approved version a later approved version took over from.</summary>
    public const string Superseded = "superseded";
}

/// <summary>A version of an Aspiration's Plan; immutable once approved. User-owned.</summary>
public sealed class PlanVersion
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AspirationId { get; set; }
    /// <summary>1, 2, ... assigned at approval; null while proposed.</summary>
    public int? Number { get; set; }
    public required string Status { get; set; }
    public required string Rationale { get; set; }
    /// <summary>Why this version exists, as a code: "first_plan", later "weekly_refresh", "re_plan", ...</summary>
    public required string Reason { get; set; }
    public required string Ramp { get; set; }
    public required string AssumptionsJson { get; set; }
    /// <summary>The validated first two weeks as the Coach drafted them; becomes scheduled Actions on approval.</summary>
    public required string TwoWeeksJson { get; set; }
    public DateOnly StartDate { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? ApprovedAt { get; set; }
}

public sealed class Milestone
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid PlanVersionId { get; set; }
    /// <summary>The Coach's id for it within the draft ("m1"); tasks point at it.</summary>
    public required string Ref { get; set; }
    public DateOnly Date { get; set; }
    public required string Title { get; set; }
    public required string ReachedWhen { get; set; }
    /// <summary>effort or ability.</summary>
    public required string Kind { get; set; }
    /// <summary>upcoming, reached or nearly_there.</summary>
    public required string State { get; set; }
}

/// <summary>A recurring Action: "N times a week" on Slots. Belongs to the Plan version.</summary>
public sealed class Habit
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid PlanVersionId { get; set; }
    public required string Title { get; set; }
    public int TimesPerWeek { get; set; }
    public int Minutes { get; set; }
    /// <summary>Comma-separated Slot IDs.</summary>
    public required string SlotIds { get; set; }
}

/// <summary>A one-off Action on a Milestone, with a date or, when <see cref="DueDate"/> is null, "this week".</summary>
public sealed class PlanTask
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid PlanVersionId { get; set; }
    public Guid MilestoneId { get; set; }
    public required string Title { get; set; }
    public int Minutes { get; set; }
    public DateOnly? DueDate { get; set; }
}

/// <summary>An occurrence in the two-week window. Exists only once its Plan version is approved.</summary>
public sealed class ScheduledAction
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AspirationId { get; set; }
    public Guid PlanVersionId { get; set; }
    public Guid? HabitId { get; set; }
    public Guid? TaskId { get; set; }
    public DateOnly Date { get; set; }
    public Guid SlotId { get; set; }
    public required string Title { get; set; }
    public int Minutes { get; set; }
}

public static class CheckInState
{
    public const string Done = "done";
    public const string Partly = "partly";
    public const string Skipped = "skipped";
    public static bool IsValid(string? s) => s is Done or Partly or Skipped;
}

/// <summary>The user's report on one scheduled Action. At most one per Action: a later tap replaces it. User-owned.</summary>
public sealed class CheckIn
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid ScheduledActionId { get; set; }
    /// <summary>Set by the phone; the latest tap's ID, so a resend is idempotent.</summary>
    public required string ClientId { get; set; }
    public required string State { get; set; }
    /// <summary>Minutes actually spent; required for partly.</summary>
    public int? MinutesSpent { get; set; }
    /// <summary>Comma-separated reason chip codes.</summary>
    public string? Reasons { get; set; }
    public string? Note { get; set; }
    public DateTime TappedAt { get; set; }
    /// <summary>The chat message that echoes this Check-in.</summary>
    public Guid EchoMessageId { get; set; }
}
