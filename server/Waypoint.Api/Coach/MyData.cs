using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// My data: the user takes it with them (a zip of complete JSON and readable Markdown) or removes it (the account and every row at once).
/// Both read through the user's global query filter, so nothing of anyone else's can be in the zip or go in the delete.
/// </summary>
public static class MyData
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static void MapMyData(this WebApplication app)
    {
        var g = app.MapGroup("/me").RequireAuthorization();
        g.MapGet("/export", Export);
        g.MapDelete("", DeleteAccount);
    }

    private static async Task<IResult> Export(WaypointDb db, TimeProvider clock, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(ct);
        var aspirations = await db.Aspirations.OrderBy(a => a.CreatedAt).ToListAsync(ct);
        var interviews = await db.Interviews.OrderBy(i => i.CreatedAt).ToListAsync(ct);
        var interviewMessages = await db.InterviewMessages.OrderBy(m => m.Seq).ToListAsync(ct);
        var versions = await db.PlanVersions.OrderBy(p => p.CreatedAt).ToListAsync(ct);
        var milestones = await db.Milestones.OrderBy(m => m.Date).ToListAsync(ct);
        var habits = await db.Habits.ToListAsync(ct);
        var tasks = await db.PlanTasks.ToListAsync(ct);
        var anchors = await db.Anchors.ToListAsync(ct);
        var slots = await db.Slots.ToListAsync(ct);
        var actions = await db.ScheduledActions.OrderBy(a => a.Date).ToListAsync(ct);
        var checkIns = await db.CheckIns.OrderBy(c => c.TappedAt).ToListAsync(ct);
        var measureReadings = await db.MeasureReadings.OrderBy(r => r.Date).ToListAsync(ct);
        var progressReadings = await db.ProgressReadings.OrderBy(r => r.At).ToListAsync(ct);
        var pauses = await db.Pauses.OrderBy(p => p.StartsAt).ToListAsync(ct);
        var chat = await db.ChatMessages.OrderBy(m => m.Seq).ToListAsync(ct);

        var everything = new
        {
            exportedAt = clock.GetUtcNow().UtcDateTime,
            user = new { user.Id, user.CreatedAt, user.TimeBudgetHours, user.ContactFrequency, user.RefreshDay, user.RefreshTime, user.QuietStart, user.QuietEnd },
            slots, anchors, aspirations, interviews, interviewMessages, planVersions = versions, milestones, habits, tasks, actions, checkIns,
            measureReadings, progressReadings, pauses, chat,
        };

        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "waypoint.json", JsonSerializer.Serialize(everything, Json));
            var used = new HashSet<string>();
            foreach (var a in aspirations)
            {
                var name = UniqueName(a.ShortName, used);
                Add(zip, $"aspirations/{name}.md", AspirationMarkdown(a, interviews, interviewMessages, versions, milestones, habits, tasks, actions, checkIns, measureReadings, pauses, chat));
            }
            Add(zip, "coach-chat.md", ChatMarkdown(chat.Where(m => m.AspirationId is null).ToList()));
        }
        return Results.File(stream.ToArray(), "application/zip", "waypoint-export.zip");
    }

    private static void Add(ZipArchive zip, string path, string text)
    {
        using var w = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false));
        w.Write(text);
    }

    private static string UniqueName(string shortName, HashSet<string> used)
    {
        var clean = new string(shortName.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-').ToArray()).Trim('-');
        if (clean.Length == 0) clean = "aspiration";
        var name = clean;
        for (var n = 2; !used.Add(name); n++) name = $"{clean}-{n}";
        return name;
    }

    private static string AspirationMarkdown(Aspiration a, List<Interview> interviews, List<InterviewMessage> interviewMessages, List<PlanVersion> versions,
        List<Milestone> milestones, List<Habit> habits, List<PlanTask> tasks, List<ScheduledAction> actions, List<CheckIn> checkIns,
        List<MeasureReading> readings, List<Pause> pauses, List<ChatMessage> chat)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {a.ShortName}").AppendLine();
        sb.AppendLine($"Aspiration: {a.OwnWords}").AppendLine();
        sb.AppendLine($"- Status: {a.Status}");
        sb.AppendLine($"- Hoped-for date: {a.HopedForDate:yyyy-MM-dd}");
        if (a.SettleForDate is { } s) sb.AppendLine($"- Settle-for date: {s:yyyy-MM-dd}");
        if (!string.IsNullOrWhiteSpace(a.SettleForPicture)) sb.AppendLine($"- Settle-for picture: {a.SettleForPicture}");
        sb.AppendLine($"- Time budget share: {a.ShareHoursPerWeek} hours a week");

        foreach (var i in interviews.Where(i => i.AspirationId == a.Id))
        {
            sb.AppendLine().AppendLine("## Interview").AppendLine();
            foreach (var m in interviewMessages.Where(m => m.InterviewId == i.Id))
                sb.AppendLine($"**{(m.Role == "user" ? "You" : "Coach")}:** {m.Text}").AppendLine();
        }

        foreach (var v in versions.Where(v => v.AspirationId == a.Id))
        {
            sb.AppendLine().AppendLine($"## Plan version {v.Number?.ToString() ?? "(not approved)"} ({v.Status}, {v.CreatedAt:yyyy-MM-dd})").AppendLine();
            if (!string.IsNullOrWhiteSpace(v.Rationale)) sb.AppendLine(v.Rationale).AppendLine();
            var ms = milestones.Where(m => m.PlanVersionId == v.Id).ToList();
            if (ms.Count > 0)
            {
                sb.AppendLine("### Milestones").AppendLine();
                foreach (var m in ms)
                {
                    sb.AppendLine($"- {m.Date:yyyy-MM-dd} **{m.Title}**: reached when {m.ReachedWhen} ({m.State})");
                    if (m.MeasureName is not null) sb.AppendLine($"  - Measure: {m.MeasureName} ({m.MeasureUnit}), starting at {m.MeasureStart}, hoping for about {m.MeasureHopedFor}");
                }
                sb.AppendLine();
            }
            var hs = habits.Where(h => h.PlanVersionId == v.Id).ToList();
            if (hs.Count > 0)
            {
                sb.AppendLine("### Actions that repeat").AppendLine();
                foreach (var h in hs)
                {
                    sb.AppendLine($"- {h.Title}: {h.TimesPerWeek} times a week, {h.Minutes} minutes");
                    if (!string.IsNullOrWhiteSpace(h.GuideJson)) sb.AppendLine($"  - Guide: {GuideText(h.GuideJson)}");
                }
                sb.AppendLine();
            }
            var ts = tasks.Where(t => t.PlanVersionId == v.Id).ToList();
            if (ts.Count > 0)
            {
                sb.AppendLine("### One-off Actions").AppendLine();
                foreach (var t in ts)
                {
                    sb.AppendLine($"- {t.Title}: {t.Minutes} minutes{(t.DueDate is { } d ? $", due {d:yyyy-MM-dd}" : "")}");
                    if (!string.IsNullOrWhiteSpace(t.GuideJson)) sb.AppendLine($"  - Guide: {GuideText(t.GuideJson)}");
                }
                sb.AppendLine();
            }
        }

        var rs = readings.Where(r => r.AspirationId == a.Id).ToList();
        if (rs.Count > 0)
        {
            sb.AppendLine().AppendLine("## Measure readings").AppendLine();
            foreach (var r in rs) sb.AppendLine($"- {r.Date:yyyy-MM-dd}: {r.Value}");
        }

        var mine = actions.Where(x => x.AspirationId == a.Id).ToList();
        if (mine.Count > 0)
        {
            sb.AppendLine().AppendLine("## Actions and Check-ins").AppendLine();
            foreach (var x in mine)
            {
                var c = checkIns.Where(c => c.ScheduledActionId == x.Id).OrderByDescending(c => c.TappedAt).FirstOrDefault();
                var said = c is null ? "no Check-in" : $"{c.State}{(c.MinutesSpent is { } m ? $", {m} minutes" : "")}{(string.IsNullOrWhiteSpace(c.Note) ? "" : $", \"{c.Note}\"")}";
                sb.AppendLine($"- {x.Date:yyyy-MM-dd} {x.Title} ({x.Minutes} minutes): {said}");
            }
        }

        var ps = pauses.Where(p => p.AspirationId == a.Id).ToList();
        if (ps.Count > 0)
        {
            sb.AppendLine().AppendLine("## Pauses").AppendLine();
            foreach (var p in ps) sb.AppendLine($"- {p.StartsAt:yyyy-MM-dd} to {(p.EndsAt is { } e ? e.ToString("yyyy-MM-dd") : "open")}{(p.ResumedAt is { } r ? $", resumed {r:yyyy-MM-dd}" : "")}");
        }

        var thread = chat.Where(m => m.AspirationId == a.Id).ToList();
        if (thread.Count > 0) sb.AppendLine().AppendLine("## Chat").AppendLine().Append(ChatMarkdown(thread, heading: false));
        return sb.ToString();
    }

    private static string GuideText(string guideJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(guideJson);
            return doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString()! : guideJson;
        }
        catch (JsonException) { return guideJson; }
    }

    private static string ChatMarkdown(List<ChatMessage> messages, bool heading = true)
    {
        var sb = new StringBuilder();
        if (heading) sb.AppendLine("# Coach chat").AppendLine();
        foreach (var m in messages)
            sb.AppendLine($"**{(m.Role == "user" ? "You" : "Coach")}** ({m.CreatedAt:yyyy-MM-dd HH:mm}): {m.Text}").AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// Delete everything: every row of the user's, hard-deleted at once, refresh tokens included, so no session can be renewed.
    /// Children go before parents; the whole thing is one transaction.
    /// </summary>
    private static async Task<IResult> DeleteAccount(WaypointDb db, CancellationToken ct)
    {
        var user = await db.Users.SingleAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.CheckIns.ExecuteDeleteAsync(ct);
        await db.Pauses.ExecuteDeleteAsync(ct);
        await db.ScheduledActions.ExecuteDeleteAsync(ct);
        await db.PlanTasks.ExecuteDeleteAsync(ct);
        await db.Habits.ExecuteDeleteAsync(ct);
        await db.Milestones.ExecuteDeleteAsync(ct);
        await db.ProgressReadings.ExecuteDeleteAsync(ct);
        await db.MeasureReadings.ExecuteDeleteAsync(ct);
        await db.PlanVersions.ExecuteDeleteAsync(ct);
        await db.WeeklyRefreshes.ExecuteDeleteAsync(ct);
        await db.Anchors.ExecuteDeleteAsync(ct);
        await db.InterviewMessages.ExecuteDeleteAsync(ct);
        await db.Interviews.ExecuteDeleteAsync(ct);
        await db.ChatMessages.ExecuteDeleteAsync(ct);
        await db.Jobs.ExecuteDeleteAsync(ct);
        await db.DeviceTokens.ExecuteDeleteAsync(ct);
        await db.Aspirations.ExecuteDeleteAsync(ct);
        await db.SlotEdits.ExecuteDeleteAsync(ct);
        await db.Slots.ExecuteDeleteAsync(ct);
        await db.Changes.ExecuteDeleteAsync(ct);
        await db.RefreshTokens.ExecuteDeleteAsync(ct);
        await db.Users.Where(u => u.Id == user.Id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);
        return Results.NoContent();
    }
}
