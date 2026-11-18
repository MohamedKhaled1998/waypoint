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

    public async IAsyncEnumerable<string> StreamAsync(CoachRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        if (Fail) throw new InvalidOperationException("provider down");
        var chunks = Script.Count > 0 ? Script.Dequeue() : Chunks.ToArray();
        foreach (var chunk in chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}
