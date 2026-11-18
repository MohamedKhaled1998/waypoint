using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public sealed record SendMessageRequest(string? Text, string? ClientId, Guid? AspirationId);

public static class CoachChat
{
    private const int WindowSize = 20;

    // The stable system part: no user data, so it caches. Never add the user's name or email.
    public const string SystemPrompt =
        "You are the Coach in Waypoint, a personal coaching app. You help the user reach their Aspirations " +
        "with warmth and honesty. Keep replies short and plain. Never invent facts about the user's progress.";

    public static void MapCoachChat(this WebApplication app)
    {
        app.MapGet("/coach/messages", async (WaypointDb db, CancellationToken ct) =>
            Results.Ok(await ThreadAsync(db, ct))).RequireAuthorization();
        app.MapPost("/coach/messages", Send).RequireAuthorization();
    }

    /// <summary>The thread in order. <c>Replied</c> is false for a user message the Coach has not answered yet.</summary>
    private static async Task<object[]> ThreadAsync(WaypointDb db, CancellationToken ct)
    {
        var all = await db.ChatMessages.OrderBy(m => m.Seq).ToListAsync(ct);
        var answered = all.Where(m => m.InReplyToId is not null).Select(m => m.InReplyToId!.Value).ToHashSet();
        return all.Select(m => View(m, answered.Contains(m.Id))).ToArray();
    }

    public static object View(ChatMessage m, bool answered) => new
    {
        m.Id, m.Role, m.Text, m.AspirationId, m.ClientId, m.Card,
        CardData = m.CardJson is null ? null : System.Text.Json.Nodes.JsonNode.Parse(m.CardJson),
        Replied = m.Role != "user" || answered,
        CreatedAt = DateTime.SpecifyKind(m.CreatedAt, DateTimeKind.Utc),
    };

    private static async Task Send(SendMessageRequest req, HttpContext http, WaypointDb db, CurrentUser me,
        ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock, ILoggerFactory logs)
    {
        var log = logs.CreateLogger("Waypoint.Coach.Chat");
        var ct = http.RequestAborted;
        if (string.IsNullOrWhiteSpace(req.Text) || string.IsNullOrWhiteSpace(req.ClientId) || req.ClientId.Length > 64)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var user = await db.ChatMessages.FirstOrDefaultAsync(m => m.ClientId == req.ClientId, ct);
        if (user is null)
        {
            user = new ChatMessage
            {
                Id = Guid.NewGuid(), UserId = me.Id!.Value, Role = "user", Text = req.Text,
                AspirationId = req.AspirationId, ClientId = req.ClientId, CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.ChatMessages.Add(user);
            await db.SaveChangesAsync(ct);
        }

        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.StartAsync(ct);

        // A resend whose reply already exists replays it instead of calling the model again.
        var existing = await db.ChatMessages.FirstOrDefaultAsync(m => m.InReplyToId == user.Id, ct);
        if (existing is not null)
        {
            await Write(http, "delta", new { text = existing.Text }, ct);
            await Write(http, "done", new { messageId = existing.Id }, ct);
            return;
        }

        var watch = Stopwatch.StartNew();
        var reply = new StringBuilder();
        try
        {
            var choice = options.Value.For(CoachCallKind.Chat);
            var window = await db.ChatMessages.Where(m => m.Seq <= user.Seq)
                .OrderByDescending(m => m.Seq).Take(WindowSize).OrderBy(m => m.Seq).ToListAsync(ct);
            var turns = window.SkipWhile(m => m.Role != "user")
                .Select(m => new CoachTurn(m.Role == "user" ? "user" : "assistant", m.Text)).ToList();

            await foreach (var chunk in model.StreamAsync(
                new CoachRequest(CoachCallKind.Chat, choice.Provider, choice.Model, SystemPrompt, turns), ct))
            {
                reply.Append(chunk);
                await Write(http, "delta", new { text = chunk }, ct);
            }
            if (reply.Length == 0) throw new CoachUnavailableException("empty_reply");

            var coach = new ChatMessage
            {
                Id = Guid.NewGuid(), UserId = user.UserId, Role = "coach", Text = reply.ToString(),
                AspirationId = user.AspirationId, InReplyToId = user.Id, CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.ChatMessages.Add(coach);
            await db.SaveChangesAsync(ct);
            log.LogInformation("Coach reply {MessageId} for {UserMessageId} in {ElapsedMs} ms, {Chars} chars",
                coach.Id, user.Id, watch.ElapsedMilliseconds, reply.Length);
            await Write(http, "done", new { messageId = coach.Id }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.LogInformation("Chat request for {UserMessageId} aborted by the phone", user.Id);
        }
        catch (Exception ex)
        {
            // Type only: exception messages and provider bodies can carry free text.
            log.LogWarning("Coach call for {UserMessageId} failed after {ElapsedMs} ms: {ErrorType}",
                user.Id, watch.ElapsedMilliseconds, ex.GetType().Name);
            await Write(http, "error", new { type = "coach_unavailable" }, CancellationToken.None);
        }
    }

    private static async Task Write(HttpContext http, string name, object data, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, JsonSerializerOptions.Web)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
