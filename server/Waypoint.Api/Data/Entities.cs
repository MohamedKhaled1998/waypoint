namespace Waypoint.Api.Data;

/// <summary>Keyed on the Google <c>sub</c>, never the email.</summary>
public sealed class User
{
    public Guid Id { get; set; }
    public required string GoogleSub { get; set; }
    public DateTime CreatedAt { get; set; }
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
}
