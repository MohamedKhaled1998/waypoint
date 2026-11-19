using System.Runtime.CompilerServices;
using Waypoint.Api.Coach;

namespace Waypoint.Api.Tests.Support;

/// <summary>Streams scripted chunks; can be told to fail, before or after the first chunk.</summary>
public sealed class FakeCoachModelClient : ICoachModelClient
{
    public List<string> Chunks { get; set; } = ["Hello", " there"];
    public bool Fail { get; set; }
    public List<CoachRequest> Requests { get; } = [];

    /// <summary>Scripted replies, one per call, used before <see cref="Chunks"/>.</summary>
    public Queue<string[]> Script { get; } = new();
    public void Enqueue(params string[] chunks) => Script.Enqueue(chunks);

    /// <summary>Answers a request itself when it returns chunks, before <see cref="Script"/>; for tests with many jobs in flight.</summary>
    public Func<CoachRequest, string[]?>? Responder { get; set; }

    public FakeCoachModelClient() => Responder = req => ReminderTextFor(req) ?? WeeklyRefreshFor(req) ?? SilenceNudgeFor(req) ?? NoteFor(req) ?? GuideFor(req);

    /// <summary>The default responder: every reminder batch gets text for each Action in it, so those jobs never eat a scripted reply.</summary>
    public static string[]? ReminderTextFor(CoachRequest req)
    {
        if (req.Kind != CoachCallKind.ReminderText) return null;
        var ids = System.Text.Json.JsonDocument.Parse(req.Turns[0].Text).RootElement.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("id").GetString());
        return ["```json\n" + System.Text.Json.JsonSerializer.Serialize(new { reminders = ids.Select(id => new { id, text = "Easy run: shoes by the door." }) }) + "\n```"];
    }

    /// <summary>A silence nudge: one line, so a nudge that comes due never eats a scripted reply.</summary>
    public static string[]? SilenceNudgeFor(CoachRequest req) => req.Kind == CoachCallKind.SilenceNudge ? ["Thinking of you; no rush."] : null;

    /// <summary>The chat's Pause check: by default the message is not a request to pause. A test sets <see cref="PauseIntent"/>.</summary>
    public string? PauseIntent { get; set; }

    /// <summary>Milestone and Measure notes: one line naming what the app asked it to say.</summary>
    public static string[]? NoteFor(CoachRequest req)
    {
        if (req.Kind is not (CoachCallKind.MilestoneNote or CoachCallKind.MeasureNote)) return null;
        return ["Coach says " + System.Text.Json.JsonDocument.Parse(req.Turns[0].Text).RootElement.GetProperty("say").GetString()];
    }

    /// <summary>Scripted replies for Guide calls, one per call. With none queued the Coach writes no Guides, so other tests never meet one.</summary>
    public Queue<string> GuideScript { get; } = new();
    public void EnqueueGuides(object reply) => GuideScript.Enqueue("```json\n" + System.Text.Json.JsonSerializer.Serialize(reply) + "\n```");

    /// <summary>
    /// Scripted replies for weekly-refresh Guide calls, each for the one refresh whose request contains its marker (text of that
    /// test's own Guide). Refreshes of other tests' users, which come due on the same fake clock, never take it.
    /// </summary>
    public List<(string Marker, string Reply)> RefreshGuideScript { get; } = [];
    public void EnqueueRefreshGuides(string marker, object reply) =>
        RefreshGuideScript.Add((marker, "```json\n" + System.Text.Json.JsonSerializer.Serialize(reply) + "\n```"));

    public string[]? GuideFor(CoachRequest req)
    {
        if (req.Kind != CoachCallKind.Guide) return null;
        if (req.Turns[0].Text.Contains("\"mode\":\"refresh\""))
        {
            var at = RefreshGuideScript.FindIndex(r => req.Turns[0].Text.Contains(r.Marker));
            if (at < 0) return ["```json\n{\"guides\":[]}\n```"];
            var reply = RefreshGuideScript[at].Reply;
            RefreshGuideScript.RemoveAt(at);
            return [reply];
        }
        return [GuideScript.Count > 0 ? GuideScript.Dequeue() : "```json\n{\"guides\":[]}\n```"];
    }

    /// <summary>The weekly refresh call: a rationale and a reason for every change code it was given.</summary>
    public static string[]? WeeklyRefreshFor(CoachRequest req)
    {
        if (req.Kind != CoachCallKind.WeeklyRefresh) return null;
        var root = System.Text.Json.JsonDocument.Parse(req.Turns[0].Text).RootElement;
        var codes = root.GetProperty("changes").EnumerateArray().Select(c => c.GetProperty("code").GetString()!).Distinct().ToList();
        var standing = root.TryGetProperty("progress", out var p) ? "Where you stand: " + p.GetProperty("state").GetString() : null;
        return ["```json\n" + System.Text.Json.JsonSerializer.Serialize(new { rationale = "Next week, gently.", reasons = codes.ToDictionary(c => c, c => "You kept up, so a small step."), standing }) + "\n```"];
    }

    public async IAsyncEnumerable<string> StreamAsync(CoachRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        if (Fail) throw new InvalidOperationException("provider down");
        var chunks = (request.Kind == CoachCallKind.PauseIntent ? new[] { PauseIntent ?? "{\"pause\": null}" } : null) ?? Responder?.Invoke(request) ?? (Script.Count > 0 ? Script.Dequeue() : Chunks.ToArray());
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}
