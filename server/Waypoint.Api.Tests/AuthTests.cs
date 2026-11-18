using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed record TokenPair(string AccessToken, string RefreshToken);
public sealed record Me(Guid Id);
public sealed record Session(Guid Id);

public sealed class AuthTests(WaypointApiFactory factory) : IClassFixture<WaypointApiFactory>
{
    private static string NewSub() => "sub-" + Guid.NewGuid().ToString("N");

    private async Task<TokenPair> SignInAsync(string sub)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/google",
            new { idToken = FakeGoogleVerifier.TokenFor(sub) });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<TokenPair>())!;
    }

    private HttpClient ClientFor(TokenPair tokens)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    [Fact]
    public async Task A_valid_Google_ID_token_creates_a_User_keyed_on_sub_and_returns_tokens()
    {
        var sub = NewSub();
        var first = await SignInAsync(sub);
        Assert.False(string.IsNullOrEmpty(first.AccessToken));
        Assert.False(string.IsNullOrEmpty(first.RefreshToken));

        var me = await ClientFor(first).GetFromJsonAsync<Me>("/me");

        // Signing in again with the same sub finds the same User.
        var second = await SignInAsync(sub);
        var meAgain = await ClientFor(second).GetFromJsonAsync<Me>("/me");
        Assert.Equal(me!.Id, meAgain!.Id);

        var other = await ClientFor(await SignInAsync(NewSub())).GetFromJsonAsync<Me>("/me");
        Assert.NotEqual(me.Id, other!.Id);
    }

    [Fact]
    public async Task An_invalid_Google_ID_token_is_rejected()
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/google", new { idToken = "garbage" });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Requests_without_an_access_token_are_rejected()
    {
        var response = await factory.CreateClient().GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task The_access_token_expires_after_about_15_minutes()
    {
        var tokens = await SignInAsync(NewSub());
        var client = ClientFor(tokens);
        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/me")).StatusCode);
        factory.Clock.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/me")).StatusCode);
    }

    [Fact]
    public async Task A_refresh_rotates_the_refresh_token_and_the_old_one_stops_working()
    {
        var first = await SignInAsync(NewSub());
        var http = factory.CreateClient();

        var refreshed = await http.PostAsJsonAsync("/auth/refresh", new { refreshToken = first.RefreshToken });
        refreshed.EnsureSuccessStatusCode();
        var second = (await refreshed.Content.ReadFromJsonAsync<TokenPair>())!;
        Assert.NotEqual(first.RefreshToken, second.RefreshToken);
        Assert.Equal(HttpStatusCode.OK, (await ClientFor(second).GetAsync("/me")).StatusCode);

        var replay = await http.PostAsJsonAsync("/auth/refresh", new { refreshToken = first.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);

        // Replaying a used token is treated as theft: the newer token is revoked too.
        var afterReplay = await http.PostAsJsonAsync("/auth/refresh", new { refreshToken = second.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, afterReplay.StatusCode);
    }

    [Fact]
    public async Task An_expired_refresh_token_is_rejected()
    {
        var tokens = await SignInAsync(NewSub());
        factory.Clock.Advance(TimeSpan.FromDays(61));
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Signing_out_revokes_the_refresh_token()
    {
        var tokens = await SignInAsync(NewSub());
        var http = factory.CreateClient();
        var signOut = await http.PostAsJsonAsync("/auth/sign-out", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.NoContent, signOut.StatusCode);
        var refresh = await http.PostAsJsonAsync("/auth/refresh", new { refreshToken = tokens.RefreshToken });
        Assert.Equal(HttpStatusCode.Unauthorized, refresh.StatusCode);
    }

    [Fact]
    public async Task One_user_can_never_read_another_users_rows()
    {
        var alice = await SignInAsync(NewSub());
        var bob = await SignInAsync(NewSub());
        await SignInAsync(Guid.NewGuid().ToString()); // more rows in the table that belong to nobody in this test

        var aliceSessions = await ClientFor(alice).GetFromJsonAsync<List<Session>>("/me/sessions");
        var bobSessions = await ClientFor(bob).GetFromJsonAsync<List<Session>>("/me/sessions");

        Assert.Single(aliceSessions!);
        Assert.Single(bobSessions!);
        Assert.NotEqual(aliceSessions![0].Id, bobSessions![0].Id);
    }

    [Fact]
    public async Task A_valid_access_token_for_a_User_that_no_longer_exists_is_unauthorized()
    {
        var tokens = await SignInAsync(NewSub());
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<WaypointDb>();
            await db.RefreshTokens.IgnoreQueryFilters().ExecuteDeleteAsync();
            await db.Users.IgnoreQueryFilters().ExecuteDeleteAsync();
        }
        var response = await ClientFor(tokens).GetAsync("/me");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void The_API_refuses_to_start_without_a_Google_client_ID()
    {
        var unconfigured = factory.WithWebHostBuilder(b => b.UseSetting("Google:ClientIds:0", ""));
        Assert.ThrowsAny<Exception>(() => unconfigured.CreateClient());
    }

    [Fact]
    public async Task The_Google_verifier_rejects_every_token_when_no_client_ID_is_configured()
    {
        var verifier = new GoogleTokenVerifier(Microsoft.Extensions.Options.Options.Create(new GoogleOptions()));
        Assert.Null(await verifier.VerifyAsync("any", CancellationToken.None));
    }
}
