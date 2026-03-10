using System.Text.Json;
using Npgsql;
using StackExchange.Redis;

namespace DistributedJobSystem.Worker;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly IConnectionMultiplexer _mux;
    private readonly string _pgConnectionString;

    public Worker(ILogger<Worker> logger, IConnectionMultiplexer mux, IConfiguration configuration)
    {
        _logger = logger;
        _mux = mux;
        _pgConnectionString =
            configuration.GetSection("Postgres")["ConnectionString"]
            ?? "Host=postgres;Port=5432;Database=djs;Username=djs;Password=djs";
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var db = _mux.GetDatabase();
        _logger.LogInformation("Worker started.");

        while (!stoppingToken.IsCancellationRequested)
        {
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
            await UpdateJobRowAsync(
                job.Id,
                JobStatus.RUNNING,
                startedAt: DateTimeOffset.UtcNow,
                finishedAt: null,
                retryCount: job.Retry,
                result: null,
                error: null,
                ct: stoppingToken);

            try
            {
                var result = await ExecuteJobAsync(job, stoppingToken);
                await db.StringSetAsync(RedisKeys.JobResult(job.Id), result);
                await SetStatusAsync(db, job.Id, JobStatus.COMPLETED, stoppingToken);
                await UpdateJobRowAsync(
                    job.Id,
                    JobStatus.COMPLETED,
                    startedAt: null,
                    finishedAt: DateTimeOffset.UtcNow,
                    retryCount: job.Retry,
                    result: result,
                    error: null,
                    ct: stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job failed: {jobId} {type}", job.Id, job.Type);

                var nextRetry = job.Retry + 1;
                await db.StringSetAsync(RedisKeys.JobError(job.Id), ex.Message);

                if (nextRetry <= job.MaxRetry)
                {
                    await SetStatusAsync(db, job.Id, JobStatus.RETRYING, stoppingToken);
                    await UpdateJobRowAsync(
                        job.Id,
                        JobStatus.RETRYING,
                        startedAt: null,
                        finishedAt: null,
                        retryCount: nextRetry,
                        result: null,
                        error: ex.Message,
                        ct: stoppingToken);

                    var delaySeconds = Math.Pow(2, nextRetry);
                    await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);

                    var retryJob = job with { Retry = nextRetry };
                    await db.ListLeftPushAsync(RedisQueues.DefaultQueue, JsonSerializer.Serialize(retryJob));
                    await SetStatusAsync(db, job.Id, JobStatus.PENDING, stoppingToken);
                }
                else
                {
                    await SetStatusAsync(db, job.Id, JobStatus.FAILED, stoppingToken);
                    await UpdateJobRowAsync(
                        job.Id,
                        JobStatus.FAILED,
                        startedAt: null,
                        finishedAt: DateTimeOffset.UtcNow,
                        retryCount: nextRetry,
                        result: null,
                        error: ex.Message,
                        ct: stoppingToken);
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
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
                return $"Email sent at {DateTimeOffset.UtcNow:O}";

            case "image_processing":
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

    private async Task UpdateJobRowAsync(
        string jobId,
        JobStatus status,
        DateTimeOffset? startedAt = null,
        DateTimeOffset? finishedAt = null,
        int? retryCount = null,
        string? result = null,
        string? error = null,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = new NpgsqlConnection(_pgConnectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                update jobs
                set status = @status,
                    started_at = coalesce(@started_at, started_at),
                    finished_at = coalesce(@finished_at, finished_at),
                    retry_count = coalesce(@retry_count, retry_count),
                    result = coalesce(@result, result),
                    error_message = coalesce(@error, error_message)
                where id = @id
                """;

            cmd.Parameters.AddWithValue("@status", status.ToString());
            cmd.Parameters.AddWithValue("@id", jobId);
            cmd.Parameters.AddWithValue("@started_at", (object?)startedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@finished_at", (object?)finishedAt ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@retry_count", (object?)retryCount ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@result", (object?)result ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@error", (object?)error ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update job row for {jobId}", jobId);
        }
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
