using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public static class ReminderPrompt
{
    public const string System = """
        You write the notification text for reminders in Waypoint, a personal coaching app, in the Coach's voice:
        warm, plain, brief, forgiving, never strict. Each reminder is one or two short sentences (under 140
        characters) in the second person. No emojis, no motivational quotes, no clock times, no guilt.

        The first message is JSON: {"actions":[{"id":"","title":"","minutes":0,"when":"now | get_ready"}]}.
        "now" reminders arrive as the Action begins; "get_ready" ones arrive ahead of it, so say what to prepare.
        Reply with exactly one fenced json block and nothing else:

        ```json
        { "reminders": [{ "id": "the same id", "text": "" }] }
        ```
        """;
}

/// <summary>
/// Writes the reminder text for a Plan version's scheduled Actions in one batch on the light model and stores it with
/// each Action, so a reminder needs no model call. A failure throws, and the job retries with backoff; nothing waits on it.
/// </summary>
public sealed class ReminderWriter(WaypointDb db, ICoachModelClient model, IOptions<CoachOptions> options)
{
    private const int BatchSize = 40;
    private const int MaxText = 300;

    public async Task WriteAsync(Guid planVersionId, CancellationToken ct)
    {
        var pending = await db.ScheduledActions.Where(a => a.PlanVersionId == planVersionId && a.ReminderText == null)
            .OrderBy(a => a.Date).ThenBy(a => a.Id).ToListAsync(ct);
        var choice = options.Value.For(CoachCallKind.ReminderText);
        foreach (var batch in pending.Chunk(BatchSize))
        {
            var brief = new JsonObject
            {
                ["actions"] = new JsonArray(batch.Select(a => (JsonNode)new JsonObject
                {
                    ["id"] = a.Id.ToString(), ["title"] = a.Title, ["minutes"] = a.Minutes, ["when"] = a.ReminderKind,
                }).ToArray()),
            };
            var reply = new StringBuilder();
            await foreach (var chunk in model.StreamAsync(new CoachRequest(CoachCallKind.ReminderText, choice.Provider, choice.Model,
                ReminderPrompt.System, [new CoachTurn("user", brief.ToJsonString())]), ct))
                reply.Append(chunk);

            var texts = Parse(reply.ToString());
            var written = 0;
            foreach (var a in batch)
            {
                if (!texts.TryGetValue(a.Id, out var text)) continue;
                a.ReminderText = text.Length > MaxText ? text[..MaxText] : text;
                written++;
            }
            if (written == 0) throw new CoachUnavailableException("no_reminder_text");
            await db.SaveChangesAsync(ct);
        }
        // Some Actions the model skipped: fail so the retry asks for just those.
        if (pending.Any(a => a.ReminderText == null)) throw new CoachUnavailableException("reminder_text_incomplete");
    }

    private static Dictionary<Guid, string> Parse(string reply)
    {
        var result = new Dictionary<Guid, string>();
        var from = reply.IndexOf('{');
        var to = reply.LastIndexOf('}');
        if (from < 0 || to <= from) return result;
        try
        {
            if (JsonNode.Parse(reply[from..(to + 1)])?["reminders"] is not JsonArray list) return result;
            foreach (var item in list.OfType<JsonObject>())
                if (Guid.TryParse(HandOff.Str(item, "id"), out var id) && HandOff.Str(item, "text") is { Length: > 0 } text)
                    result[id] = text.Trim();
        }
        catch (JsonException) { }
        return result;
    }
}
