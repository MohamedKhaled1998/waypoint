using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>One reading of an Aspiration's Measure. A check on the Plan, never a gate: nothing in Pace, Projection or a Milestone's state reads it. User-owned.</summary>
public sealed class MeasureReading
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid AspirationId { get; set; }
    /// <summary>The Milestone it was read at, when it was.</summary>
    public string? MilestoneRef { get; set; }
    public DateOnly Date { get; set; }
    public double Value { get; set; }
    /// <summary>plan_accepted, manual or chat_reply.</summary>
    public required string Source { get; set; }
    /// <summary>Set by the phone, so a resend is idempotent.</summary>
    public string? ClientId { get; set; }
    public DateTime CreatedAt { get; set; }
}

public static class MeasureSources
{
    public const string PlanAccepted = "plan_accepted", Manual = "manual", ChatReply = "chat_reply";
}

public static class MeasureVerdicts
{
    public const string TooFew = "too_few";
    public const string RightWay = "right_way";
    /// <summary>Moving the right way, but slower than the hoped-for value needs.</summary>
    public const string Slower = "slower";
    public const string Flat = "flat";
    public const string WrongWay = "wrong_way";
}

public sealed record Point(DateOnly Date, double Value);

public sealed record Trend(string Direction, double Change, DateOnly? Since, string Verdict);

/// <summary>Reads a Measure's trend against the hoped-for value. Deterministic server code: the Coach only phrases it.</summary>
public static class MeasureMath
{
    /// <summary>Progress below this share of the way to the hoped-for value counts as flat.</summary>
    public const double FlatShare = 0.05;
    /// <summary>Moving slower than this share of the pace the hoped-for date needs counts as lagging.</summary>
    public const double LagShare = 0.75;
    /// <summary>Flat or wrong way for about this long is enough for a re-think, without waiting for a second Milestone.</summary>
    public const int RethinkDays = 42;
    /// <summary>The Coach responds to a Measure at most this often.</summary>
    public const int ResponseCooldownDays = 28;
    /// <summary>The Coach asks for a reading at the refresh when the last is this old.</summary>
    public const int AskAfterDays = 28;

    public static Trend Assess(IReadOnlyList<Point> readings, double start, double hopedFor, DateOnly target)
    {
        var pts = readings.OrderBy(p => p.Date).ToList();
        if (pts.Count < 2 || start == hopedFor) return new Trend("flat", 0, pts.FirstOrDefault()?.Date, MeasureVerdicts.TooFew);
        var first = pts[0];
        var last = pts[^1];
        var change = Math.Round(last.Value - first.Value, 2);
        var direction = change > 0 ? "up" : change < 0 ? "down" : "flat";
        var span = hopedFor - start;
        var share = (last.Value - first.Value) / span; // 1 = all the way there
        if (share < 0) return new Trend(direction, change, first.Date, MeasureVerdicts.WrongWay);
        if (share < FlatShare) return new Trend(direction, change, first.Date, MeasureVerdicts.Flat);
        var needed = Math.Max(target.DayNumber - first.Date.DayNumber, 1);
        var elapsed = Math.Min((last.Date.DayNumber - first.Date.DayNumber) / (double)needed, 1);
        return new Trend(direction, change, first.Date, share < LagShare * elapsed ? MeasureVerdicts.Slower : MeasureVerdicts.RightWay);
    }

    public static bool Stalled(Trend t) => t.Verdict is MeasureVerdicts.Flat or MeasureVerdicts.WrongWay;

    /// <summary>The trend carried on to a date, one decimal: where a later hoped-for value would sit if the work keeps moving at this rate.</summary>
    public static double Extend(IReadOnlyList<Point> readings, DateOnly date)
    {
        var pts = readings.OrderBy(p => p.Date).ToList();
        var days = Math.Max(pts[^1].Date.DayNumber - pts[0].Date.DayNumber, 1);
        var perDay = (pts[^1].Value - pts[0].Value) / days;
        return Math.Round(pts[^1].Value + perDay * Math.Max(date.DayNumber - pts[^1].Date.DayNumber, 0), 1);
    }
}

/// <summary>What the server knows of one Aspiration's Measure as of today.</summary>
public sealed record MeasureState(IReadOnlyList<Milestone> Measured, IReadOnlyList<MeasureReading> Readings)
{
    public bool Has => Measured.Count > 0;
    public Milestone? Next => Measured.FirstOrDefault(m => m.State != MilestoneService.Reached) ?? Measured.LastOrDefault();
    public IReadOnlyList<Point> Points(DateOnly? upTo = null) => Readings.Where(r => upTo is null || r.Date <= upTo).Select(r => new Point(r.Date, r.Value)).ToList();

    public Trend TrendNow() => Next is { MeasureStart: { } s, MeasureHopedFor: { } h } n ? MeasureMath.Assess(Points(), s, h, n.Date) : new Trend("flat", 0, null, MeasureVerdicts.TooFew);

    /// <summary>The last two Milestones whose date has passed with a Measure: did both see it flat or the wrong way?</summary>
    public bool StalledAtTwoMilestones(DateOnly today)
    {
        var passed = Measured.Where(m => m.Date <= today && m.MeasureStart is not null && m.MeasureHopedFor is not null).OrderByDescending(m => m.Date).Take(2).ToList();
        return passed.Count == 2 && passed.All(m => MeasureMath.Stalled(MeasureMath.Assess(Points(m.Date), m.MeasureStart!.Value, m.MeasureHopedFor!.Value, m.Date)));
    }

    public bool StalledForWeeks(DateOnly today)
    {
        var pts = Points();
        return pts.Count >= 2 && today.DayNumber - pts.Min(p => p.Date).DayNumber >= MeasureMath.RethinkDays && MeasureMath.Stalled(TrendNow());
    }
}

public sealed class MeasureStore(WaypointDb db, TimeProvider clock)
{
    public DateOnly Today => DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);

    public async Task<MeasureState> LoadAsync(Guid aspirationId, CancellationToken ct)
    {
        var current = await db.PlanVersions.Where(p => p.AspirationId == aspirationId && p.Status == PlanVersionStatus.Approved).Select(p => p.Id).FirstOrDefaultAsync(ct);
        var measured = await db.Milestones.Where(m => m.PlanVersionId == current && m.MeasureName != null).OrderBy(m => m.Date).ToListAsync(ct);
        var readings = await db.MeasureReadings.Where(r => r.AspirationId == aspirationId).OrderBy(r => r.Date).ThenBy(r => r.CreatedAt).ToListAsync(ct);
        return new MeasureState(measured, readings);
    }
}

public sealed record ReadingRequest(double? Value, DateOnly? Date, string? ClientId);

public static class Measures
{
    public static object View(MeasureReading r) => new { r.Id, r.Date, r.Value, r.Source, r.MilestoneRef };

    public static void MapMeasures(this WebApplication app)
    {
        var g = app.MapGroup("/aspirations/{aspirationId:guid}/measure").RequireAuthorization();

        g.MapGet("/", async (Guid aspirationId, WaypointDb db, MeasureStore store, CancellationToken ct) =>
        {
            if (!await db.Aspirations.AnyAsync(a => a.Id == aspirationId && a.Status != "closed", ct)) return Results.NotFound();
            var s = await store.LoadAsync(aspirationId, ct);
            if (!s.Has) return Results.Ok(new { HasMeasure = false, Readings = Array.Empty<object>() });
            var trend = s.TrendNow();
            var next = s.Next!;
            return Results.Ok(new
            {
                HasMeasure = true, Name = next.MeasureName, Unit = next.MeasureUnit, Start = next.MeasureStart, HopedFor = next.MeasureHopedFor, HopedForDate = next.Date,
                Milestones = s.Measured.Select(m => new { m.Ref, m.Title, m.Date, m.State, HopedFor = m.MeasureHopedFor }),
                Readings = s.Readings.Select(View),
                Trend = new { trend.Direction, trend.Change, trend.Since, trend.Verdict },
            });
        });

        // "Add reading", any time. A resend with the same client ID changes nothing.
        g.MapPost("/readings", async (Guid aspirationId, ReadingRequest req, WaypointDb db, MeasureStore store, TimeProvider clock, JobQueue jobs, Auth.CurrentUser me, CancellationToken ct) =>
        {
            if (req.Value is not { } value || !double.IsFinite(value) || Math.Abs(value) > 1e9 || req.ClientId is { Length: > 64 }) return Results.BadRequest();
            if (!await db.Aspirations.AnyAsync(a => a.Id == aspirationId && a.Status != "closed", ct)) return Results.NotFound();
            if (req.ClientId is not null && await db.MeasureReadings.FirstOrDefaultAsync(r => r.ClientId == req.ClientId, ct) is { } same) return Results.Ok(View(same));
            var s = await store.LoadAsync(aspirationId, ct);
            if (!s.Has) return Results.Conflict(new { error = "no_measure" });
            var date = req.Date ?? store.Today;
            if (date > store.Today) return Results.BadRequest();
            var reading = Add(db, clock, me.Id!.Value, aspirationId, value, date, MeasureSources.Manual, req.ClientId, s.Next?.Ref);
            jobs.Add(JobKinds.MeasureCheck, reading.UserId, new Dictionary<string, Guid> { ["readingId"] = reading.Id }, aspirationId);
            await db.SaveChangesAsync(ct);
            return Results.Ok(View(reading));
        });

        // "Reset later hoped-for values to the trend": the user's yes to the Coach's proposal. The values are worked out again here, never taken from the phone.
        g.MapPost("/accept-reset", async (Guid aspirationId, WaypointDb db, MeasureStore store, CancellationToken ct) =>
        {
            var s = await store.LoadAsync(aspirationId, ct);
            if (!s.Has) return Results.NotFound();
            // Only the Coach's outstanding proposal can be accepted, and once.
            var proposal = await db.ChatMessages.Where(m => m.AspirationId == aspirationId && m.Card == "measure_reset").OrderByDescending(m => m.Seq).FirstOrDefaultAsync(ct);
            var card = proposal?.CardJson is null ? null : JsonNode.Parse(proposal.CardJson)?.AsObject();
            if (proposal is null || card is null || card["accepted"]?.GetValue<bool>() == true) return Results.Conflict(new { error = "no_proposal" });
            card["accepted"] = true;
            proposal.CardJson = card.ToJsonString();
            foreach (var (m, to) in MeasureNotes.ResetValues(s, store.Today)) m.MeasureHopedFor = to;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { Milestones = s.Measured.Select(m => new { m.Ref, HopedFor = m.MeasureHopedFor }) });
        });
    }

    public static MeasureReading Add(WaypointDb db, TimeProvider clock, Guid userId, Guid aspirationId, double value, DateOnly date, string source, string? clientId, string? milestoneRef)
    {
        var r = new MeasureReading
        {
            Id = Guid.NewGuid(), UserId = userId, AspirationId = aspirationId, MilestoneRef = milestoneRef, Date = date, Value = value, Source = source,
            ClientId = clientId, CreatedAt = clock.GetUtcNow().UtcDateTime,
        };
        db.MeasureReadings.Add(r);
        return r;
    }

    /// <summary>A reply that is essentially one number, with the Measure's unit after it if any: "68", "62,5", "68 bpm".</summary>
    private static readonly Regex Reply = new(@"^\s*(?<n>-?\d+(?:[.,]\d+)?)\s*(?<unit>[\p{L}%°/ ]{0,24}?)\s*\.?\s*$", RegexOptions.Compiled);

    /// <summary>
    /// A reply to the Coach's "what's your reading?" is saved as a reading when it is just a number (and the Measure's unit, if given), and
    /// its check is queued like any reading's. Anything else is left as chat. Stored with the user message, in the caller's save.
    /// </summary>
    public static async Task CaptureReplyAsync(WaypointDb db, TimeProvider clock, JobQueue jobs, ChatMessage reply, CancellationToken ct)
    {
        var asked = await db.ChatMessages.Where(m => m.Seq < reply.Seq).OrderByDescending(m => m.Seq).FirstOrDefaultAsync(ct);
        if (asked is not { Card: "measure_ask", AspirationId: { } aspirationId }) return;
        var hit = Reply.Match(reply.Text);
        if (!hit.Success || !double.TryParse(hit.Groups["n"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return;
        var s = await new MeasureStore(db, clock).LoadAsync(aspirationId, ct);
        if (!s.Has) return;
        var unit = hit.Groups["unit"].Value.Trim();
        if (unit.Length > 0 && !string.Equals(unit.Replace(" ", ""), (s.Next?.MeasureUnit ?? "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase)) return;
        var reading = Add(db, clock, reply.UserId, aspirationId, value, DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime), MeasureSources.ChatReply, "chat-" + reply.Id.ToString("N"), s.Next?.Ref);
        jobs.Add(JobKinds.MeasureCheck, reading.UserId, new Dictionary<string, Guid> { ["readingId"] = reading.Id }, aspirationId);
    }
}

public static class MeasurePrompt
{
    public const string System = """
        You are the Coach in Waypoint, a personal coaching app. The first message is JSON from the app about the
        user's Measure: a number kept next to a Milestone to check whether the Plan is working. It is a check on the
        Plan, never a test of the user, and never a condition for reaching a Milestone. The app has decided what
        happened; "say" tells you what to write. Reply with two or three short plain sentences, nothing else, no
        emojis, no guilt. Comment on the trend against the Plan, never praise or scold a single number.

        - ask: ask for a new reading of the Measure ("name" in "unit"), lightly. One question.
        - lagging: it is moving the right way, slower than hoped. Say you suggest moving the later hoped-for values
          to match the trend, and that the work stays as it is. Never suggest more work or a changed Settle-for line.
        - rethink: it has stayed flat or gone the wrong way. Say the Plan's work may not be the right work yet and
          offer to think about what the work is. Only when the Measure is about health or the body, say a professional
          could help too. Never suggest more work, and never talk about a Settle-for choice.

        Never invent facts or numbers.
        """;
}

/// <summary>The Coach's words about a Measure: asking for a reading, and the response to a lagging or stalled one.</summary>
public sealed class MeasureNotes(WaypointDb db, MeasureStore store, MilestoneNotes phrasing, TimeProvider clock)
{
    /// <summary>Later hoped-for values moved to the trend, only where the trend falls short of what they asked of it.</summary>
    public static IReadOnlyList<(Milestone Milestone, double To)> ResetValues(MeasureState s, DateOnly today)
    {
        var points = s.Points();
        if (points.Count < 2) return [];
        var list = new List<(Milestone, double)>();
        foreach (var m in s.Measured.Where(m => m.State != MilestoneService.Reached && m.Date > today && m.MeasureStart is not null && m.MeasureHopedFor is not null))
        {
            var to = MeasureMath.Extend(points, m.Date);
            var up = m.MeasureHopedFor > m.MeasureStart;
            // Only lowered: a trend that gets there anyway leaves the hoped-for value alone.
            if (up ? to < m.MeasureHopedFor : to > m.MeasureHopedFor) list.Add((m, to));
        }
        return list;
    }

    public async Task CheckAsync(Guid readingId, CancellationToken ct)
    {
        var reading = await db.MeasureReadings.FirstOrDefaultAsync(r => r.Id == readingId, ct);
        if (reading is null) return;
        var s = await store.LoadAsync(reading.AspirationId, ct);
        if (!s.Has || s.Next is not { } next) return;
        var today = store.Today;
        var now = clock.GetUtcNow().UtcDateTime;
        var trend = s.TrendNow();
        var rethink = MeasureMath.Stalled(trend) && (s.StalledAtTwoMilestones(today) || s.StalledForWeeks(today));
        var lagging = trend.Verdict == MeasureVerdicts.Slower;
        if (!rethink && !lagging) return;
        // A reset with nothing to reset is no proposal, and must not start the cooldown.
        IReadOnlyList<(Milestone Milestone, double To)> resets = rethink ? [] : ResetValues(s, today);
        if (!rethink && resets.Count == 0) return;
        if (await db.ChatMessages.AnyAsync(m => m.AspirationId == reading.AspirationId && (m.Card == "measure_reset" || m.Card == "measure_rethink")
                && m.CreatedAt > now.AddDays(-MeasureMath.ResponseCooldownDays), ct)) return;

        var brief = new JsonObject { ["say"] = rethink ? "rethink" : "lagging", ["name"] = next.MeasureName, ["unit"] = next.MeasureUnit, ["direction"] = trend.Direction };
        var text = await phrasing.PhraseAsync(CoachCallKind.MeasureNote, MeasurePrompt.System, brief, ct);
        var card = new JsonObject { ["aspirationId"] = reading.AspirationId, ["name"] = next.MeasureName, ["unit"] = next.MeasureUnit };
        if (!rethink)
            card["resets"] = new JsonArray(resets.Select(r => (JsonNode)new JsonObject
                { ["ref"] = r.Milestone.Ref, ["title"] = r.Milestone.Title, ["date"] = r.Milestone.Date.ToString("yyyy-MM-dd"), ["from"] = r.Milestone.MeasureHopedFor, ["to"] = r.To }).ToArray());
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(), UserId = reading.UserId, Role = "coach", Text = text, AspirationId = reading.AspirationId, CreatedAt = now,
            Card = rethink ? "measure_rethink" : "measure_reset", CardJson = card.ToJsonString(),
        });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Asks for a reading in chat; the user's reply is saved as one.</summary>
    public async Task AskAsync(Guid aspirationId, CancellationToken ct)
    {
        var s = await store.LoadAsync(aspirationId, ct);
        if (!s.Has || s.Next is not { } next) return;
        var now = clock.GetUtcNow().UtcDateTime;
        // One open ask at a time.
        var lastAsk = await db.ChatMessages.Where(m => m.AspirationId == aspirationId && m.Card == "measure_ask").OrderByDescending(m => m.Seq).FirstOrDefaultAsync(ct);
        if (lastAsk is not null && !await db.ChatMessages.AnyAsync(m => m.Seq > lastAsk.Seq && m.Role == "user", ct)) return;
        var text = await phrasing.PhraseAsync(CoachCallKind.MeasureNote, MeasurePrompt.System,
            new JsonObject { ["say"] = "ask", ["name"] = next.MeasureName, ["unit"] = next.MeasureUnit }, ct);
        db.ChatMessages.Add(new ChatMessage
        {
            Id = Guid.NewGuid(), UserId = next.UserId, Role = "coach", Text = text, AspirationId = aspirationId, CreatedAt = now, Card = "measure_ask",
            CardJson = new JsonObject { ["aspirationId"] = aspirationId, ["name"] = next.MeasureName, ["unit"] = next.MeasureUnit }.ToJsonString(),
        });
        await db.SaveChangesAsync(ct);
    }
}
