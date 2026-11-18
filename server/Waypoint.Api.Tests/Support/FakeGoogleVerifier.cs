using Waypoint.Api.Auth;

namespace Waypoint.Api.Tests.Support;

/// <summary>Accepts tokens of the form "valid:{sub}"; anything else is rejected.</summary>
public sealed class FakeGoogleVerifier : IGoogleTokenVerifier
{
    public const string Prefix = "valid:";

    public static string TokenFor(string sub) => Prefix + sub;

    public Task<GoogleIdentity?> VerifyAsync(string idToken, CancellationToken ct)
    {
        GoogleIdentity? identity = idToken.StartsWith(Prefix, StringComparison.Ordinal)
            ? new GoogleIdentity(idToken[Prefix.Length..], "Test User")
            : null;
        return Task.FromResult(identity);
    }
}
