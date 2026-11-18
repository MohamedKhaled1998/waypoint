using System.Globalization;
using System.Text.Json.Nodes;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

/// <summary>
/// The Plan's hard rules, checked in server code whatever the model says. Returns problem codes only (never the
/// user's words), so they are safe to log and to hand back to the Coach.
/// </summary>
public static class PlanRules
{
    public static readonly string[] MilestoneKinds = ["effort", "ability"];
    public const string ThisWeek = "this_week";

    public static List<string> Validate(JsonNode? node, IReadOnlyList<Slot> slots, DateOnly start, DateOnly? settleFor)
    {
        var problems = new List<string>();
        if (node is not JsonObject plan) return ["plan_malformed"];
        if (string.IsNullOrWhiteSpace(HandOff.Str(plan, "rationale"))) problems.Add("rationale_missing");

        var refs = new HashSet<string>();
        if (plan["milestones"] is not JsonArray milestones || milestones.Count == 0) problems.Add("milestones_missing");
        else
            for (var i = 0; i < milestones.Count; i++)
            {
                if (milestones[i] is not JsonObject m) { problems.Add($"milestone_{i}_malformed"); continue; }
                var id = HandOff.Str(m, "id");
                if (string.IsNullOrWhiteSpace(id) || !refs.Add(id)) problems.Add($"milestone_{i}_id_invalid");
                if (string.IsNullOrWhiteSpace(HandOff.Str(m, "title"))) problems.Add($"milestone_{i}_title_missing");
                if (string.IsNullOrWhiteSpace(HandOff.Str(m, "reached_when"))) problems.Add($"milestone_{i}_reached_when_missing");
                if (!MilestoneKinds.Contains(HandOff.Str(m, "kind"))) problems.Add($"milestone_{i}_kind_invalid");
                if (ParseDate(HandOff.Str(m, "date")) is not { } date) problems.Add($"milestone_{i}_date_invalid");
                else if (settleFor is not null && date > settleFor) problems.Add($"milestone_{i}_past_settle_for_date");
            }

        // Actions find their Slot by label, so two Slots with one label would link to the wrong one.
        if (slots.GroupBy(s => s.Label).Any(g => g.Count() > 1)) problems.Add("slot_labels_ambiguous");
        var byLabel = slots.GroupBy(s => s.Label).ToDictionary(g => g.Key, g => g.First());

        if (plan["habits"] is not JsonArray habits) problems.Add("habits_missing");
        else
        {
            var titles = new HashSet<string>();
            for (var i = 0; i < habits.Count; i++)
            {
                if (habits[i] is not JsonObject h) { problems.Add($"habit_{i}_malformed"); continue; }
                if (string.IsNullOrWhiteSpace(HandOff.Str(h, "title"))) problems.Add($"habit_{i}_title_missing");
                if (HandOff.Num(h, "times_per_week") is not ({ } n and >= 1 and <= 7) || n != Math.Floor(n)) problems.Add($"habit_{i}_times_per_week_invalid");
                if (HandOff.Num(h, "minutes") is null or <= 0) problems.Add($"habit_{i}_minutes_invalid");
                if (h["slots"] is not JsonArray hs || hs.Count == 0 || hs.Any(s => s?.GetValue<string>() is not { } l || !byLabel.ContainsKey(l)))
                    problems.Add($"habit_{i}_slot_unknown");
                if (HandOff.Str(h, "title") is { } ht && !titles.Add(ht)) problems.Add($"habit_{i}_title_duplicate");
            }
        }

        if (plan["tasks"] is not JsonArray tasks) problems.Add("tasks_missing");
        else
        {
            var titles = new HashSet<string>();
            for (var i = 0; i < tasks.Count; i++)
            {
                if (tasks[i] is not JsonObject t) { problems.Add($"task_{i}_malformed"); continue; }
                if (string.IsNullOrWhiteSpace(HandOff.Str(t, "title"))) problems.Add($"task_{i}_title_missing");
                if (HandOff.Num(t, "minutes") is null or <= 0) problems.Add($"task_{i}_minutes_invalid");
                if (HandOff.Str(t, "milestone") is not { } mref || !refs.Contains(mref)) problems.Add($"task_{i}_milestone_unknown");
                var due = HandOff.Str(t, "due");
                if (due != ThisWeek && ParseDate(due) is null) problems.Add($"task_{i}_due_invalid");
                if (HandOff.Str(t, "title") is { } tt && !titles.Add(tt)) problems.Add($"task_{i}_title_duplicate");
            }
        }

        if (plan["first_two_weeks"] is not JsonArray actions) problems.Add("first_two_weeks_missing");
        else
        {
            var end = start.AddDays(13);
            var perSlotDay = new Dictionary<(DateOnly, string), double>();
            for (var i = 0; i < actions.Count; i++)
            {
                if (actions[i] is not JsonObject a) { problems.Add($"action_{i}_malformed"); continue; }
                if (string.IsNullOrWhiteSpace(HandOff.Str(a, "action"))) problems.Add($"action_{i}_title_missing");
                if (!new[] { "habit", "task" }.Contains(HandOff.Str(a, "kind"))) problems.Add($"action_{i}_kind_invalid");
                var minutes = HandOff.Num(a, "minutes");
                if (minutes is null or <= 0) problems.Add($"action_{i}_minutes_invalid");
                var date = ParseDate(HandOff.Str(a, "date"));
                if (date is null) problems.Add($"action_{i}_date_invalid");
                else if (date < start || date > end) problems.Add($"action_{i}_outside_two_weeks");
                var label = HandOff.Str(a, "slot");
                if (label is null || !byLabel.TryGetValue(label, out var slot)) { problems.Add($"action_{i}_unknown_slot"); continue; }
                if (date is null) continue;
                if (!slot.Days.Split(',').Contains(Day(date.Value))) problems.Add($"action_{i}_not_a_slot_day");
                perSlotDay[(date.Value, label)] = perSlotDay.GetValueOrDefault((date.Value, label)) + (minutes ?? 0);
            }
            foreach (var ((date, label), used) in perSlotDay)
                if (used > Room(byLabel[label]))
                    problems.Add($"slot_overfull_{date:yyyy-MM-dd}");
        }
        return problems;
    }

    public static double Room(Slot s) => (HandOff.Time(s.End)!.Value - HandOff.Time(s.Start)!.Value).TotalMinutes;

    public static string Day(DateOnly d) => HandOff.Days[((int)d.DayOfWeek + 6) % 7];

    public static DateOnly? ParseDate(string? s) =>
        DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
}

/// <summary>A draft the hard rules rejected twice; carries problem codes only.</summary>
public sealed class PlanProblemException(IReadOnlyList<string> problems) : Exception("plan_draft_invalid"), ISafeReason
{
    public string Reason { get; } = string.Join(",", problems);
}
