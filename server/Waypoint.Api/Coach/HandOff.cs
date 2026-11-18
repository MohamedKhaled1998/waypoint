using System.Globalization;
using System.Text.Json.Nodes;

namespace Waypoint.Api.Coach;

/// <summary>
/// Validates the Interview's hand-off. Returns problem codes only (never the user's words), so they are safe to log
/// and to hand back to the Coach when it is asked again.
/// </summary>
public static class HandOff
{
    public const int ShortNameMax = 16;
    public static readonly string[] Days = ["mon", "tue", "wed", "thu", "fri", "sat", "sun"];
    public static readonly string[] OnSkipValues = ["gentle_check", "quiet_replan", "ask_why"];
    public static readonly string[] ContactValues = ["Quiet", "Gentle", "Chatty"];

    public static List<string> Validate(JsonNode? node, bool needsUserBlock, DateOnly today, string? shortNameOverride = null)
    {
        var problems = new List<string>();
        if (node is not JsonObject root) return ["hand_off_missing"];

        if (root["aspiration"] is not JsonObject a) problems.Add("aspiration_missing");
        else
        {
            foreach (var f in new[] { "aspiration", "success_looks_like", "why_now", "starting_point", "picture" })
                if (string.IsNullOrWhiteSpace(Str(a, f))) problems.Add($"{f}_missing");

            var shortName = shortNameOverride ?? Str(a, "short_name");
            if (string.IsNullOrWhiteSpace(shortName)) problems.Add("short_name_missing");
            else if (shortName.Trim().Length > ShortNameMax) problems.Add("short_name_too_long");

            var share = Num(a, "share_hours_per_week");
            if (share is null or <= 0) problems.Add("share_hours_per_week_missing");

            var hoped = Date(a, "hoped_for_date", problems);
            if (hoped is not null && hoped <= today) problems.Add("hoped_for_date_not_in_future");
            var settleDate = Date(a, "settle_for_date", problems, optional: true);
            if (settleDate is not null && hoped is not null && settleDate < hoped) problems.Add("settle_for_date_before_hoped_for_date");
            if (settleDate is null && string.IsNullOrWhiteSpace(Str(a, "settle_for_picture")) && !problems.Contains("settle_for_date_invalid"))
                problems.Add("settle_for_missing");

            if (!OnSkipValues.Contains(Str(a, "on_skip"))) problems.Add("on_skip_invalid");
        }

        if (needsUserBlock)
        {
            if (root["user"] is not JsonObject u) problems.Add("user_missing");
            else
            {
                if (u["slots"] is not JsonArray slots || slots.Count == 0) problems.Add("slots_missing");
                else
                    for (var i = 0; i < slots.Count; i++)
                        if (!ValidSlot(slots[i] as JsonObject)) problems.Add($"slot_{i}_needs_day_and_time_window");
                if (Num(u, "time_budget_hours_per_week") is null or <= 0) problems.Add("time_budget_missing");
                if (!ContactValues.Contains(Str(u, "contact_frequency"))) problems.Add("contact_frequency_invalid");
            }
        }
        return problems;
    }

    public static bool ValidSlot(JsonObject? s)
    {
        if (s is null || string.IsNullOrWhiteSpace(Str(s, "label"))) return false;
        if (s["days"] is not JsonArray days || days.Count == 0) return false;
        if (days.Any(d => d is not JsonValue v || !v.TryGetValue<string>(out var day) || !Days.Contains(day))) return false;
        return Time(Str(s, "start")) is { } start && Time(Str(s, "end")) is { } end && start < end;
    }

    public static TimeOnly? Time(string? s) =>
        TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;

    public static string? Str(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    public static double? Num(JsonObject o, string key) =>
        o[key] is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;

    private static DateOnly? Date(JsonObject o, string key, List<string> problems, bool optional = false)
    {
        var s = Str(o, key);
        if (string.IsNullOrWhiteSpace(s))
        {
            if (!optional) problems.Add($"{key}_missing");
            return null;
        }
        if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)) return d;
        problems.Add($"{key}_invalid");
        return null;
    }
}
