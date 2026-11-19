using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;

namespace Waypoint.Api.Tests;

public sealed partial class MilestoneTests
{
    private static async Task<HttpResponseMessage> ReadAsync(Seeded s, double value, string date, string? clientId = null) =>
        await s.Client.PostAsJsonAsync($"/aspirations/{s.AspirationId}/measure/readings", new { value, date, clientId });

    private async Task<List<JsonElement>> CardsAsync(Seeded s, string card) =>
        (await ThreadAsync(s.Client)).Where(m => m.GetProperty("card").ValueKind == JsonValueKind.String && m.GetProperty("card").GetString() == card).ToList();

    private async Task<DateOnly?> SettleForAsync(Seeded s)
    {
        using var scope = factory.Services.CreateScope();
        return (await scope.ServiceProvider.GetRequiredService<WaypointDb>().Aspirations.IgnoreQueryFilters().FirstAsync(a => a.Id == s.AspirationId)).SettleForDate;
    }

    [Fact]
    public async Task The_Measure_shows_readings_and_the_trend_and_a_resend_adds_nothing()
    {
        var s = await SeedAsync(measure: "hr", date: new DateOnly(2026, 3, 2));
        (await ReadAsync(s, 70, "2025-12-08", "r1")).EnsureSuccessStatusCode();
        (await ReadAsync(s, 65, "2026-01-05", "r2")).EnsureSuccessStatusCode();
        (await ReadAsync(s, 65, "2026-01-05", "r2")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.BadRequest, (await ReadAsync(s, 64, "2030-01-01")).StatusCode);

        var m = await s.Client.GetFromJsonAsync<JsonElement>($"/aspirations/{s.AspirationId}/measure/");
        Assert.True(m.GetProperty("hasMeasure").GetBoolean());
        Assert.Equal("Resting heart rate", m.GetProperty("name").GetString());
        Assert.Equal(2, m.GetProperty("readings").GetArrayLength());
        var trend = m.GetProperty("trend");
        Assert.Equal("down", trend.GetProperty("direction").GetString());
        Assert.Equal(-5, trend.GetProperty("change").GetDouble());
        Assert.Equal("2025-12-08", trend.GetProperty("since").GetString());
    }

    [Fact]
    public async Task A_lagging_Measure_proposes_resetting_later_hoped_for_values_and_never_the_Settle_for_choice()
    {
        var s = await SeedAsync(measure: "hr", date: new DateOnly(2026, 3, 2));
        var settle = await SettleForAsync(s);
        var actionsBefore = (await s.Client.GetFromJsonAsync<JsonElement>("/scheduled-actions")).GetArrayLength();
        (await ReadAsync(s, 70, "2025-12-08")).EnsureSuccessStatusCode();
        (await ReadAsync(s, 68, "2026-01-05")).EnsureSuccessStatusCode(); // right way, but slower than hoped
        await SettleAsync();

        var cards = await CardsAsync(s, "measure_reset");
        var card = Assert.Single(cards);
        Assert.Empty(await CardsAsync(s, "measure_rethink"));
        var json = card.GetProperty("cardData").ToString();
        Assert.Contains("\"resets\"", json);
        Assert.DoesNotContain("settle", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("settle", card.GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase);

        // Nothing moves until the user says yes; then only hoped-for values do, and no work is added.
        var before = await s.Client.GetFromJsonAsync<JsonElement>($"/aspirations/{s.AspirationId}/measure/");
        Assert.All(before.GetProperty("milestones").EnumerateArray(), x => Assert.True(x.GetProperty("hopedFor").GetDouble() >= 55));
        (await s.Client.PostAsync($"/aspirations/{s.AspirationId}/measure/accept-reset", null)).EnsureSuccessStatusCode();
        var after = await s.Client.GetFromJsonAsync<JsonElement>($"/aspirations/{s.AspirationId}/measure/");
        var later = after.GetProperty("milestones").EnumerateArray().Single(x => x.GetProperty("ref").GetString() == "m2");
        Assert.True(later.GetProperty("hopedFor").GetDouble() > 55); // nearer the trend than 55
        Assert.Equal(settle, await SettleForAsync(s));
        Assert.Equal(actionsBefore, (await s.Client.GetFromJsonAsync<JsonElement>("/scheduled-actions")).GetArrayLength());
    }

    [Fact]
    public async Task A_Measure_flat_for_six_weeks_asks_for_a_re_think_and_never_the_Settle_for_choice()
    {
        var s = await SeedAsync(measure: "hr", date: new DateOnly(2026, 3, 2));
        var settle = await SettleForAsync(s);
        (await ReadAsync(s, 70, "2025-11-24")).EnsureSuccessStatusCode();
        (await ReadAsync(s, 71, "2026-01-05")).EnsureSuccessStatusCode(); // the wrong way for six weeks
        await SettleAsync();

        var card = Assert.Single(await CardsAsync(s, "measure_rethink"));
        Assert.Empty(await CardsAsync(s, "measure_reset"));
        Assert.DoesNotContain("resets", card.GetProperty("cardData").ToString());
        Assert.DoesNotContain("settle", card.GetProperty("text").GetString()!, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(settle, await SettleForAsync(s));
    }

    [Fact]
    public async Task A_Measure_on_track_gets_no_response()
    {
        var s = await SeedAsync(measure: "hr", date: new DateOnly(2026, 3, 2));
        (await ReadAsync(s, 70, "2025-12-08")).EnsureSuccessStatusCode();
        (await ReadAsync(s, 66, "2026-01-05")).EnsureSuccessStatusCode();
        await SettleAsync();
        Assert.Empty(await CardsAsync(s, "measure_reset"));
        Assert.Empty(await CardsAsync(s, "measure_rethink"));
    }

    [Fact]
    public async Task Readings_stay_out_of_Pace_and_the_Projection()
    {
        var s = await SeedAsync(measure: "hr", date: new DateOnly(2026, 3, 2));
        var without = (await s.Client.GetStringAsync($"/progress/{s.AspirationId}"));
        (await ReadAsync(s, 70, "2025-12-08")).EnsureSuccessStatusCode();
        (await ReadAsync(s, 90, "2026-01-05")).EnsureSuccessStatusCode();
        (await ReadAsync(s, 40, "2026-01-05")).EnsureSuccessStatusCode();
        var with = (await s.Client.GetStringAsync($"/progress/{s.AspirationId}"));
        Assert.Equal(without, with);
    }

    [Fact]
    public async Task Adding_a_reading_without_a_Measure_is_refused()
    {
        var s = await SeedAsync();
        Assert.Equal(HttpStatusCode.Conflict, (await ReadAsync(s, 70, "2026-01-05")).StatusCode);
    }
}
