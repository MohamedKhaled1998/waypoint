using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

public sealed record StoredMessage(Guid Id, string Role, string Text, string? ClientId, bool Replied);
public sealed record SseEvent(string Name, JsonElement Data);

public sealed class CoachChatTests(WaypointApiFactory factory) : IClassFixture<WaypointApiFactory>
{
    private async Task<HttpClient> SignedInClientAsync()
    {
        var sub = "sub-" + Guid.NewGuid().ToString("N");
        var response = await factory.CreateClient().PostAsJsonAsync("/auth/google",
            new { idToken = FakeGoogleVerifier.TokenFor(sub) });
        var tokens = (await response.Content.ReadFromJsonAsync<TokenPair>())!;
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);
        return client;
    }

    private static async Task<List<SseEvent>> SendAsync(HttpClient client, string text, string clientId)
    {
        var response = await client.PostAsJsonAsync("/coach/messages", new { text, clientId });
        response.EnsureSuccessStatusCode();
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        var events = new List<SseEvent>();
        foreach (var block in (await response.Content.ReadAsStringAsync()).Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var lines = block.Split('\n');
            var name = lines.Single(l => l.StartsWith("event: ", StringComparison.Ordinal))[7..];
            var data = lines.Single(l => l.StartsWith("data: ", StringComparison.Ordinal))[6..];
            events.Add(new SseEvent(name, JsonDocument.Parse(data).RootElement.Clone()));
        }
        return events;
    }

    private static Task<StoredMessage[]> MessagesAsync(HttpClient client) =>
        client.GetFromJsonAsync<StoredMessage[]>("/coach/messages")!;

    [Fact]
    public async Task A_message_streams_delta_events_then_done_and_both_messages_are_stored()
    {
        factory.Coach.Fail = false;
        factory.Coach.Chunks = ["Hello", " there"];
        var client = await SignedInClientAsync();

        var events = await SendAsync(client, "I want to run a half marathon", "c-1");

        Assert.Equal(["delta", "delta", "done"], events.Select(e => e.Name));
        Assert.Equal("Hello", events[0].Data.GetProperty("text").GetString());
        var stored = await MessagesAsync(client);
        Assert.Equal(["user", "coach"], stored.Select(m => m.Role));
        Assert.Equal("I want to run a half marathon", stored[0].Text);
        Assert.Equal("c-1", stored[0].ClientId);
        Assert.Equal("Hello there", stored[1].Text);
        Assert.Equal(stored[1].Id, events[^1].Data.GetProperty("messageId").GetGuid());
    }

    [Fact]
    public async Task A_provider_failure_yields_error_and_the_users_message_stays_saved()
    {
        factory.Coach.Fail = true;
        var client = await SignedInClientAsync();

        var events = await SendAsync(client, "Are you there?", "c-1");

        Assert.Equal(["error"], events.Select(e => e.Name));
        var stored = await MessagesAsync(client);
        Assert.Equal(["user"], stored.Select(m => m.Role));
        Assert.Equal("Are you there?", stored[0].Text);
    }

    [Fact]
    public async Task Resending_with_the_same_client_ID_does_not_duplicate_and_the_late_reply_comes_once_the_call_succeeds()
    {
        factory.Coach.Fail = true;
        var client = await SignedInClientAsync();
        await SendAsync(client, "Hi", "c-1");

        factory.Coach.Fail = false;
        var retry = await SendAsync(client, "Hi", "c-1");
        var again = await SendAsync(client, "Hi", "c-1");

        Assert.Equal("done", retry[^1].Name);
        Assert.Equal("done", again[^1].Name);
        var stored = await MessagesAsync(client);
        Assert.Equal(["user", "coach"], stored.Select(m => m.Role));
    }

    [Fact]
    public async Task The_history_says_which_user_messages_still_wait_for_a_reply()
    {
        factory.Coach.Fail = true;
        var client = await SignedInClientAsync();
        await SendAsync(client, "Hi", "c-1");
        Assert.False((await MessagesAsync(client)).Single().Replied);

        factory.Coach.Fail = false;
        await SendAsync(client, "Hi", "c-1");
        Assert.True((await MessagesAsync(client)).First(m => m.Role == "user").Replied);
    }

    [Fact]
    public async Task Another_user_never_sees_the_thread()
    {
        factory.Coach.Fail = false;
        var mine = await SignedInClientAsync();
        await SendAsync(mine, "private words", "c-1");
        Assert.Empty(await MessagesAsync(await SignedInClientAsync()));
    }

    [Fact]
    public async Task The_model_for_each_kind_of_call_comes_from_configuration()
    {
        factory.Coach.Fail = false;
        using var configured = factory.WithWebHostBuilder(b =>
            b.UseSetting("Coach:Models:Chat:Model", "model-from-config"));
        var client = configured.CreateClient();
        var sub = "sub-" + Guid.NewGuid().ToString("N");
        var tokens = (await (await client.PostAsJsonAsync("/auth/google", new { idToken = FakeGoogleVerifier.TokenFor(sub) }))
            .Content.ReadFromJsonAsync<TokenPair>())!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens.AccessToken);

        await SendAsync(client, "hello", "c-1");

        Assert.Equal("model-from-config", factory.Coach.Requests[^1].Model);
    }

    [Fact]
    public async Task Free_text_from_a_message_never_appears_in_logs_and_the_prompt_never_carries_name_or_email()
    {
        const string secret = "my-knee-surgery-is-on-the-4th-xyzzy";
        var client = await SignedInClientAsync();

        factory.Coach.Fail = false;
        await SendAsync(client, secret, "c-1");
        factory.Coach.Fail = true;
        await SendAsync(client, secret + "-again", "c-2");

        Assert.NotEmpty(factory.Logs.Lines);
        Assert.DoesNotContain(factory.Logs.Lines, l => l.Contains("xyzzy") || l.Contains("Hello"));
        factory.Coach.Fail = false;
        await SendAsync(client, "x", "c-3");
        Assert.DoesNotContain("Test User", factory.Coach.Requests[^1].System);
    }
}
