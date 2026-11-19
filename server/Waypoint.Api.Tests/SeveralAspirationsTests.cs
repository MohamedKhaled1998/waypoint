using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>Up to three active Aspirations share one Time budget and the same Slots.</summary>
public sealed class SeveralAspirationsTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private static JsonObject Hand(string shortName, string settleFor, bool withUser)
    {
        var h = HandOff(settleFor);
        h["aspiration"]!["short_name"] = shortName;
        h["aspiration"]!["hoped_for_date"] = "2026-05-15";
        if (withUser)
        {
            // One Slot that holds two 30-minute Actions a day.
            var slot = h["user"]!["slots"]![0]!.AsObject();
            slot["days"] = new JsonArray("mon", "tue", "wed", "thu", "fri");
            slot["end"] = "08:00";
        }
        else h.Remove("user");
        return h;
    }

    private async Task<Guid> AddAspirationAsync(HttpClient c, string shortName, string settleFor, bool first = false)
    {
        factory.Coach.Fail = false;
        var created = await c.PostAsJsonAsync("/interviews", new { ownWords = shortName });
        var id = (await created.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        factory.Coach.Enqueue($"Here is what I heard.\n{Marker}\n{Hand(shortName, settleFor, first).ToJsonString()}");
        (await c.PostAsJsonAsync($"/interviews/{id}/messages", new { text = "Mornings", clientId = "m-" + id })).EnsureSuccessStatusCode();
        var confirm = await c.PostAsJsonAsync($"/interviews/{id}/confirm", new { shortName = (string?)null });
        Assert.True(confirm.IsSuccessStatusCode, await confirm.Content.ReadAsStringAsync());
        return (await confirm.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("aspirationId").GetGuid();
    }

    private async Task ApproveAsync(HttpClient c, Guid aspirationId)
    {
        var version = await DraftedAsync(c, aspirationId, Plan());
        (await c.PostAsync($"/plan-versions/{version}/approve", null)).EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task A_fourth_active_Aspiration_is_refused()
    {
        var (c, _) = await SignedInAsync();
        await AddAspirationAsync(c, "One", "2026-12-31", first: true);
        await AddAspirationAsync(c, "Two", "2026-12-31");
        await AddAspirationAsync(c, "Three", "2026-12-31");

        var response = await c.PostAsJsonAsync("/interviews", new { ownWords = "a fourth thing" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("too_many_active_aspirations", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error").GetString());
    }

    [Fact]
    public async Task Three_Aspirations_share_a_Slot_within_its_room_each_keep_a_weekly_Action_and_the_one_nearest_its_Settle_for_line_wins()
    {
        var (c, _) = await SignedInAsync();
        // Created in the opposite order to their urgency: the Settle-for line, not who came first, decides.
        // Each Plan job runs before the next Interview, so the scripted Plan goes to the right Aspiration.
        var lax = await AddAspirationAsync(c, "Lax", "2026-12-31", first: true);
        await ApproveAsync(c, lax);
        var tight = await AddAspirationAsync(c, "Tight", "2026-06-02");
        await ApproveAsync(c, tight);
        var mid = await AddAspirationAsync(c, "Mid", "2026-09-01");
        await ApproveAsync(c, mid);

        var actions = (await c.GetFromJsonAsync<JsonElement>("/scheduled-actions")).EnumerateArray().ToList();
        string Date(JsonElement a) => a.GetProperty("date").GetString()!;
        Guid Who(JsonElement a) => a.GetProperty("aspirationId").GetGuid();

        // Day capacity: the Slot holds 60 minutes.
        foreach (var day in actions.GroupBy(Date))
            Assert.True(day.Sum(a => a.GetProperty("minutes").GetInt32()) <= 60, $"{day.Key} is over the Slot");

        // The weekly minimum: Mon 5 Jan and Mon 12 Jan weeks.
        foreach (var id in new[] { lax, tight, mid })
        {
            Assert.Contains(actions, a => Who(a) == id && string.CompareOrdinal(Date(a), "2026-01-12") < 0);
            Assert.Contains(actions, a => Who(a) == id && string.CompareOrdinal(Date(a), "2026-01-12") >= 0);
        }

        // Priority: Tight keeps everything it planned; Mid beats Lax.
        Assert.Equal(5, actions.Count(a => Who(a) == tight));
        Assert.True(actions.Count(a => Who(a) == mid) >= actions.Count(a => Who(a) == lax));
        // Lax gets only its weekly minimum: one Action a week. The removal of its other unchecked Actions is intended (see
        // Scheduling.ResolveAsync): it leaves no reminder or job behind, and the next Weekly refresh plans from what remains.
        Assert.Equal(2, actions.Count(a => Who(a) == lax));
    }

    [Fact]
    public async Task A_stale_approved_PlanVersion_does_not_change_which_Aspiration_is_closest_to_its_Settle_for_line()
    {
        // Jobs the earlier tests left behind run first, without eating this test's scripted Plans.
        while (await RunJobsAsync() > 0) { }
        var (c, userId) = await SignedInAsync();
        var lax = await AddAspirationAsync(c, "Lax", "2026-12-31", first: true);
        await ApproveAsync(c, lax);
        var tight = await AddAspirationAsync(c, "Tight", "2026-06-02");
        await ApproveAsync(c, tight);

        var mid = await AddAspirationAsync(c, "Mid", "2026-09-01");
        var midVersion = await DraftedAsync(c, mid, Plan());

        // Lax also has an older approved version whose last Milestone sits right on its Settle-for line.
        using (var scope = factory.Services.CreateScope())
        {
            scope.ServiceProvider.GetRequiredService<Waypoint.Api.Auth.CurrentUser>().Override = userId;
            var db = scope.ServiceProvider.GetRequiredService<Waypoint.Api.Data.WaypointDb>();
            // Several of them, so a lookup that takes whichever comes first is likely to meet one.
            for (var n = -5; n < 0; n++)
            {
            var stale = new Waypoint.Api.Data.PlanVersion
            {
                Id = Guid.NewGuid(), UserId = userId, AspirationId = lax, Number = n, Status = Waypoint.Api.Data.PlanVersionStatus.Approved,
                Rationale = "", Reason = "first_plan", Ramp = "", AssumptionsJson = "[]", TwoWeeksJson = "[]",
                StartDate = new DateOnly(2026, 1, 5), CreatedAt = DateTime.UtcNow,
            };
            db.PlanVersions.Add(stale);
            db.Milestones.Add(new Waypoint.Api.Data.Milestone
            {
                Id = Guid.NewGuid(), UserId = userId, PlanVersionId = stale.Id, Ref = "m0", Date = new DateOnly(2026, 12, 31),
                Title = "Old", ReachedWhen = "old", Kind = "effort", State = "upcoming",
            });
            }
            await db.SaveChangesAsync();
        }
        (await c.PostAsync($"/plan-versions/{midVersion}/approve", null)).EnsureSuccessStatusCode();

        var actions = (await c.GetFromJsonAsync<JsonElement>("/scheduled-actions")).EnumerateArray().ToList();
        Assert.Equal(5, actions.Count(a => a.GetProperty("aspirationId").GetGuid() == tight));
        Assert.Equal(2, actions.Count(a => a.GetProperty("aspirationId").GetGuid() == lax));
    }
}
