using System.Collections.Concurrent;
using Waypoint.Api.Push;

namespace Waypoint.Api.Tests.Support;

/// <summary>Records every push; tokens listed in <see cref="Invalid"/> are reported as no longer valid.</summary>
public sealed class FakePushSender : IPushSender
{
    public ConcurrentQueue<PushMessage> Sent { get; } = new();
    public ConcurrentDictionary<string, bool> Invalid { get; } = new();

    public Task<PushResult> SendAsync(PushMessage message, CancellationToken ct)
    {
        Sent.Enqueue(message);
        return Task.FromResult(Invalid.ContainsKey(message.Token) ? PushResult.InvalidToken : PushResult.Sent);
    }
}
