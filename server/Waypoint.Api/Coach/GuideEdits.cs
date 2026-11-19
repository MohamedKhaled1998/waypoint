using System.Text;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// Holds back the part of a streamed reply that starts at a marker, so the user never sees it: text before the marker is
/// released as it arrives (minus a tail that might be the start of the marker), everything after it is kept as the payload.
/// </summary>
public sealed class MarkerFilter(string marker)
{
    private readonly StringBuilder held = new();
    public StringBuilder Visible { get; } = new();
    public StringBuilder Payload { get; } = new();
    public bool Marked { get; private set; }

    /// <summary>The text of this chunk that is safe to show.</summary>
    public string Feed(string chunk)
    {
        if (Marked) { Payload.Append(chunk); return ""; }
        held.Append(chunk);
        var text = held.ToString();
        var at = text.IndexOf(marker, StringComparison.Ordinal);
        if (at >= 0)
        {
            Marked = true;
            Payload.Append(text[(at + marker.Length)..]);
            held.Clear();
            return Show(text[..at]);
        }
        var keep = 0;
        for (var n = Math.Min(marker.Length - 1, text.Length); n > 0; n--)
            if (text.EndsWith(marker[..n], StringComparison.Ordinal)) { keep = n; break; }
        held.Clear().Append(text[^keep..]);
        return Show(text[..^keep]);
    }

    /// <summary>What is still held at the end of the reply (a false alarm for the marker).</summary>
    public string Flush()
    {
        var rest = held.ToString();
        held.Clear();
        return Show(rest);
    }

    private string Show(string s)
    {
        Visible.Append(s);
        return s;
    }
}

/// <summary>A chat request to change a Guide: validated like any Guide and applied at once, with the change kept with the Plan version.</summary>
public static class GuideEdits
{
    public const string Marker = "<<GUIDE>>";

    /// <summary>The Guides in force, for the Coach's context in chat. Null when there are none.</summary>
    public static async Task<string?> ContextAsync(WaypointDb db, CancellationToken ct)
    {
        var versions = await db.PlanVersions.Where(p => p.Status == PlanVersionStatus.Approved).Select(p => p.Id).ToListAsync(ct);
        var habits = await db.Habits.Where(h => versions.Contains(h.PlanVersionId) && h.GuideJson != null).Select(h => new { h.Title, h.GuideJson }).ToListAsync(ct);
        var tasks = await db.PlanTasks.Where(t => versions.Contains(t.PlanVersionId) && t.GuideJson != null).Select(t => new { t.Title, t.GuideJson }).ToListAsync(ct);
        var all = habits.Concat(tasks).ToList();
        if (all.Count == 0) return null;
        return new JsonArray(all.Select(g => (JsonNode)new JsonObject { ["action"] = g.Title, ["guide"] = JsonNode.Parse(g.GuideJson!) }).ToArray()).ToJsonString();
    }

    /// <summary>
    /// Applies the edit in <paramref name="payload"/> and returns the card to show. Throws <see cref="CoachUnavailableException"/> when
    /// the edit is unusable or breaks a rule, so nothing is changed and the Coach is late rather than wrong.
    /// </summary>
    public static async Task<(Guid AspirationId, JsonObject Card)> ApplyAsync(WaypointDb db, TimeProvider clock, string payload, Guid? aspirationId, CancellationToken ct)
    {
        var edit = GuideRules.ParseObject(payload) ?? throw new CoachUnavailableException("guide_edit_invalid");
        var title = HandOff.Str(edit, "action");
        var reason = HandOff.Str(edit, "reason");
        if (string.IsNullOrWhiteSpace(title) || edit["guide"] is not JsonObject guide) throw new CoachUnavailableException("guide_edit_invalid");

        var current = await db.PlanVersions.Where(p => p.Status == PlanVersionStatus.Approved).ToListAsync(ct);
        var habits = await db.Habits.Where(h => h.Title == title && current.Select(v => v.Id).Contains(h.PlanVersionId) && h.GuideJson != null).ToListAsync(ct);
        var tasks = await db.PlanTasks.Where(t => t.Title == title && current.Select(v => v.Id).Contains(t.PlanVersionId) && t.GuideJson != null).ToListAsync(ct);
        var owners = habits.Select(h => (h.PlanVersionId, h.GuideJson!)).Concat(tasks.Select(t => (t.PlanVersionId, t.GuideJson!))).ToList();
        if (aspirationId is not null) owners = owners.Where(o => current.Any(v => v.Id == o.PlanVersionId && v.AspirationId == aspirationId)).ToList();
        if (owners.Count != 1) throw new CoachUnavailableException("guide_edit_unknown_action");
        var version = current.First(v => v.Id == owners[0].PlanVersionId);

        var interview = await db.Interviews.Where(i => i.AspirationId == version.AspirationId && i.HandOffJson != null).OrderByDescending(i => i.CreatedAt).FirstOrDefaultAsync(ct);
        var previous = JsonNode.Parse(owners[0].Item2)!.AsObject();
        var problems = GuideRules.Validate(guide, GuideRules.About(interview?.HandOffJson), previous);
        if (problems.Count > 0) throw new CoachUnavailableException("guide_edit_rejected_" + string.Join("_", problems));
        var next = GuideRules.Normalise(guide);
        var json = next.ToJsonString();

        // The approved version, a proposal waiting beside it, and the Actions still ahead all carry the new Guide.
        var versionIds = await db.PlanVersions.Where(p => p.AspirationId == version.AspirationId && (p.Status == PlanVersionStatus.Approved || p.Status == PlanVersionStatus.Proposed))
            .Select(p => p.Id).ToListAsync(ct);
        foreach (var h in await db.Habits.Where(h => h.Title == title && versionIds.Contains(h.PlanVersionId)).ToListAsync(ct)) h.GuideJson = json;
        foreach (var t in await db.PlanTasks.Where(t => t.Title == title && versionIds.Contains(t.PlanVersionId)).ToListAsync(ct)) t.GuideJson = json;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        foreach (var a in await db.ScheduledActions.Where(a => a.AspirationId == version.AspirationId && a.Title == title && a.Date >= today).ToListAsync(ct)) a.GuideJson = json;

        // A chat edit is kept with the Plan version it changed (its Guide log) and does not create a new version: the spec's
        // list of version-creating changes (first Plan, weekly refresh, Re-plan, Catch-up, Time budget cut) leaves Guide
        // edits out, and says a Guide changes "right away" on a chat request, "kept with its Plan version".
        var log = version.GuideLogJson is null ? [] : JsonNode.Parse(version.GuideLogJson)!.AsArray();
        log.Add(new JsonObject
        {
            ["action"] = title, ["source"] = "chat", ["reason"] = reason?.Trim() ?? "", ["at"] = clock.GetUtcNow().UtcDateTime, ["guide"] = next.DeepClone(),
        });
        version.GuideLogJson = log.ToJsonString();
        await GuideReferrals.PostIfNewAsync(db, clock, version.AspirationId, [new(title, next)], ct);

        var aspiration = await db.Aspirations.FirstAsync(a => a.Id == version.AspirationId, ct);
        return (aspiration.Id, new JsonObject
        {
            ["aspirationId"] = aspiration.Id, ["shortName"] = aspiration.ShortName, ["hue"] = aspiration.Hue, ["action"] = title, ["guide"] = next,
        });
    }
}
