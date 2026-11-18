using Google.Apis.Auth;
using Microsoft.Extensions.Options;

namespace Waypoint.Api.Auth;

public sealed record GoogleIdentity(string Sub, string? Name);

public interface IGoogleTokenVerifier
{
    /// <summary>Returns the verified identity, or null when the token is invalid.</summary>
    Task<GoogleIdentity?> VerifyAsync(string idToken, CancellationToken ct);
}

public sealed class GoogleOptions
{
    public string[] ClientIds { get; set; } = [];
}

public sealed class GoogleTokenVerifier(IOptions<GoogleOptions> options) : IGoogleTokenVerifier
{
    public async Task<GoogleIdentity?> VerifyAsync(string idToken, CancellationToken ct)
    {
        // Without a configured audience any Google-issued token would pass; reject everything instead.
        if (options.Value.ClientIds.Length == 0) return null;
        try
        {
            var payload = await GoogleJsonWebSignature.ValidateAsync(idToken,
                new GoogleJsonWebSignature.ValidationSettings { Audience = options.Value.ClientIds });
            return new GoogleIdentity(payload.Subject, payload.Name);
        }
        catch (InvalidJwtException)
        {
            return null;
        }
    }
}
