using System.Runtime.CompilerServices;
using Waypoint.Api.Coach;

namespace Waypoint.Api.Tests.Support;

/// <summary>Streams scripted chunks; can be told to fail, before or after the first chunk.</summary>
public sealed class FakeCoachModelClient : ICoachModelClient
{
    public List<string> Chunks { get; set; } = ["Hello", " there"];
    public bool Fail { get; set; }
    public List<CoachRequest> Requests { get; } = [];

    public async IAsyncEnumerable<string> StreamAsync(CoachRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        Requests.Add(request);
        if (Fail) throw new InvalidOperationException("provider down");
        foreach (var chunk in Chunks)
        {
            await Task.Yield();
            yield return chunk;
        }
    }
}
