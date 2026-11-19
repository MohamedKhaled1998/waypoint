using FirebaseAdmin;
using FirebaseAdmin.Messaging;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Options;

namespace Waypoint.Api.Push;

public sealed class FirebaseOptions
{
    /// <summary>Path of the service-account JSON (env <c>Firebase__ServiceAccountFile</c>).</summary>
    public string? ServiceAccountFile { get; set; }
}

/// <summary>One notification for one device token. <see cref="Data"/> carries the route the tap opens.</summary>
public sealed record PushMessage(string Token, string Title, string Body, IReadOnlyDictionary<string, string> Data, string? Category = null);

public enum PushResult { Sent, InvalidToken }

/// <summary>The one way the server reaches a phone. Throws when FCM itself fails, so the caller's job retries.</summary>
public interface IPushSender
{
    Task<PushResult> SendAsync(PushMessage message, CancellationToken ct);
}

/// <summary>FCM HTTP v1 through FirebaseAdmin. High priority, so the notification always shows.</summary>
public sealed class FirebasePushSender(IOptions<FirebaseOptions> options) : IPushSender
{
    private readonly Lazy<FirebaseMessaging> _messaging = new(() =>
    {
        var app = FirebaseApp.DefaultInstance ?? FirebaseApp.Create(new AppOptions
        {
            Credential = GoogleCredential.FromFile(options.Value.ServiceAccountFile),
        });
        return FirebaseMessaging.GetMessaging(app);
    });

    public async Task<PushResult> SendAsync(PushMessage message, CancellationToken ct)
    {
        var fcm = new Message
        {
            Token = message.Token,
            Notification = new Notification { Title = message.Title, Body = message.Body },
            Data = message.Data.ToDictionary(p => p.Key, p => p.Value),
            Android = new AndroidConfig { Priority = Priority.High, Notification = new AndroidNotification { ChannelId = "coach", ClickAction = message.Category } },
        };
        try
        {
            await _messaging.Value.SendAsync(fcm, ct);
            return PushResult.Sent;
        }
        catch (FirebaseMessagingException ex) when (ex.MessagingErrorCode is MessagingErrorCode.Unregistered or MessagingErrorCode.InvalidArgument)
        {
            return PushResult.InvalidToken;
        }
    }
}
