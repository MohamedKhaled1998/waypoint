using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Waypoint.Api.Data;

namespace Waypoint.Api.Auth;

public sealed record TokenPair(string AccessToken, string RefreshToken);

public static class AuthDefaults
{
    public static readonly TimeSpan AccessTokenLifetime = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(60);

    public static SymmetricSecurityKey SigningKey(string key) => new(Encoding.UTF8.GetBytes(key));
}

/// <summary>Sign-in, rotation and sign-out. The only code that looks across users (IgnoreQueryFilters).</summary>
public sealed class TokenService(WaypointDb db, TimeProvider clock, IConfiguration config)
{
    public async Task<TokenPair> SignInAsync(GoogleIdentity identity, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.Users.IgnoreQueryFilters().SingleOrDefaultAsync(u => u.GoogleSub == identity.Sub, ct);
        if (user is null)
        {
            user = new User { Id = Guid.NewGuid(), GoogleSub = identity.Sub, CreatedAt = now };
            db.Users.Add(user);
        }
        return Issue(user.Id, Guid.NewGuid(), now);
    }

    /// <summary>Rotates a refresh token; null when unknown, expired or already used.</summary>
    public async Task<TokenPair?> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return null;

        if (token.RevokedAt is not null)
        {
            // A used token coming back means it leaked: end the whole session family.
            await RevokeFamilyAsync(token.FamilyId, now, ct);
            return null;
        }
        if (token.ExpiresAt <= now) return null;

        token.RevokedAt = now;
        return Issue(token.UserId, token.FamilyId, now);
    }

    public async Task SignOutAsync(string refreshToken, CancellationToken ct)
    {
        var hash = Hash(refreshToken);
        var token = await db.RefreshTokens.IgnoreQueryFilters().SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is not null) await RevokeFamilyAsync(token.FamilyId, clock.GetUtcNow().UtcDateTime, ct);
    }

    private async Task RevokeFamilyAsync(Guid familyId, DateTime now, CancellationToken ct)
    {
        var family = await db.RefreshTokens.IgnoreQueryFilters()
            .Where(t => t.FamilyId == familyId && t.RevokedAt == null).ToListAsync(ct);
        foreach (var t in family) t.RevokedAt = now;
        await db.SaveChangesAsync(ct);
    }

    // Callers save: sign-in saves the new user together with the token.
    private TokenPair Issue(Guid userId, Guid familyId, DateTime now)
    {
        var refresh = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(), UserId = userId, FamilyId = familyId, TokenHash = Hash(refresh),
            CreatedAt = now, ExpiresAt = now + AuthDefaults.RefreshTokenLifetime,
        });

        var jwt = new JwtSecurityToken(
            claims: [new Claim(ClaimTypes.NameIdentifier, userId.ToString())],
            notBefore: now, expires: now + AuthDefaults.AccessTokenLifetime,
            signingCredentials: new SigningCredentials(
                AuthDefaults.SigningKey(config["Jwt:Key"]!), SecurityAlgorithms.HmacSha256));
        return new TokenPair(new JwtSecurityTokenHandler { OutboundClaimTypeMap = { } }.WriteToken(jwt), refresh);
    }

    private static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
