using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public sealed record StartInterviewRequest(string? OwnWords);
public sealed record InterviewMessageRequest(string? Text, string? ClientId);
public sealed record ConfirmInterviewRequest(string? ShortName);

public static class Interviews
{
    private const int MaxActiveAspirations = 3;
    private const int MaxAttempts = 3;
    private static readonly string[] Hues = ["coral", "teal", "amber"];

    public static void MapInterviews(this WebApplication app)
    {
        var g = app.MapGroup("/interviews").RequireAuthorization();
        g.MapPost("/", Start);
        g.MapGet("/current", async (WaypointDb db, CancellationToken ct) =>
        {
            var i = await db.Interviews.Where(x => x.Status != InterviewStatus.Confirmed)
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            return i is null ? Results.NotFound() : Results.Ok(await ViewAsync(db, i, ct));
        });
        g.MapGet("/{id:guid}", async (Guid id, WaypointDb db, CancellationToken ct) =>
            await db.Interviews.FirstOrDefaultAsync(x => x.Id == id, ct) is { } i
                ? Results.Ok(await ViewAsync(db, i, ct)) : Results.NotFound());
        g.MapPost("/{id:guid}/messages", Say);
        g.MapPost("/{id:guid}/resume", Resume);
        g.MapPost("/{id:guid}/confirm", Confirm);

        app.MapGet("/aspirations", async (WaypointDb db, CancellationToken ct) =>
            Results.Ok(await db.Aspirations.OrderBy(a => a.CreatedAt).Select(a => new
            {
                a.Id, a.OwnWords, a.ShortName, a.Hue, a.Status, a.ShareHoursPerWeek, a.HopedForDate,
                a.SettleForDate, a.SettleForPicture, a.OnSkip,
            }).ToListAsync(ct))).RequireAuthorization();

        app.MapGet("/me/setup", async (WaypointDb db, CancellationToken ct) =>
        {
            var u = await db.Users.SingleAsync(ct);
            var slots = (await db.Slots.OrderBy(s => s.Start).ToListAsync(ct))
                .Select(s => new { s.Id, s.Label, Days = s.Days.Split(','), s.Start, s.End });
            return Results.Ok(new { u.TimeBudgetHours, u.ContactFrequency, Done = u.SetupAt != null, Slots = slots });
        }).RequireAuthorization();
    }

    private static async Task<object> ViewAsync(WaypointDb db, Interview i, CancellationToken ct)
    {
        var messages = await db.InterviewMessages.Where(m => m.InterviewId == i.Id).OrderBy(m => m.Seq)
            .Select(m => new { m.Id, m.Role, m.Text, m.ClientId }).ToListAsync(ct);
        return new
        {
            i.Id, i.Status, i.OwnWords, i.AspirationId,
            IsFirst = await IsFirstAsync(db, ct),
            HandOff = i.HandOffJson is null ? null : JsonNode.Parse(i.HandOffJson),
            Messages = messages,
        };
    }

    private static async Task<bool> IsFirstAsync(WaypointDb db, CancellationToken ct) =>
        (await db.Users.Select(u => u.SetupAt).SingleAsync(ct)) is null;

    private static async Task<IResult> Start(StartInterviewRequest req, WaypointDb db, CurrentUser me, TimeProvider clock, CancellationToken ct)
    {
        var words = req.OwnWords?.Trim();
        if (string.IsNullOrEmpty(words) || words.Length > 500) return Results.BadRequest();

        // One Interview at a time: starting again picks the unfinished one back up.
        var open = await db.Interviews.Where(x => x.Status != InterviewStatus.Confirmed)
            .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
        if (open is not null) return Results.Ok(await ViewAsync(db, open, ct));

        if (await db.Aspirations.CountAsync(a => a.Status == "active", ct) >= MaxActiveAspirations)
            return Results.Conflict(new { error = "too_many_active_aspirations" });

        var i = new Interview
        {
            Id = Guid.NewGuid(), UserId = me.Id!.Value, OwnWords = words, Status = InterviewStatus.InProgress,
            CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.Interviews.Add(i);
        await db.SaveChangesAsync(ct);
        return Results.Created($"/interviews/{i.Id}", await ViewAsync(db, i, ct));
    }

    private static async Task Say(Guid id, InterviewMessageRequest req, HttpContext http, WaypointDb db, CurrentUser me,
        ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock, ILoggerFactory logs)
    {
        var ct = http.RequestAborted;
        if (string.IsNullOrWhiteSpace(req.Text) || string.IsNullOrWhiteSpace(req.ClientId) || req.ClientId.Length > 64)
        {
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var interview = await db.Interviews.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (interview is null || interview.Status == InterviewStatus.Confirmed)
        {
            http.Response.StatusCode = interview is null ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict;
            return;
        }

        var user = await db.InterviewMessages.FirstOrDefaultAsync(m => m.ClientId == req.ClientId, ct);
        if (user is null)
        {
            user = new InterviewMessage
            {
                Id = Guid.NewGuid(), UserId = me.Id!.Value, InterviewId = id, Role = "user", Text = req.Text,
                ClientId = req.ClientId, CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.InterviewMessages.Add(user);
            // "Correct something": talking again reopens a picture that was awaiting confirmation.
            interview.Status = InterviewStatus.InProgress;
            await db.SaveChangesAsync(ct);
        }

        await StartSse(http, ct);
        var existing = await db.InterviewMessages.FirstOrDefaultAsync(m => m.InReplyToId == user.Id, ct);
        if (existing is not null)
        {
            await Write(http, "delta", new { text = existing.Text }, ct);
            await Write(http, "done", new { messageId = existing.Id, status = interview.Status }, ct);
            return;
        }
        await RunTurn(http, db, interview, user.Id, null, model, options, clock, logs.CreateLogger("Waypoint.Coach.Interview"), ct);
    }

    private static async Task Resume(Guid id, HttpContext http, WaypointDb db, ICoachModelClient model,
        IOptions<CoachOptions> options, TimeProvider clock, ILoggerFactory logs)
    {
        var ct = http.RequestAborted;
        var interview = await db.Interviews.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (interview is null || interview.Status == InterviewStatus.Confirmed)
        {
            http.Response.StatusCode = interview is null ? StatusCodes.Status404NotFound : StatusCodes.Status409Conflict;
            return;
        }
        await StartSse(http, ct);
        var hasCoach = await db.InterviewMessages.AnyAsync(m => m.InterviewId == id && m.Role == "coach", ct);
        await RunTurn(http, db, interview, null, hasCoach ? InterviewPrompt.Resume : InterviewPrompt.Begin,
            model, options, clock, logs.CreateLogger("Waypoint.Coach.Interview"), ct);
    }

    /// <summary>One Coach turn: stream the visible reply, read the hand-off block, ask again when it is invalid.</summary>
    private static async Task RunTurn(HttpContext http, WaypointDb db, Interview interview, Guid? inReplyTo, string? instruction,
        ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock, ILogger log, CancellationToken ct)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var isFirst = await IsFirstAsync(db, ct);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var choice = options.Value.For(CoachCallKind.Interview);
            var stored = await db.InterviewMessages.Where(m => m.InterviewId == interview.Id).OrderBy(m => m.Seq).ToListAsync(ct);
            var turns = new List<CoachTurn> { new("user", interview.OwnWords) };
            turns.AddRange(stored.Select(m => new CoachTurn(m.Role == "user" ? "user" : "assistant", m.Text)));
            if (instruction is not null) turns.Add(new CoachTurn("user", instruction));
            var system = InterviewPrompt.For(isFirst);

            string? visible = null;
            JsonNode? handOff = null;
            var ready = false;
            for (var attempt = 1; attempt <= MaxAttempts; attempt++)
            {
                var splitter = new HandOffSplitter();
                var full = new StringBuilder();
                await foreach (var chunk in model.StreamAsync(
                    new CoachRequest(CoachCallKind.Interview, choice.Provider, choice.Model, system, turns), ct))
                {
                    full.Append(chunk);
                    // Only the first attempt reaches the phone; a retry is silent.
                    var show = splitter.Feed(chunk);
                    if (attempt == 1 && show.Length > 0) await Write(http, "delta", new { text = show }, ct);
                }
                var tail = splitter.Finish();
                if (attempt == 1 && tail.Length > 0) await Write(http, "delta", new { text = tail }, ct);
                if (full.Length == 0) throw new CoachUnavailableException("empty_reply");

                var (text, block) = HandOffSplitter.Split(full.ToString());
                visible ??= text;
                if (block is null) { handOff = null; ready = false; break; }

                JsonNode? parsed = null;
                var problems = new List<string>();
                try { parsed = JsonNode.Parse(block); }
                catch (JsonException) { problems.Add("hand_off_not_json"); }

                var wantsReady = parsed?["ready"] is JsonValue r && r.TryGetValue<bool>(out var b) && b;
                if (problems.Count == 0 && wantsReady) problems = HandOff.Validate(parsed, isFirst, today);
                if (problems.Count == 0) { handOff = parsed; ready = wantsReady; break; }

                log.LogInformation("Interview {InterviewId} hand-off rejected on attempt {Attempt}: {Problems}",
                    interview.Id, attempt, string.Join(",", problems));
                if (attempt == MaxAttempts) throw new CoachUnavailableException("hand_off_invalid");
                turns.Add(new CoachTurn("assistant", full.ToString()));
                turns.Add(new CoachTurn("user", InterviewPrompt.Rejected(problems)));
            }

            var coach = new InterviewMessage
            {
                Id = Guid.NewGuid(), UserId = interview.UserId, InterviewId = interview.Id, Role = "coach",
                Text = visible!.Trim(), InReplyToId = inReplyTo, CreatedAt = clock.GetUtcNow().UtcDateTime,
            };
            db.InterviewMessages.Add(coach);
            if (handOff is not null) interview.HandOffJson = handOff.ToJsonString();
            interview.Status = ready ? InterviewStatus.AwaitingConfirmation : InterviewStatus.InProgress;
            await db.SaveChangesAsync(ct);
            log.LogInformation("Interview {InterviewId} coach turn {MessageId} in {ElapsedMs} ms, status {Status}",
                interview.Id, coach.Id, watch.ElapsedMilliseconds, interview.Status);
            await Write(http, "done", new { messageId = coach.Id, status = interview.Status, handOff }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            log.LogInformation("Interview {InterviewId} request aborted by the phone", interview.Id);
        }
        catch (Exception ex)
        {
            log.LogWarning("Interview {InterviewId} Coach call failed after {ElapsedMs} ms: {ErrorType}",
                interview.Id, watch.ElapsedMilliseconds, ex.GetType().Name);
            await Write(http, "error", new { type = "coach_unavailable" }, CancellationToken.None);
        }
    }

    private static async Task<IResult> Confirm(Guid id, ConfirmInterviewRequest req, WaypointDb db, CurrentUser me, JobQueue jobs, TimeProvider clock, CancellationToken ct)
    {
        var interview = await db.Interviews.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (interview is null) return Results.NotFound();
        if (interview.Status != InterviewStatus.AwaitingConfirmation || interview.HandOffJson is null)
            return Results.Conflict(new { error = "not_awaiting_confirmation" });

        var now = clock.GetUtcNow().UtcDateTime;
        var user = await db.Users.SingleAsync(ct);
        var first = user.SetupAt is null;
        var handOff = JsonNode.Parse(interview.HandOffJson);
        var shortName = req.ShortName?.Trim();
        var problems = HandOff.Validate(handOff, first, DateOnly.FromDateTime(now), shortName);
        if (problems.Count > 0) return Results.UnprocessableEntity(new { error = "invalid_hand_off", problems });
        if (await db.Aspirations.CountAsync(a => a.Status == "active", ct) >= MaxActiveAspirations)
            return Results.Conflict(new { error = "too_many_active_aspirations" });

        var a = handOff!["aspiration"]!.AsObject();
        var used = await db.Aspirations.Where(x => x.Status == "active").Select(x => x.Hue).ToListAsync(ct);
        var settleDate = HandOff.Str(a, "settle_for_date");
        var aspiration = new Aspiration
        {
            Id = Guid.NewGuid(), UserId = me.Id!.Value,
            OwnWords = interview.OwnWords,
            ShortName = shortName ?? HandOff.Str(a, "short_name")!.Trim(),
            Hue = Hues.FirstOrDefault(h => !used.Contains(h)) ?? Hues[0],
            Status = "active",
            ShareHoursPerWeek = HandOff.Num(a, "share_hours_per_week")!.Value,
            HopedForDate = DateOnly.ParseExact(HandOff.Str(a, "hoped_for_date")!, "yyyy-MM-dd"),
            SettleForDate = string.IsNullOrWhiteSpace(settleDate) ? null : DateOnly.ParseExact(settleDate, "yyyy-MM-dd"),
            SettleForPicture = string.IsNullOrWhiteSpace(HandOff.Str(a, "settle_for_picture")) ? null : HandOff.Str(a, "settle_for_picture"),
            OnSkip = HandOff.Str(a, "on_skip")!,
            CreatedAt = now,
        };
        db.Aspirations.Add(aspiration);

        if (first)
        {
            var u = handOff["user"]!.AsObject();
            foreach (var s in u["slots"]!.AsArray().Select(s => s!.AsObject()))
                db.Slots.Add(new Slot
                {
                    Id = Guid.NewGuid(), UserId = user.Id, Label = HandOff.Str(s, "label")!,
                    Days = string.Join(",", s["days"]!.AsArray().Select(d => d!.GetValue<string>())),
                    Start = HandOff.Str(s, "start")!, End = HandOff.Str(s, "end")!,
                });
            user.TimeBudgetHours = HandOff.Num(u, "time_budget_hours_per_week");
            user.ContactFrequency = HandOff.Str(u, "contact_frequency");
            user.SetupAt = now;
        }

        interview.Status = InterviewStatus.Confirmed;
        interview.AspirationId = aspiration.Id;

        var card = new JsonObject
        {
            ["interviewId"] = interview.Id, ["aspirationId"] = aspiration.Id, ["shortName"] = aspiration.ShortName,
            ["hue"] = aspiration.Hue, ["picture"] = HandOff.Str(a, "picture"),
            ["hopedForDate"] = HandOff.Str(a, "hoped_for_date"), ["settleForDate"] = settleDate,
            ["settleForPicture"] = aspiration.SettleForPicture,
            ["openQuestions"] = a["open_questions"]?.DeepClone() ?? new JsonArray(),
        };
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(), UserId = user.Id, Role = "coach", Text = HandOff.Str(a, "picture")!,
            AspirationId = aspiration.Id, Card = "interview_summary", CardJson = card.ToJsonString(), CreatedAt = now,
        });
        jobs.Add(JobKinds.Plan, user.Id, new Dictionary<string, Guid> { ["aspirationId"] = aspiration.Id }, aspiration.Id);
        await db.SaveChangesAsync(ct);
        return Results.Ok(new { aspirationId = aspiration.Id, aspiration.ShortName, aspiration.Hue });
    }

    private static async Task StartSse(HttpContext http, CancellationToken ct)
    {
        http.Response.ContentType = "text/event-stream";
        http.Response.Headers.CacheControl = "no-cache";
        await http.Response.StartAsync(ct);
    }

    private static async Task Write(HttpContext http, string name, object data, CancellationToken ct)
    {
        await http.Response.WriteAsync($"event: {name}\ndata: {JsonSerializer.Serialize(data, JsonSerializerOptions.Web)}\n\n", ct);
        await http.Response.Body.FlushAsync(ct);
    }
}

/// <summary>Streams the visible part of a reply and holds back everything from the hand-off marker on.</summary>
public sealed class HandOffSplitter(string marker = InterviewPrompt.Marker)
{
    private readonly string _marker = marker;
    private readonly StringBuilder _pending = new();
    private bool _inBlock;

    public string Feed(string chunk)
    {
        if (_inBlock) return "";
        _pending.Append(chunk);
        var s = _pending.ToString();
        var at = s.IndexOf(_marker, StringComparison.Ordinal);
        if (at >= 0)
        {
            _inBlock = true;
            _pending.Clear();
            return s[..at].TrimEnd();
        }
        // Keep back a tail that could be the start of the marker.
        var keep = 0;
        for (var n = Math.Min(_marker.Length - 1, s.Length); n > 0; n--)
            if (s.EndsWith(_marker[..n], StringComparison.Ordinal)) { keep = n; break; }
        _pending.Clear().Append(s[^keep..]);
        return s[..^keep];
    }

    public string Finish() => _inBlock ? "" : _pending.ToString();

    public static (string Text, string? Block) Split(string full, string marker = InterviewPrompt.Marker)
    {
        var at = full.IndexOf(marker, StringComparison.Ordinal);
        if (at < 0) return (full, null);
        var block = full[(at + marker.Length)..].Trim();
        // Tolerate a fenced block.
        if (block.StartsWith("```", StringComparison.Ordinal))
            block = StripJsonTag(block.Trim('`').Trim());
        return (full[..at], block);
    }

    private static string StripJsonTag(string s) =>
        s.StartsWith("json", StringComparison.OrdinalIgnoreCase) ? s[4..].Trim() : s;
}
