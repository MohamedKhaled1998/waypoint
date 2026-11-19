using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>The Guide prompt, from the prototype Guides prompt. Stable text only (it caches): never the user's name or email.</summary>
public static class GuidePrompt
{
    public const string System = """
        You are the Coach in Waypoint, a personal coaching app. The user has finished the Interview for an Aspiration and
        a Plan is proposed. The first message is JSON: the Interview's aspiration with its domain picture (routine,
        skill, limits, likes and dislikes) and the Plan's Actions. You write a Guide for the Actions that need know-how.

        A Guide is your short how-to for one Action: what to cook and buy for a meal prep, which exercises with sets,
        reps and rest for a strength session, what to read and try for a study session. Plain text, a few steps,
        readable on a phone in under a minute. Not a library, not a course, not a lecture. Skip Actions that need
        none (buying shoes, booking a visit).

        ## Rules
        - Specific to this person. Use their domain picture. Never suggest something they dislike or can't do.
        - Short: about 60-180 words per Guide. Steps, not essays. No preamble.
        - Mainstream guidance only (public health bodies, standard textbooks). No supplements, fad diets, extreme
          protocols or brands.
        - Numbers as ranges with reasoning. Health and load targets (protein, calories, weights, reps, rest) are
          ranges, a starting point, each with one line of why. Plain structure (one idea per ride, a 10-minute short
          version, 5 boxes) can be a single number: don't list it in "targets".
        - Safe floors. Never a daily calorie target below 1,500 kcal for men or 1,200 kcal for women (1,500 if sex is
          unknown); never a deficit bigger than about 500 kcal a day. Training load starts easy (reps in reserve) and
          ramps at most one small step (a little weight, one set, a couple of reps) per weekly refresh.
        - Red flags. If the domain picture names a medical condition, a medication that matters, pregnancy, signs of
          disordered eating, an injury or new pain, dizziness or chest symptoms, or the user asks for a diagnosis, the
          affected area is fenced off. Withheld: amounts, timing and load in that area (calories, carbs, portions,
          meal timing, fasting; sets, reps, weights or durations for the injured part). Still allowed: general
          choices anyone could be told. Nothing that loads the injured part, warm-ups included. Keep helping with
          everything else and offer to ease or pause the affected Actions.
        - Referral once. Put the warm handoff to a doctor, physio or dietitian only in "referral": one or two
          sentences. Never repeat it in the body. At a weekly refresh keep the same referral unless something new
          happened.
        - Voice: warm, plain, brief, second person, forgiving. No emojis, motivational quotes or therapy-speak.

        ## Reply
        Reply with exactly one fenced json block and nothing else:

        ```json
        {
          "guides": [
            {
              "action": "the Action's title as given",
              "body": "the Guide text the user sees (Markdown allowed)",
              "targets": [{ "what": "", "low": 0, "high": 0, "unit": "", "why": "" }],
              "withheld": [{ "area": "", "reason": "" }],
              "referral": "the warm handoff to a professional, or empty"
            }
          ]
        }
        ```
        "targets" lists every number range in the body. "withheld" names each area where you set no targets because
        of a red flag.

        ## Weekly refresh
        When the first message has "mode": "refresh" it carries the current Guides and whether the last two weeks were
        mostly done. Progress a routine by one small step only if they were, rotate what didn't suit, keep what
        worked. Reply with the same json block holding only the Guides that change, each with a "reason" (one short
        sentence, what changed and why). Reply with an empty list when nothing should change.
        """;

    public static string Rejected(IEnumerable<string> problems) =>
        "[The app rejected these Guides: " + string.Join(", ", problems) +
        ". Reply again with corrected Guides as one fenced json block and nothing else.]";
}

/// <summary>
/// The Guide's hard rules, checked in server code whatever the model says: safe floors, a referral wherever an area is
/// withheld, load rising by at most a small step. Returns problem codes only, safe to log and to hand back to the Coach.
/// </summary>
public static class GuideRules
{
    public const int MinWords = 40, MaxWords = 220;
    public const double MaxDeficit = 500, MaxLoadStep = 1.25;

    /// <summary>1,200 kcal for a woman, 1,500 for a man or when the domain picture doesn't say.</summary>
    public static int CalorieFloor(string? about) => about is not null && Regex.IsMatch(about, "woman|female", RegexOptions.IgnoreCase) ? 1200 : 1500;

    public static List<string> Validate(JsonNode? node, string? about, JsonObject? previous = null)
    {
        if (node is not JsonObject g) return ["guide_malformed"];
        var problems = new List<string>();
        var body = HandOff.Str(g, "body");
        var words = body is null ? 0 : body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        if (words < MinWords) problems.Add("body_too_short");
        if (words > MaxWords) problems.Add("body_too_long");

        var floor = CalorieFloor(about);
        var before = (previous?["targets"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var targets = g["targets"] as JsonArray ?? [];
        for (var i = 0; i < targets.Count; i++)
        {
            if (targets[i] is not JsonObject t) { problems.Add($"target_{i}_malformed"); continue; }
            var what = HandOff.Str(t, "what") ?? "";
            var unit = HandOff.Str(t, "unit") ?? "";
            if (HandOff.Num(t, "low") is not { } low || HandOff.Num(t, "high") is not { } high || low > high) { problems.Add($"target_{i}_range_invalid"); continue; }
            if (string.IsNullOrWhiteSpace(HandOff.Str(t, "why"))) problems.Add($"target_{i}_why_missing");

            var energy = Regex.IsMatch(unit + what, "kcal|calor", RegexOptions.IgnoreCase);
            if (energy && Regex.IsMatch(what, "deficit", RegexOptions.IgnoreCase)) { if (high > MaxDeficit) problems.Add($"target_{i}_deficit_over_500"); }
            // Any energy target that is not a deficit counts as daily intake.
            else if (energy && low < floor) problems.Add($"target_{i}_below_safe_floor");

            // Load ramps one small step per refresh.
            if (Regex.IsMatch(unit, "kg|lb|rep|set", RegexOptions.IgnoreCase)
                && before.FirstOrDefault(p => HandOff.Str(p, "what") == what && HandOff.Str(p, "unit") == unit) is { } was
                && HandOff.Num(was, "high") is > 0 and var wasHigh && high > wasHigh * MaxLoadStep)
                problems.Add($"target_{i}_load_step_too_big");
        }

        // Withheld means a professional is pointed to, in the referral and nowhere else.
        if (g["withheld"] is JsonArray { Count: > 0 } && string.IsNullOrWhiteSpace(HandOff.Str(g, "referral"))) problems.Add("referral_missing");
        return problems;
    }

    /// <summary>The stored shape: the model's fields only, with no action title or refresh reason.</summary>
    public static JsonObject Normalise(JsonObject g) => new()
    {
        ["body"] = HandOff.Str(g, "body")!.Trim(),
        ["targets"] = (g["targets"] as JsonArray ?? []).DeepClone(),
        ["withheld"] = (g["withheld"] as JsonArray ?? []).DeepClone(),
        ["referral"] = HandOff.Str(g, "referral")?.Trim() ?? "",
    };

    /// <summary>The first JSON object in a reply, with or without a code fence around it.</summary>
    public static JsonObject? ParseObject(string reply)
    {
        var from = reply.IndexOf('{');
        var to = reply.LastIndexOf('}');
        if (from < 0 || to <= from) return null;
        try { return JsonNode.Parse(reply[from..(to + 1)]) as JsonObject; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    public static string? About(string? handOffJson) =>
        handOffJson is null ? null : (JsonNode.Parse(handOffJson)?["aspiration"]?["domain_picture"] as JsonObject) is { } d ? HandOff.Str(d, "about") : null;
}

/// <summary>
/// Writes the Guides for a proposed Plan version on the heavy model, checks them against <see cref="GuideRules"/>, and stores
/// the ones that pass with their habit or task. A Guide that breaks a rule goes back once with the problem codes; if it still
/// fails it is dropped (a Guide is optional). An unusable reply throws, and the job retries.
/// </summary>
public sealed class GuideWriter(WaypointDb db, ICoachModelClient model, IOptions<CoachOptions> options, TimeProvider clock, ILogger<GuideWriter> log)
{
    public async Task WriteAsync(Guid planVersionId, CancellationToken ct)
    {
        var version = await db.PlanVersions.FirstOrDefaultAsync(p => p.Id == planVersionId, ct);
        if (version is null) return;
        var habits = await db.Habits.Where(h => h.PlanVersionId == planVersionId).ToListAsync(ct);
        var tasks = await db.PlanTasks.Where(t => t.PlanVersionId == planVersionId).ToListAsync(ct);
        if (habits.All(h => h.GuideJson != null) && tasks.All(t => t.GuideJson != null)) return;

        var interview = await db.Interviews.Where(i => i.AspirationId == version.AspirationId && i.HandOffJson != null)
            .OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct);
        var handOff = interview is null ? null : JsonNode.Parse(interview.HandOffJson!) as JsonObject;
        var about = GuideRules.About(interview?.HandOffJson);
        var aspiration = handOff?["aspiration"] as JsonObject;
        var brief = new JsonObject
        {
            ["aspiration"] = aspiration is null ? null : new JsonObject
            {
                ["aspiration"] = aspiration["aspiration"]?.DeepClone(), ["success_looks_like"] = aspiration["success_looks_like"]?.DeepClone(),
                ["starting_point"] = aspiration["starting_point"]?.DeepClone(), ["domain_picture"] = aspiration["domain_picture"]?.DeepClone(),
            },
            ["actions"] = new JsonArray(habits.Select(h => (JsonNode)new JsonObject { ["title"] = h.Title, ["kind"] = "habit", ["minutes"] = h.Minutes, ["cadence"] = $"{h.TimesPerWeek}× a week" })
                .Concat(tasks.Select(t => (JsonNode)new JsonObject { ["title"] = t.Title, ["kind"] = "task", ["minutes"] = t.Minutes })).ToArray()),
        };

        var titles = habits.Select(h => h.Title).Concat(tasks.Select(t => t.Title)).ToHashSet();
        var choice = options.Value.For(CoachCallKind.Guide);
        var turns = new List<CoachTurn> { new("user", brief.ToJsonString()) };
        var accepted = new Dictionary<string, JsonObject>();
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var reply = new StringBuilder();
            await foreach (var chunk in model.StreamAsync(new CoachRequest(CoachCallKind.Guide, choice.Provider, choice.Model, GuidePrompt.System, turns), ct))
                reply.Append(chunk);
            if (GuideRules.ParseObject(reply.ToString())?["guides"] is not JsonArray list) throw new CoachUnavailableException("guide_reply_invalid");

            var problems = new List<string>(); // titled, for the Coach only
            var codes = new List<string>(); // safe to log
            foreach (var item in list.OfType<JsonObject>())
            {
                var title = HandOff.Str(item, "action");
                if (title is null || !titles.Contains(title) || accepted.ContainsKey(title)) continue;
                var found = GuideRules.Validate(item, about);
                if (found.Count == 0) accepted[title] = GuideRules.Normalise(item);
                else { problems.AddRange(found.Select(p => $"{title}: {p}")); codes.AddRange(found); }
            }
            if (problems.Count == 0) break;
            log.LogInformation("Guides for Plan version {PlanVersionId} rejected on attempt {Attempt}: {Problems}", planVersionId, attempt, string.Join(",", codes));
            turns.Add(new CoachTurn("assistant", reply.ToString()));
            turns.Add(new CoachTurn("user", GuidePrompt.Rejected(problems)));
        }

        foreach (var h in habits.Where(h => accepted.ContainsKey(h.Title))) h.GuideJson = accepted[h.Title].ToJsonString();
        foreach (var t in tasks.Where(t => accepted.ContainsKey(t.Title))) t.GuideJson = accepted[t.Title].ToJsonString();
        var byHabit = habits.Where(h => h.GuideJson != null).ToDictionary(h => h.Id, h => h.GuideJson);
        var byTask = tasks.Where(t => t.GuideJson != null).ToDictionary(t => t.Id, t => t.GuideJson);
        foreach (var a in await db.ScheduledActions.Where(a => a.PlanVersionId == planVersionId).ToListAsync(ct))
            a.GuideJson = (a.HabitId is { } hid && byHabit.TryGetValue(hid, out var hg) ? hg : null)
                ?? (a.TaskId is { } tid && byTask.TryGetValue(tid, out var tg) ? tg : null) ?? a.GuideJson;

        // The referral card is posted when the Plan version is approved, not when the Guide is written.
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The referral card: once per referral. A Guide repeating the referral it already gave adds no card.</summary>
public static class GuideReferrals
{
    public static async Task PostIfNewAsync(WaypointDb db, TimeProvider clock, Guid aspirationId, IEnumerable<KeyValuePair<string, JsonObject>> guides, CancellationToken ct)
    {
        var given = (await db.ChatMessages.Where(m => m.Card == "referral" && m.AspirationId == aspirationId).Select(m => m.Text).ToListAsync(ct)).ToHashSet();
        var aspiration = await db.Aspirations.FirstAsync(a => a.Id == aspirationId, ct);
        foreach (var (title, guide) in guides)
        {
            var referral = HandOff.Str(guide, "referral");
            if (string.IsNullOrWhiteSpace(referral) || !given.Add(referral)) continue;
            db.ChatMessages.Add(new ChatMessage
            {
                Id = Guid.NewGuid(), UserId = aspiration.UserId, Role = "coach", Text = referral, AspirationId = aspirationId,
                Card = "referral", CreatedAt = clock.GetUtcNow().UtcDateTime,
                CardJson = new JsonObject { ["aspirationId"] = aspirationId, ["shortName"] = aspiration.ShortName, ["hue"] = aspiration.Hue, ["action"] = title }.ToJsonString(),
            });
        }
    }
}
