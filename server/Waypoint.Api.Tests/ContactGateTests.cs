using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Waypoint.Api.Auth;
using Waypoint.Api.Coach;
using Waypoint.Api.Data;
using Waypoint.Api.Push;
using Waypoint.Api.Tests.Support;

namespace Waypoint.Api.Tests;

/// <summary>Contact frequency, quiet hours and the one gate every unprompted Coach message goes through; has its own factory and clock.</summary>
public sealed class ContactGateTests(WaypointApiFactory factory) : PlanTestBase(factory), IClassFixture<WaypointApiFactory>
{
    private DateTime Now => factory.Clock.GetUtcNow().UtcDateTime;

    /// <summary>Moves the fake clock forward to the next time of day (UTC).</summary>
    private void AdvanceToTime(int hour, int minute = 0)
    {
        var at = Now.Date.AddHours(hour).AddMinutes(minute);
        if (at <= Now) at = at.AddDays(1);
        factory.Clock.Advance(at - Now);
    }

    private async Task<(Guid UserId, Guid AspirationId)> UserAsync(string frequency, string settleFor = "2027-01-01", string? quietStart = null, string? quietEnd = null)
    {
        var hand = HandOff(settleFor);
        hand["user"]!["contact_frequency"] = frequency;
        var (_, userId, _, aspirationId) = await ConfirmedAsync(hand);
        if (quietStart is not null)
            await WithScope(userId, async (sp, db) =>
            {
                var u = await db.Users.SingleAsync();
                u.QuietStart = quietStart; u.QuietEnd = quietEnd!;
                await db.SaveChangesAsync();
                return 0;
            });
        return (userId, aspirationId);
    }

    private async Task<T> WithScope<T>(Guid userId, Func<IServiceProvider, WaypointDb, Task<T>> work)
    {
        using var scope = factory.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<CurrentUser>().Override = userId;
        return await work(scope.ServiceProvider, scope.ServiceProvider.GetRequiredService<WaypointDb>());
    }

    /// <summary>Seeds Coach messages sent this long ago; unprompted ones count toward Contact frequency, replies do not.</summary>
    private Task SeedAsync(Guid userId, bool unprompted, params double[] hoursAgo) => WithScope(userId, async (_, db) =>
    {
        foreach (var h in hoursAgo)
            db.ChatMessages.Add(new ChatMessage { Id = Guid.NewGuid(), UserId = userId, Role = "coach", Text = "hi", CreatedAt = Now.AddHours(-h), Unprompted = unprompted });
        await db.SaveChangesAsync();
        return 0;
    });

    private Task<GateDecision> DecideAsync(Guid userId, UnpromptedRequest request) =>
        WithScope(userId, (sp, _) => sp.GetRequiredService<ContactGate>().DecideAsync(request, CancellationToken.None));

    private static DateTime At(DateTime now, double hoursFromNow) => now.AddHours(hoursFromNow);

    // Each frequency's cap and spacing. Hours are how long ago each counted message went; hold is hours from now (null: may send).
    [Theory]
    [InlineData("Gentle", UnpromptedKind.CoachCheckIn, new double[0], null)]
    [InlineData("Gentle", UnpromptedKind.CoachCheckIn, new[] { 2.0 }, 22.0)]
    [InlineData("Gentle", UnpromptedKind.CoachCheckIn, new[] { 25.0 }, null)]
    [InlineData("Gentle", UnpromptedKind.SilenceNudge, new[] { 2.0 }, 22.0)]
    [InlineData("Chatty", UnpromptedKind.CoachCheckIn, new[] { 1.0, 2.0 }, null)]
    [InlineData("Chatty", UnpromptedKind.CoachCheckIn, new[] { 1.0, 2.0, 3.0 }, 21.0)]
    [InlineData("Chatty", UnpromptedKind.WelcomeBack, new[] { 1.0, 2.0, 3.0, 30.0 }, 21.0)]
    [InlineData("Quiet", UnpromptedKind.CoachCheckIn, new double[0], null)]
    [InlineData("Quiet", UnpromptedKind.CoachCheckIn, new[] { 24.0 }, 24.0)] // spaced two days apart
    [InlineData("Quiet", UnpromptedKind.CoachCheckIn, new[] { 49.0 }, null)]
    [InlineData("Quiet", UnpromptedKind.SilenceNudge, new[] { 24.0 }, null)] // nudges ignore Quiet's spacing
    [InlineData("Quiet", UnpromptedKind.SilenceNudge, new[] { 50.0, 100.0, 150.0 }, 19.0)] // ...but still count toward the cap
    [InlineData("Quiet", UnpromptedKind.CoachCheckIn, new[] { 50.0, 100.0, 150.0 }, 19.0)]
    public async Task Contact_frequency_caps_and_spaces_unprompted_messages(string frequency, UnpromptedKind kind, double[] sentHoursAgo, double? holdHours)
    {
        AdvanceToTime(12); // midday: far from quiet hours for every case below
        var (userId, aspirationId) = await UserAsync(frequency);
        await SeedAsync(userId, unprompted: true, sentHoursAgo);

        var decision = await DecideAsync(userId, new UnpromptedRequest(kind, aspirationId));

        if (holdHours is null) Assert.IsType<GateDecision.Send>(decision);
        else
        {
            var hold = Assert.IsType<GateDecision.Hold>(decision);
            Assert.Equal(At(Now, holdHours.Value), hold.Until, TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public async Task Replies_and_other_messages_the_user_asked_for_never_count()
    {
        AdvanceToTime(12);
        var (userId, aspirationId) = await UserAsync("Gentle");
        await SeedAsync(userId, unprompted: false, 0.1, 0.2, 0.3);

        Assert.IsType<GateDecision.Send>(await DecideAsync(userId, new UnpromptedRequest(UnpromptedKind.CoachCheckIn, aspirationId)));
    }

    [Fact]
    public async Task A_posted_unprompted_message_counts_but_a_reminder_text_job_does_not()
    {
        AdvanceToTime(12);
        var (userId, aspirationId) = await UserAsync("Gentle");
        await WithScope(userId, async (sp, _) =>
        {
            await sp.GetRequiredService<CoachMessenger>().PostUnpromptedAsync(new GateDecision.Send(), aspirationId, "How did it go?", CancellationToken.None);
            return 0;
        });

        Assert.IsType<GateDecision.Hold>(await DecideAsync(userId, new UnpromptedRequest(UnpromptedKind.CoachCheckIn, aspirationId)));
    }

    public static IEnumerable<object?[]> QuietCases() =>
    [
        // time of day (UTC), quiet start, quiet end, ValidUntil hours from now, expected: "send" | "hold:<hours from now>" | "drop"
        [12, 0, "21:30", "07:00", null, "send"],
        [21, 29, "21:30", "07:00", null, "send"],
        [21, 30, "21:30", "07:00", null, "hold:9.5"],
        [23, 0, "21:30", "07:00", null, "hold:8"],
        [3, 0, "21:30", "07:00", null, "hold:4"],
        [7, 0, "21:30", "07:00", null, "send"],
        [23, 0, "21:30", "07:00", 6.0, "drop"],      // stale by the time quiet hours end
        [23, 0, "21:30", "07:00", 10.0, "hold:8"],   // still worth sending at 07:00
        [22, 30, "22:00", "08:00", null, "hold:9.5"], // the user's own quiet hours
        [21, 45, "22:00", "08:00", null, "send"],
        [14, 0, "13:00", "15:00", null, "hold:1"],    // a span that does not cross midnight
    ];

    [Theory]
    [MemberData(nameof(QuietCases))]
    public async Task Quiet_hours_hold_unprompted_messages_until_they_end_and_drop_the_stale(int hour, int minute, string start, string end, double? validHours, string expected)
    {
        var (userId, aspirationId) = await UserAsync("Chatty", quietStart: start, quietEnd: end);
        AdvanceToTime(hour, minute);
        DateTime? valid = validHours is { } v ? Now.AddHours(v) : null;

        var decision = await DecideAsync(userId, new UnpromptedRequest(UnpromptedKind.CoachCheckIn, aspirationId, valid));

        switch (expected.Split(':'))
        {
            case ["send"]: Assert.IsType<GateDecision.Send>(decision); break;
            case ["drop"]: Assert.IsType<GateDecision.Drop>(decision); break;
            case ["hold", var h]:
                Assert.Equal(Now.AddHours(double.Parse(h, System.Globalization.CultureInfo.InvariantCulture)), Assert.IsType<GateDecision.Hold>(decision).Until, TimeSpan.FromSeconds(1));
                break;
        }
    }

    [Fact]
    public async Task The_default_quiet_hours_are_21_30_to_07_00()
    {
        var (userId, _) = await UserAsync("Gentle");
        var user = await WithScope(userId, async (_, db) => await db.Users.SingleAsync());
        Assert.Equal(("21:30", "07:00"), (user.QuietStart, user.QuietEnd));
    }

    // Every unprompted sender goes through the gate, and a held message waits for the gate's time.

    /// <summary>Moves the clock to the next Monday 09:00 and returns the days to shift the sample Plan so it starts the next day.</summary>
    private int AlignToMonday()
    {
        var next = Now.Date.AddHours(9);
        while (next < Now || next.DayOfWeek != DayOfWeek.Monday) next = next.AddDays(1);
        factory.Clock.Advance(next - Now);
        return DateOnly.FromDateTime(next.AddDays(1)).DayNumber - new DateOnly(2026, 1, 6).DayNumber;
    }

    private async Task<(HttpClient Client, Guid UserId, Guid AspirationId)> ApprovedAsync(string settleFor)
    {
        var shift = AlignToMonday();
        await RunJobsAsync();
        var hand = HandOff(settleFor);
        hand["aspiration"]!["hoped_for_date"] = "2026-12-20";
        var (c, userId, _, aspirationId) = await ConfirmedAsync(hand);
        var plan = ShiftedPlan(shift);
        plan["tasks"]![1]!["minutes"] = 10;
        var versionId = await DraftedAsync(c, aspirationId, plan);
        (await c.PostAsync($"/plan-versions/{versionId}/approve", null)).EnsureSuccessStatusCode();
        return (c, userId, aspirationId);
    }

    private async Task DrainJobsAsync() { while (await RunJobsAsync() > 0) { } }

    private async Task<JsonElement[]> CoachLinesAsync(Guid userId, string card) =>
        (await (await ReauthAsync(userId)).GetFromJsonAsync<JsonElement[]>("/coach/messages"))!
            .Where(m => m.GetProperty("card").ValueKind == JsonValueKind.String && m.GetProperty("card").GetString() == card).ToArray();

    [Fact]
    public async Task A_silence_nudge_over_the_cap_waits_for_the_gate_then_goes()
    {
        var (c, userId, aspirationId) = await ApprovedAsync("2027-01-01"); // Monday 09:00
        await DrainJobsAsync();

        // Silence turns 3 days at Thursday 09:00 but Gentle's one message for the day went an hour before.
        factory.Clock.Advance(TimeSpan.FromDays(3) - TimeSpan.FromMinutes(1));
        await SeedAsync(userId, unprompted: true, 1);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        factory.Coach.Enqueue("Thinking of you.");
        await DrainJobsAsync();
        Assert.Empty(await CoachLinesAsync(userId, "silence")); // held: the day's one message is not free yet

        factory.Clock.Advance(TimeSpan.FromHours(23));
        await DrainJobsAsync();
        Assert.Single(await CoachLinesAsync(userId, "silence"));
    }

    [Fact]
    public async Task A_welcome_back_inside_quiet_hours_is_held_until_they_end()
    {
        var (c, userId, _) = await ApprovedAsync("2027-01-01");
        var pause = await c.PostAsJsonAsync("/pauses", new { clientId = "p-" + Guid.NewGuid().ToString("N"), open = true });
        pause.EnsureSuccessStatusCode();
        var id = (await pause.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();

        factory.Clock.Advance(TimeSpan.FromDays(3));
        AdvanceToTime(23); // the user resumes late at night
        c = await ReauthAsync(userId);
        (await c.PostAsync($"/pauses/{id}/resume", null)).EnsureSuccessStatusCode();
        factory.Coach.Enqueue("Welcome back.");
        await DrainJobsAsync();
        Assert.Empty(await CoachLinesAsync(userId, "welcome_back"));

        AdvanceToTime(7);
        await DrainJobsAsync();
        Assert.Single(await CoachLinesAsync(userId, "welcome_back"));
    }

    [Fact]
    public async Task Over_the_cap_the_message_for_the_Aspiration_closest_to_its_Settle_for_line_goes_first()
    {
        AdvanceToTime(12);
        var (userId, nearId) = await UserAsync("Gentle", settleFor: "2027-01-01");
        var farId = await AddAspirationAsync(userId, "2027-06-01");
        // Both messages are due now, and Gentle has room for one.
        await WithScope(userId, async (sp, db) =>
        {
            var jobs = sp.GetRequiredService<JobQueue>();
            foreach (var asp in new[] { farId, nearId })
                jobs.Add(JobKinds.WelcomeBack, userId, new Dictionary<string, Guid> { ["pauseId"] = Guid.NewGuid() }, asp);
            await db.SaveChangesAsync();
            return 0;
        });

        var far = await DecideAsync(userId, new UnpromptedRequest(UnpromptedKind.WelcomeBack, farId));
        var near = await DecideAsync(userId, new UnpromptedRequest(UnpromptedKind.WelcomeBack, nearId));

        Assert.IsType<GateDecision.Send>(near);
        Assert.Equal(Now + ContactGate.PriorityRetry, Assert.IsType<GateDecision.Hold>(far).Until);
    }

    [Fact]
    public async Task With_room_for_both_the_one_further_from_its_Settle_for_line_is_not_held()
    {
        AdvanceToTime(12);
        var (userId, nearId) = await UserAsync("Chatty", settleFor: "2027-01-01");
        var farId = await AddAspirationAsync(userId, "2027-06-01");
        await WithScope(userId, async (sp, db) =>
        {
            var jobs = sp.GetRequiredService<JobQueue>();
            foreach (var asp in new[] { farId, nearId })
                jobs.Add(JobKinds.WelcomeBack, userId, new Dictionary<string, Guid> { ["pauseId"] = Guid.NewGuid() }, asp);
            await db.SaveChangesAsync();
            return 0;
        });

        Assert.IsType<GateDecision.Send>(await DecideAsync(userId, new UnpromptedRequest(UnpromptedKind.WelcomeBack, farId)));
    }

    private Task<Guid> AddAspirationAsync(Guid userId, string settleFor) => WithScope(userId, async (_, db) =>
    {
        var first = await db.Aspirations.FirstAsync();
        var id = Guid.NewGuid();
        db.Aspirations.Add(new Aspiration
        {
            Id = id, UserId = userId, ShortName = "Guitar", OwnWords = "learn guitar", Hue = "teal", Status = first.Status,
            HopedForDate = first.HopedForDate, SettleForDate = DateOnly.Parse(settleFor), OnSkip = first.OnSkip, CreatedAt = Now,
        });
        await db.SaveChangesAsync();
        return id;
    });
}
