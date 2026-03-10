using System.Text.Json;
using StackExchange.Redis;

namespace DistributedJobSystem.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConnectionMultiplexer _mux;

    public Worker(ILogger<Worker> logger, IConnectionMultiplexer mux)
    {
        _logger = logger;
        _mux = mux;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _mux.GetDatabase();
        _logger.LogInformation("Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            // BRPOP blocks until a job arrives (or timeout so we can observe cancellation)
            RedisValue jobJson;
            try
            {
                var popped = await db.ListRightPopAsync(RedisQueues.DefaultQueue);
                if (popped.IsNullOrEmpty)
                {
                    await Task.Delay(250, stoppingToken);
                    continue;
                }

                jobJson = popped;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Redis pop failed; backing off.");
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken);
                continue;
            }

            JobMessage? job;
            try
            {
                job = JsonSerializer.Deserialize<JobMessage>(jobJson!);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deserialize job: {jobJson}", jobJson.ToString());
                continue;
            }

            if (job is null || string.IsNullOrWhiteSpace(job.Id))
                continue;

            await SetStatusAsync(db, job.Id, JobStatus.RUNNING, stoppingToken);

            try
            {
                var result = await ExecuteJobAsync(job, stoppingToken);
                await db.StringSetAsync(RedisKeys.JobResult(job.Id), result);
                await SetStatusAsync(db, job.Id, JobStatus.COMPLETED, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job failed: {jobId} {type}", job.Id, job.Type);

                var nextRetry = job.Retry + 1;
                await db.StringSetAsync(RedisKeys.JobError(job.Id), ex.Message);

                if (nextRetry <= job.MaxRetry)
                {
                    await SetStatusAsync(db, job.Id, JobStatus.RETRYING, stoppingToken);

                    var delaySeconds = Math.Pow(2, nextRetry);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);

                    var retryJob = job with { Retry = nextRetry };
                    await db.ListLeftPushAsync(RedisQueues.DefaultQueue, JsonSerializer.Serialize(retryJob));
                    await SetStatusAsync(db, job.Id, JobStatus.PENDING, stoppingToken);
                }
                else
                {
                    await SetStatusAsync(db, job.Id, JobStatus.FAILED, stoppingToken);
                    await db.ListLeftPushAsync(RedisQueues.DeadLetterQueue, jobJson);
                }
            }
        }
    }

    private async Task<string> ExecuteJobAsync(JobMessage job, CancellationToken ct)
    {
        switch (job.Type)
        {
            case "send_email":
                // Simulated email send
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                return $"Email sent at {DateTimeOffset.UtcNow:O}";

            case "image_processing":
                // Simulated image processing
                await Task.Delay(TimeSpan.FromSeconds(2), ct);
                return $"Image processed at {DateTimeOffset.UtcNow:O}";

            default:
                throw new InvalidOperationException($"Unknown job type: {job.Type}");
        }
    }

    private static async Task SetStatusAsync(IDatabase db, string jobId, JobStatus status, CancellationToken ct)
    {
        await db.StringSetAsync(RedisKeys.JobStatus(jobId), status.ToString());
        await db.StringSetAsync(RedisKeys.JobUpdatedAt(jobId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    }
}

static class RedisQueues
{
    public const string DefaultQueue = "job_queue:default";
    public const string DeadLetterQueue = "job_queue:dead_letter";
}

static class RedisKeys
{
    public static string JobStatus(string jobId) => $"job_status:{jobId}";
    public static string JobUpdatedAt(string jobId) => $"job_updated_at:{jobId}";
    public static string JobResult(string jobId) => $"job_result:{jobId}";
    public static string JobError(string jobId) => $"job_error:{jobId}";
}

enum JobStatus
{
    PENDING,
    RUNNING,
    FAILED,
    COMPLETED,
    RETRYING
}

record JobMessage(
    string Id,
    string Type,
    JsonElement Payload,
    int Retry,
    int MaxRetry,
    DateTimeOffset CreatedAt);
