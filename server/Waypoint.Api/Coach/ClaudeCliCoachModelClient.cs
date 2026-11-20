using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Waypoint.Api.Coach;

/// <summary>Section <c>ClaudeCli</c>: development only. Runs Coach calls through the local <c>claude</c> CLI.</summary>
public sealed class ClaudeCliOptions
{
    public bool Enabled { get; set; }
    public string Path { get; set; } = "claude";
}

/// <summary>
/// Development stand-in for <see cref="AnthropicCoachModelClient"/>: runs each Coach call through
/// <c>claude -p</c>, which signs in with the developer's own Claude login or <c>CLAUDE_CODE_OAUTH_TOKEN</c>
/// instead of Waypoint's API key. For one developer testing locally only, never for app users.
/// </summary>
public sealed class ClaudeCliCoachModelClient(IOptions<ClaudeCliOptions> options) : ICoachModelClient
{
    public async IAsyncEnumerable<string> StreamAsync(CoachRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.Value.Path)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            // No CLAUDE.md or project settings get picked up from here.
            WorkingDirectory = System.IO.Path.GetTempPath(),
        };
        foreach (var arg in new[]
        {
            "-p", "--model", request.Model, "--system-prompt", request.System, "--tools", "",
            "--output-format", "stream-json", "--include-partial-messages", "--verbose",
            "--no-session-persistence", "--strict-mcp-config", "--disable-slash-commands", "--setting-sources", "",
        }) start.ArgumentList.Add(arg);
        // An API key in the environment would win over the OAuth login and bill the API.
        start.Environment.Remove("ANTHROPIC_API_KEY");

        Process process;
        try { process = Process.Start(start) ?? throw new CoachUnavailableException("cli_not_started"); }
        catch (System.ComponentModel.Win32Exception) { throw new CoachUnavailableException("cli_not_found"); }
        using var _ = process;
        using var kill = ct.Register(() => { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
        // Drain stderr so the CLI never blocks on it; it may echo prompt text, so it is never logged.
        var stderr = process.StandardError.ReadToEndAsync(CancellationToken.None);

        await process.StandardInput.WriteAsync(Prompt(request.Turns));
        process.StandardInput.Close();

        var wroteText = false;
        var failed = false;
        while (await process.StandardOutput.ReadLineAsync(ct) is { } line)
        {
            if (!line.StartsWith('{')) continue;
            using var json = JsonDocument.Parse(line);
            var root = json.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "stream_event"
                && root.GetProperty("event") is var ev
                && ev.GetProperty("type").GetString() == "content_block_delta"
                && ev.GetProperty("delta").TryGetProperty("text", out var text))
            {
                wroteText = true;
                yield return text.GetString() ?? "";
            }
            else if (type == "result" && root.TryGetProperty("is_error", out var isError) && isError.GetBoolean())
            {
                failed = true;
            }
        }

        await process.WaitForExitAsync(ct);
        await stderr;
        if (failed || process.ExitCode != 0) throw new CoachUnavailableException("cli_error");
        if (!wroteText) throw new CoachUnavailableException("cli_empty");
    }

    /// <summary>The CLI takes one prompt, so earlier turns are replayed as a transcript ahead of the last one.</summary>
    private static string Prompt(IReadOnlyList<CoachTurn> turns)
    {
        if (turns.Count == 1) return turns[0].Text;
        var sb = new StringBuilder("The conversation so far:\n\n");
        foreach (var turn in turns.Take(turns.Count - 1))
            sb.Append('<').Append(turn.Role).Append(">\n").Append(turn.Text).Append("\n</").Append(turn.Role).Append(">\n\n");
        sb.Append("Reply as the assistant to this latest message:\n\n").Append(turns[^1].Text);
        return sb.ToString();
    }
}
