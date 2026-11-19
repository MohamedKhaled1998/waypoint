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
        ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock, JobQueue jobs, ILoggerFactory logs)
    {
        var log = logs.CreateLogger("Waypoint.Coach.Chat");
        var ct = http.RequestAborted;
        if (string.IsNullOrWhiteSpace(req.Text) || string.IsNullOrWhiteSpace(req.ClientId) || req.ClientId.Length > 64)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        await Silence.TouchAsync(db, jobs, clock, ct);
        await db.SaveChangesAsync(ct);
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
            var system = SystemPrompt;
            if (await PauseFromChatAsync(user, db, jobs, model, options.Value, clock, ct) is { } started)
                system += " The user asked for a Pause in this message and it has already started, with no approval needed: " + started +
                    " Confirm it in a line or two, warmly, and say they can resume any time. Do not say you will need to do anything.";
            var turns = window.SkipWhile(m => m.Role != "user")
                .Select(m => new CoachTurn(m.Role == "user" ? "user" : "assistant", m.Text)).ToList();

            await foreach (var chunk in model.StreamAsync(
                new CoachRequest(CoachCallKind.Chat, choice.Provider, choice.Model, system, turns), ct))
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

    private const string PauseIntentSystem = """
        You read one message from a user of a coaching app and decide whether they are asking to step back (a Pause) from
        their Aspirations: for example "I'm sick this week", "on holiday till the 20th", "pause everything for now".
        The first turn is JSON: "today" (a date), "aspirations" (id and shortName) and "message". Reply with only JSON:
        {"pause": null} when they are not asking, otherwise
        {"pause": {"aspirationId": <id from the list, or null for everything>, "span": "today" | "until" | "open", "until": "yyyy-MM-dd" (last day, only for "until")}}.
        Use "open" when they give no end. Pick one Aspiration only when they clearly name it.
        """;

    /// <summary>
    /// The model only reads the message; the server decides and starts the Pause (no approval) and says what it did.
    /// Returns a plain description for the reply, or null when nothing was started. Safe to run again on a resend.
    /// </summary>
    private static async Task<string?> PauseFromChatAsync(ChatMessage user, WaypointDb db, JobQueue jobs, ICoachModelClient model,
        CoachOptions options, TimeProvider clock, CancellationToken ct)
    {
        var clientId = "chat-" + user.Id.ToString("N");
        var now = clock.GetUtcNow().UtcDateTime;
        var aspirations = await db.Aspirations.Where(a => a.Status == "active").ToListAsync(ct);
        var made = await db.Pauses.FirstOrDefaultAsync(p => p.ClientId == clientId, ct);
        if (made is null)
        {
            var choice = options.For(CoachCallKind.PauseIntent);
            var input = new System.Text.Json.Nodes.JsonObject
            {
                ["today"] = DateOnly.FromDateTime(now).ToString("yyyy-MM-dd"),
                ["aspirations"] = new System.Text.Json.Nodes.JsonArray(aspirations.Select(a => (System.Text.Json.Nodes.JsonNode)
                    new System.Text.Json.Nodes.JsonObject { ["id"] = a.Id, ["shortName"] = a.ShortName }).ToArray()),
                ["message"] = user.Text,
            };
            var raw = new StringBuilder();
            await foreach (var chunk in model.StreamAsync(new CoachRequest(CoachCallKind.PauseIntent, choice.Provider, choice.Model,
                PauseIntentSystem, [new CoachTurn("user", input.ToJsonString())]), ct))
                raw.Append(chunk);

            var text = raw.ToString();
            var open = text.IndexOf('{');
            var close = text.LastIndexOf('}');
            if (open < 0 || close < open) return null;
            JsonElement pick;
            try { pick = JsonDocument.Parse(text[open..(close + 1)]).RootElement; }
            catch (JsonException) { return null; }
            if (pick.ValueKind != JsonValueKind.Object || !pick.TryGetProperty("pause", out var p) || p.ValueKind != JsonValueKind.Object) return null;

            Guid? aspirationId = null;
            if (p.TryGetProperty("aspirationId", out var a) && a.ValueKind == JsonValueKind.String)
            {
                if (!Guid.TryParse(a.GetString(), out var id) || aspirations.All(x => x.Id != id)) return null; // not an Aspiration of theirs: do nothing
                aspirationId = id;
            }
            var span = p.TryGetProperty("span", out var sp) && sp.ValueKind == JsonValueKind.String ? sp.GetString() : null;
            string? until = p.TryGetProperty("until", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
            if (span is not ("today" or "until" or "open") || (span == "until") != (until is not null)) return null;
            if (!Pauses.TryEnd(span == "open", span == "until" ? until : null, now, out var endsAt)) return null;

            made = new Pause
            {
                Id = Guid.NewGuid(), UserId = user.UserId, ClientId = clientId, AspirationId = aspirationId,
                StartsAt = now, EndsAt = endsAt, StartedBy = PauseStarters.User,
            };
            db.Pauses.Add(made);
            await Pauses.StartAsync(db, jobs, made, ct);
            await db.SaveChangesAsync(ct);
        }

        var scope = made.AspirationId is { } id2 ? "the Aspiration \"" + aspirations.FirstOrDefault(x => x.Id == id2)?.ShortName + "\"" : "everything";
        var ends = made.EndsAt is { } e ? "until " + DateOnly.FromDateTime(e.AddTicks(-1)).ToString("yyyy-MM-dd") + " (the end of that day)" : "until they resume";
        return $"a Pause of {scope}, {ends}.";
    }

    private static async Task Write(HttpContext http, string name, object data, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, JsonSerializerOptions.Web)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
