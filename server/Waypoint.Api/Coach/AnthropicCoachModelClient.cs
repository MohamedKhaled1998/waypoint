using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Waypoint.Api.Coach;

/// <summary>Streams from the Anthropic Messages API on Waypoint's key. The system part is marked for prompt caching.</summary>
public sealed class AnthropicCoachModelClient(HttpClient http, IOptions<AnthropicOptions> options) : ICoachModelClient
{
    public async IAsyncEnumerable<string> StreamAsync(CoachRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var o = options.Value;
        if (string.IsNullOrWhiteSpace(o.ApiKey)) throw new CoachUnavailableException("no_api_key");
        if (request.Provider != "anthropic") throw new CoachUnavailableException("unsupported_provider");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(o.BaseUrl), "/v1/messages"))
        {
            Content = JsonContent.Create(new
            {
                model = request.Model,
                max_tokens = o.MaxTokens,
                stream = true,
                system = new[] { new { type = "text", text = request.System, cache_control = new { type = "ephemeral" } } },
                messages = request.Turns.Select(t => new { role = t.Role, content = t.Text }),
            }),
        };
        message.Headers.Add("x-api-key", o.ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");

        HttpResponseMessage response;
        try { response = await http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (HttpRequestException) { throw new CoachUnavailableException("network"); }
        using var _ = response;
        // The body may echo prompt text: never read it into an exception or a log.
        if (!response.IsSuccessStatusCode) throw new CoachUnavailableException($"http_{(int)response.StatusCode}");

        using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(ct));
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal)) continue;
            using var json = JsonDocument.Parse(line.AsMemory(6));
            var root = json.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "content_block_delta" when root.GetProperty("delta").TryGetProperty("text", out var text):
                    yield return text.GetString() ?? "";
                    break;
                case "error":
                    throw new CoachUnavailableException("provider_error");
            }
        }
    }
}
