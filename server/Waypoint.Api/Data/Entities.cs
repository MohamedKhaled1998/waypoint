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
