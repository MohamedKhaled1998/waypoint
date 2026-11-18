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
    public Func<CoachRequest, string[]?>? Responder { get; set; } = ReminderTextFor;

    /// <summary>The default responder: every reminder batch gets text for each Action in it, so those jobs never eat a scripted reply.</summary>
    public static string[]? ReminderTextFor(CoachRequest req)
    {
        if (req.Kind != CoachCallKind.ReminderText) return null;
        var ids = System.Text.Json.JsonDocument.Parse(req.Turns[0].Text).RootElement.GetProperty("actions").EnumerateArray().Select(a => a.GetProperty("id").GetString());
        return ["```json\n" + System.Text.Json.JsonSerializer.Serialize(new { reminders = ids.Select(id => new { id, text = "Easy run: shoes by the door." }) }) + "\n```"];
    }

    public async IAsyncEnumerable<string> StreamAsync(CoachRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        if (Fail) throw new InvalidOperationException("provider down");
        var chunks = Responder?.Invoke(request) ?? (Script.Count > 0 ? Script.Dequeue() : Chunks.ToArray());
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}
