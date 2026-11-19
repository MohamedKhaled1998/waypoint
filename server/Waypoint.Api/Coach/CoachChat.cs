using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            var outcome = await IntentFromChatAsync(user, db, jobs, model, options.Value, clock, ct);
            if (outcome.Note is not null) system += " " + outcome.Note;
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
                Card = outcome.Card, CardJson = outcome.CardData?.ToJsonString(),
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

    private const string IntentSystem = """
        You read one message from a user of a coaching app and decide whether it asks to change their Plan. The first turn is
        JSON: "today" (a date), "aspirations" (id and shortName) and "message". Reply with only JSON, one of:
        {"pause": null}  when it asks for none of the below. Otherwise exactly one of:
        {"pause": {"aspirationId": <id from the list, or null for everything>, "span": "today" | "until" | "open", "until": "yyyy-MM-dd" (last day, only for "until"), "suggest": true | false}}
          when they want to step back. "suggest" is true when they only describe their situation ("I'm sick this week", "on holiday till the 20th")
          and false when they plainly ask for a Pause ("pause everything"). Use "open" when they give no end. Pick one Aspiration only when they clearly name it.
        {"budget_cut": {"until": "yyyy-MM-dd"}}  when they say the whole week is heavier for a while ("work is crazy this month"): the last day it lasts.
        {"replan": {"aspirationId": <id from the list, or null for all>}}  when they ask for any other change to the Plan ("make mornings shorter").
        """;

    /// <summary>What the chat found in a message: a line for the Coach's reply, and a card to put on it.</summary>
    private sealed record Outcome(string? Note, string? Card = null, JsonObject? CardData = null);

    private static readonly Outcome Nothing = new(null);

    /// <summary>
    /// The model only reads the message; the server decides. A plain request for a Pause starts it (no approval); a described situation suggests
    /// one on a card; a budget cut and a Re-plan become jobs that propose, and nothing changes until the user approves. Safe to run again on a resend.
    /// </summary>
    private static async Task<Outcome> IntentFromChatAsync(ChatMessage user, WaypointDb db, JobQueue jobs, ICoachModelClient model,
        CoachOptions options, TimeProvider clock, CancellationToken ct)
    {
        var clientId = "chat-" + user.Id.ToString("N");
        var now = clock.GetUtcNow().UtcDateTime;
        var aspirations = await db.Aspirations.Where(a => a.Status == "active").ToListAsync(ct);
        var made = await db.Pauses.FirstOrDefaultAsync(p => p.ClientId == clientId, ct);
        if (made is not null) return new(Started(made, aspirations));
        if (user.IntentJson is not null) return Describe(JsonNode.Parse(user.IntentJson)!.AsObject(), clientId, aspirations, now);

        var choice = options.For(CoachCallKind.PauseIntent);
        var input = new JsonObject
        {
            ["today"] = DateOnly.FromDateTime(now).ToString("yyyy-MM-dd"),
            ["aspirations"] = new JsonArray(aspirations.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id, ["shortName"] = a.ShortName }).ToArray()),
            ["message"] = user.Text,
        };
        var raw = new StringBuilder();
        await foreach (var chunk in model.StreamAsync(new CoachRequest(CoachCallKind.PauseIntent, choice.Provider, choice.Model,
            IntentSystem, [new CoachTurn("user", input.ToJsonString())]), ct))
            raw.Append(chunk);

        var intent = ReadIntent(raw.ToString(), aspirations, now);
        user.IntentJson = intent.ToJsonString();
        var kind = HandOff.Str(intent, "kind");
        if (kind == "pause_started")
        {
            var until = HandOff.Str(intent, "until");
            Pauses.TryEnd(HandOff.Str(intent, "span") == "open", until, now, out var endsAt);
            made = new Pause
            {
                Id = Guid.NewGuid(), UserId = user.UserId, ClientId = clientId, AspirationId = AspirationOf(intent),
                StartsAt = now, EndsAt = endsAt, StartedBy = PauseStarters.User,
            };
            db.Pauses.Add(made);
            await Pauses.StartAsync(db, jobs, made, ct);
        }
        else if (kind == "budget_cut")
            jobs.Add(JobKinds.BudgetCut, user.UserId, new Dictionary<string, Guid> { ["chatMessageId"] = user.Id });
        else if (kind == "replan")
            foreach (var id in (intent["aspirationIds"] as JsonArray ?? []).Select(i => Guid.Parse(i!.GetValue<string>())))
                jobs.Add(JobKinds.RePlan, user.UserId, new Dictionary<string, Guid> { ["chatMessageId"] = user.Id, ["aspirationId"] = id }, id);
        await db.SaveChangesAsync(ct);
        return made is not null ? new(Started(made, aspirations)) : Describe(intent, clientId, aspirations, now);
    }

    private static Guid? AspirationOf(JsonObject intent) => Guid.TryParse(HandOff.Str(intent, "aspirationId"), out var id) ? id : null;

    /// <summary>The model's reply as the server's own reading: only what it can check, and no free text.</summary>
    private static JsonObject ReadIntent(string text, List<Aspiration> aspirations, DateTime now)
    {
        var none = new JsonObject { ["kind"] = "none" };
        var open = text.IndexOf('{');
        var close = text.LastIndexOf('}');
        if (open < 0 || close < open) return none;
        JsonObject? root;
        try { root = JsonNode.Parse(text[open..(close + 1)]) as JsonObject; }
        catch (JsonException) { return none; }
        if (root is null) return none;
        var today = DateOnly.FromDateTime(now);

        Guid? Pick(JsonObject o, out bool bad)
        {
            bad = false;
            if (o["aspirationId"] is not JsonValue v || !v.TryGetValue<string>(out var s)) return null;
            if (!Guid.TryParse(s, out var id) || aspirations.All(x => x.Id != id)) { bad = true; return null; } // not an Aspiration of theirs: do nothing
            return id;
        }

        if (root["pause"] is JsonObject p)
        {
            var aspirationId = Pick(p, out var bad);
            var span = HandOff.Str(p, "span");
            var until = HandOff.Str(p, "until");
            if (bad || span is not ("today" or "until" or "open") || (span == "until") != (until is not null)) return none;
            if (!Pauses.TryEnd(span == "open", span == "until" ? until : null, now, out _)) return none;
            return new JsonObject
            {
                ["kind"] = p["suggest"] is JsonValue sv && sv.TryGetValue<bool>(out var sg) && sg ? "pause_suggested" : "pause_started",
                ["aspirationId"] = aspirationId?.ToString(), ["span"] = span, ["until"] = until,
            };
        }
        if (root["budget_cut"] is JsonObject cut)
            return BudgetCuts.ValidEnd(HandOff.Str(cut, "until"), today) is { } end
                ? new JsonObject { ["kind"] = "budget_cut", ["until"] = end.ToString("yyyy-MM-dd") } : none;
        if (root["replan"] is JsonObject re)
        {
            var one = Pick(re, out var bad);
            if (bad) return none;
            var targets = aspirations.Where(a => one is null || a.Id == one).Select(a => a.Id).ToList();
            return targets.Count == 0 ? none : new JsonObject { ["kind"] = "replan", ["aspirationIds"] = new JsonArray(targets.Select(t => (JsonNode)t.ToString()).ToArray()) };
        }
        return none;
    }

    private static Outcome Describe(JsonObject intent, string clientId, List<Aspiration> aspirations, DateTime now)
    {
        switch (HandOff.Str(intent, "kind"))
        {
            case "pause_suggested":
            {
                var span = HandOff.Str(intent, "span");
                var until = HandOff.Str(intent, "until");
                var aspirationId = AspirationOf(intent);
                var card = new JsonObject
                {
                    ["clientId"] = clientId, ["aspirationId"] = aspirationId?.ToString(), ["span"] = span,
                    ["endsOn"] = span == "until" ? until : null, ["open"] = span == "open",
                    ["shortName"] = aspirations.FirstOrDefault(a => a.Id == aspirationId)?.ShortName,
                };
                var scope = aspirationId is not null ? "the Aspiration \"" + card["shortName"] + "\"" : "everything";
                var ends = span switch { "until" => "until " + until + " (the end of that day)", "open" => "until they resume", _ => "for the rest of today" };
                return new("You are suggesting a Pause of " + scope + ", " + ends + ". Nothing has started: it starts when they tap the card under your reply. " +
                    "Say that in a line or two, warmly, and do not say it has begun.", "pause_suggestion", card);
            }
            case "budget_cut":
                return new("The user said work is heavy for a while. You are putting together a proposal for a temporary smaller Time budget (about " +
                    (int)(BudgetCuts.CutFraction * 100) + "% fewer hours) until " + HandOff.Str(intent, "until") + ", and it comes as a card in a moment. " +
                    "Nothing changes until they approve it. Say that in a line or two, warmly.");
            case "replan":
                return new("The user asked for a change to their Plan. You are redrawing it and it comes as a card in a moment. " +
                    "Nothing changes until they review and approve it. Say that in a line or two, warmly, and do not describe the change yet.");
            default:
                return Nothing;
        }
    }

    private static string Started(Pause made, List<Aspiration> aspirations)
    {
        var scope = made.AspirationId is { } id2 ? "the Aspiration \"" + aspirations.FirstOrDefault(x => x.Id == id2)?.ShortName + "\"" : "everything";
        var ends = made.EndsAt is { } e ? "until " + DateOnly.FromDateTime(e.AddTicks(-1)).ToString("yyyy-MM-dd") + " (the end of that day)" : "until they resume";
        return "The user asked for a Pause in this message and it has already started, with no approval needed: a Pause of " + $"{scope}, {ends}." +
            " Confirm it in a line or two, warmly, and say they can resume any time. Do not say you will need to do anything.";
    }

    private static async Task Write(HttpContext http, string name, object data, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, JsonSerializerOptions.Web)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}
