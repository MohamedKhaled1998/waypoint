using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Waypoint.Api.Auth;
using Waypoint.Api.Data;

namespace Waypoint.Api.Coach;

public static class JobKinds
{
    public const string Plan = "plan";
    public const string CheckInReply = "check_in_reply";
    public const string ReminderText = "reminder_text";
    public const string PushMessage = "push_message";
}

/// <summary>Adds jobs. Payloads are row IDs only, so no free text can reach the jobs table.</summary>
public sealed class JobQueue(WaypointDb db, TimeProvider clock)
{
    public Job Add(string kind, Guid userId, IReadOnlyDictionary<string, Guid> ids, Guid? aspirationId = null, TimeSpan? delay = null)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var job = new Job
        {
            Id = Guid.NewGuid(), UserId = userId, Kind = kind, AspirationId = aspirationId, State = JobState.Pending, NextRunAt = now + (delay ?? TimeSpan.Zero), CreatedAt = now,
            PayloadJson = JsonSerializer.Serialize(ids.ToDictionary(p => p.Key, p => p.Value.ToString())),
        };
        db.Jobs.Add(job);
        return job;
    }

    public async Task EnqueueAsync(string kind, Guid userId, IReadOnlyDictionary<string, Guid> ids, Guid? aspirationId = null, TimeSpan? delay = null, CancellationToken ct = default)
    {
        Add(kind, userId, ids, aspirationId, delay);
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Runs the jobs that are due, each as its job's user. A failure retries later with growing backoff.</summary>
public sealed class JobRunner(IServiceScopeFactory scopes, TimeProvider clock, ILogger<JobRunner> log)
{
    public const int MaxAttempts = 5;

    /// <summary>1, 5, 25, 125 minutes between attempts.</summary>
    public static TimeSpan Backoff(int attempts) => TimeSpan.FromMinutes(Math.Pow(5, attempts - 1));

    /// <summary>Runs every job due now once; returns how many it ran.</summary>
    public async Task<int> RunDueAsync(CancellationToken ct)
    {
        List<Guid> due;
        using (var scope = scopes.CreateScope())
        {
            var now = clock.GetUtcNow().UtcDateTime;
            // The runner is the one reader across users (besides sign-in): it finds due jobs for everyone.
            due = await scope.ServiceProvider.GetRequiredService<WaypointDb>().Jobs.IgnoreQueryFilters()
                .Where(j => j.State == JobState.Pending && j.NextRunAt <= now)
                .OrderBy(j => j.NextRunAt).Select(j => j.Id).ToListAsync(ct);
        }
        foreach (var id in due) await RunOneAsync(id, ct);
        return due.Count;
    }

    private async Task RunOneAsync(Guid id, CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<WaypointDb>();
        // Across users, as above: the job names its user, and the work below runs as that user.
        var job = await db.Jobs.IgnoreQueryFilters().FirstAsync(j => j.Id == id, ct);
        sp.GetRequiredService<CurrentUser>().Override = job.UserId;
        try
        {
            var ids = JsonSerializer.Deserialize<Dictionary<string, Guid>>(job.PayloadJson) ?? [];
            switch (job.Kind)
            {
                case JobKinds.Plan:
                    await sp.GetRequiredService<PlanDrafter>().DraftAsync(ids["aspirationId"], ct);
                    break;
                case JobKinds.CheckInReply:
                    await sp.GetRequiredService<CheckInReplies>().ReplyAsync(ids["checkInId"], ct);
                    break;
                case JobKinds.ReminderText:
                    await sp.GetRequiredService<ReminderWriter>().WriteAsync(ids["planVersionId"], ct);
                    break;
                case JobKinds.PushMessage:
                    await sp.GetRequiredService<Waypoint.Api.Push.PushNotifier>().SendForMessageAsync(ids["chatMessageId"], ct);
                    break;
                default:
                    throw new InvalidOperationException("unknown_job_kind");
            }
            job.State = JobState.Done;
            job.Attempts++;
            log.LogInformation("Job {JobId} {Kind} done", job.Id, job.Kind);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            job.Attempts++;
            var give = job.Attempts >= MaxAttempts;
            if (give) job.State = JobState.Failed;
            else job.NextRunAt = clock.GetUtcNow().UtcDateTime + Backoff(job.Attempts);
            log.LogWarning("Job {JobId} {Kind} attempt {Attempts} failed: {ErrorType} {Reason}{Gave}", job.Id, job.Kind,
                job.Attempts, ex.GetType().Name, (ex as ISafeReason)?.Reason,
                give ? " (giving up)" : "");
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>Polls the jobs table in-process. Off in tests, which step <see cref="JobRunner"/> themselves.</summary>
public sealed class JobWorker(JobRunner runner, ILogger<JobWorker> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await runner.RunDueAsync(ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.LogWarning("Job poll failed: {ErrorType}", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { }
        }
    }
}
