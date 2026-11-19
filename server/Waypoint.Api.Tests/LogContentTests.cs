using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>Spec "Logs": IDs, timings, token counts and error types only. Every captured line must fit a known safe shape.</summary>
public sealed class LogContentTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private const string Id = "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}";
    private const string Path = @"/[\w/-]*"; // no query string, no spaces: nowhere for free text to ride along

    private static readonly Regex[] Safe =
    [
        new($"^Interview {Id} coach turn {Id} in \\d+ ms, status \\w+$"),
        new($"^Interview {Id} Coach call failed after \\d+ ms: \\w+$"),
        new($"^Interview {Id} request aborted by the phone$"),
        new($"^Coach reply {Id} for {Id} in \\d+ ms, \\d+ chars$"),
        new($"^Coach call for {Id} failed after \\d+ ms: \\w+$"),
        new($"^Chat request for {Id} aborted by the phone$"),
        new($"^Job {Id} \\w+ done$"),
        new($"^Job {Id} \\w+ attempt \\d+ failed: \\w+ [\\w.]*( \\(giving up\\))?$"),
        new($"^Plan version {Id} proposed for Aspiration {Id}$"),
        new($"^Plan draft for Aspiration {Id} rejected on attempt \\d+: [\\w ,.:;_-]*$"),
        new($"^Interview {Id} hand-off rejected on attempt \\d+: [\\w ,.:;_-]*$"),
        new($"^Weekly refresh {Id} proposed \\d+ versions$"),
        new($"^Job poll failed: \\w+$"),
        // Startup and EF noise. EF commands may only carry parameter placeholders ('?'), never values.
        new(@"^Applying migration '\w+'\.$"),
        new(@"^(Application started\. Press Ctrl\+C to shut down\.|Hosting environment: \w+|Content root path: \S+)$"),
        // EF formats timings with a thousands separator once a command takes a second or more ("1,937ms").
        new(@"^Executed DbCommand \([\d,]+ms\) \[Parameters=\[(@\w+='\?' \([^)']*\)(, )?)*\], CommandType='Text', CommandTimeout='\d+'\]"),
        // Request lines (the framework logs them at Warning and above by default; the patterns allow no query string).
        new($"^Request starting HTTP/[\\d.]+ [A-Z]+ https?://[\\w.:-]+{Path} - (null|[\\w/.-]+) (null|\\d+)$"),
        new($"^Request finished HTTP/[\\d.]+ [A-Z]+ https?://[\\w.:-]+{Path} - [\\w/.-]+ \\d+ [\\w/.;=-]* [\\d.]+ms$"),
    ];

    [Fact]
    public async Task Every_log_line_from_requests_and_jobs_holds_only_ids_timings_counts_and_error_types()
    {
        const string secret = "my-knee-surgery-is-on-the-4th-xyzzy";
        var (c, _, _, aspirationId) = await ConfirmedAsync();
        await DraftedAsync(c, aspirationId); // runs the Plan job and its push job

        factory.Coach.Fail = false;
        (await c.PostAsJsonAsync("/coach/messages", new { text = secret, clientId = "c-1" })).EnsureSuccessStatusCode();
        factory.Coach.Fail = true;
        (await c.PostAsJsonAsync("/coach/messages", new { text = secret + "-again", clientId = "c-2" })).EnsureSuccessStatusCode();
        factory.Coach.Fail = false;

        var lines = factory.Logs.Lines;
        Assert.Contains(lines, l => l.StartsWith("Job ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Coach reply", StringComparison.Ordinal));
        foreach (var line in lines)
        {
            Assert.DoesNotContain("xyzzy", line);
            Assert.DoesNotContain("half marathon", line);
        }
        var unsafeLines = lines.Where(l => !Safe.Any(r => r.IsMatch(l))).ToArray();
        Assert.True(unsafeLines.Length == 0, "Unexpected log shapes:\n" + string.Join("\n", unsafeLines));
    }
}
