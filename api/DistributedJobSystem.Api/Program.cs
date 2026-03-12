using System.Text.Json;
using System.Diagnostics.Metrics;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry;
using Serilog;
using StackExchange.Redis;
using DistributedJobSystem.Api;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((ctx, services, cfg) =>
    cfg.ReadFrom.Configuration(ctx.Configuration));

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddCors(options =>
{
    options.AddPolicy("Dashboard", policy =>
    {
        policy.WithOrigins("http://localhost:3000")
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisConnectionString =
        builder.Configuration.GetSection("Redis")["ConnectionString"] ?? "redis:6379";
    return ConnectionMultiplexer.Connect(redisConnectionString);
});

builder.Services.AddDbContext<JobDbContext>(options =>
{
    var connStr = builder.Configuration.GetConnectionString("JobsDatabase")
                  ?? "Host=postgres;Port=5432;Database=djs;Username=djs;Password=djs";
    options.UseNpgsql(connStr);
});

var meter = new Meter("DistributedJobSystem.Api", "1.0.0");
var jobsEnqueued = meter.CreateCounter<long>("jobs_enqueued_total");
var jobRetriesRequested = meter.CreateCounter<long>("jobs_retry_requested_total");

builder.Services.AddOpenTelemetry()
    .WithMetrics(metrics =>
    {
        metrics
            .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService("distributed-job-api"))
            .AddAspNetCoreInstrumentation()
            .AddMeter("DistributedJobSystem.Api")
            .AddPrometheusExporter();
    });

var app = builder.Build();

// Ensure database exists and add payload_json if missing (for existing DBs)
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
    db.Database.EnsureCreated();
    _ = db.Database.ExecuteSqlRawAsync("ALTER TABLE jobs ADD COLUMN IF NOT EXISTS payload_json TEXT").GetAwaiter().GetResult();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("Dashboard");

app.UseSerilogRequestLogging();

app.MapPrometheusScrapingEndpoint("/metrics");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("Health")
    .WithOpenApi();

app.MapPost("/jobs/send-email", async (SendEmailRequest req, string? priority, IConnectionMultiplexer mux, JobDbContext dbContext) =>
{
    var jobId = $"job_{Guid.NewGuid():N}";
    var createdAt = DateTimeOffset.UtcNow;
    var payloadJson = JsonSerializer.Serialize(req);

    var job = new JobMessage(
        Id: jobId,
        Type: "send_email",
        Payload: JsonSerializer.SerializeToElement(req),
        Retry: 0,
        MaxRetry: 5,
        CreatedAt: createdAt);

    var redis = mux.GetDatabase();
    var queue = RedisQueues.GetQueue(priority);
    await redis.ListLeftPushAsync(queue, JsonSerializer.Serialize(job));
    await redis.StringSetAsync(RedisKeys.JobStatus(jobId), JobStatus.PENDING.ToString());
    await redis.StringSetAsync(RedisKeys.JobUpdatedAt(jobId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    dbContext.Jobs.Add(new Job
    {
        Id = jobId,
        Type = job.Type,
        Status = JobStatus.PENDING.ToString(),
        CreatedAt = createdAt,
        RetryCount = 0,
        PayloadJson = payloadJson
    });
    await dbContext.SaveChangesAsync();

    jobsEnqueued.Add(1, new KeyValuePair<string, object?>("type", job.Type), new KeyValuePair<string, object?>("priority", priority ?? "default"));

    return Results.Accepted($"/jobs/{jobId}", new { id = jobId });
})
.WithName("EnqueueSendEmail")
.WithOpenApi();

app.MapPost("/jobs/image-processing", async (ImageProcessingRequest req, string? priority, IConnectionMultiplexer mux, JobDbContext dbContext) =>
{
    var jobId = $"job_{Guid.NewGuid():N}";
    var createdAt = DateTimeOffset.UtcNow;
    var payloadJson = JsonSerializer.Serialize(req);

    var job = new JobMessage(
        Id: jobId,
        Type: "image_processing",
        Payload: JsonSerializer.SerializeToElement(req),
        Retry: 0,
        MaxRetry: 5,
        CreatedAt: createdAt);

    var redis = mux.GetDatabase();
    var queue = RedisQueues.GetQueue(priority);
    await redis.ListLeftPushAsync(queue, JsonSerializer.Serialize(job));
    await redis.StringSetAsync(RedisKeys.JobStatus(jobId), JobStatus.PENDING.ToString());
    await redis.StringSetAsync(RedisKeys.JobUpdatedAt(jobId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    dbContext.Jobs.Add(new Job
    {
        Id = jobId,
        Type = job.Type,
        Status = JobStatus.PENDING.ToString(),
        CreatedAt = createdAt,
        RetryCount = 0,
        PayloadJson = payloadJson
    });
    await dbContext.SaveChangesAsync();

    jobsEnqueued.Add(1, new KeyValuePair<string, object?>("type", job.Type), new KeyValuePair<string, object?>("priority", priority ?? "default"));

    return Results.Accepted($"/jobs/{jobId}", new { id = jobId });
})
.WithName("EnqueueImageProcessing")
.WithOpenApi();

app.MapGet("/jobs/{jobId}", async (string jobId, JobDbContext dbContext) =>
{
    var job = await dbContext.Jobs.FindAsync(jobId);
    if (job is null)
    {
        return Results.NotFound(new { id = jobId });
    }

    return Results.Ok(job);
})
.WithName("GetJobStatus")
.WithOpenApi();

app.MapGet("/jobs", async (int page, int pageSize, JobDbContext dbContext) =>
{
    var safePage = page <= 0 ? 1 : page;
    var safePageSize = pageSize <= 0 || pageSize > 100 ? 50 : pageSize;

    var query = dbContext.Jobs
        .OrderByDescending(j => j.CreatedAt)
        .Skip((safePage - 1) * safePageSize)
        .Take(safePageSize);

    var jobs = await query.ToListAsync();

    return Results.Ok(jobs);
})
.WithName("ListJobs")
.WithOpenApi();

app.MapPost("/jobs/{jobId}/retry", async (string jobId, IConnectionMultiplexer mux, JobDbContext dbContext) =>
{
    var job = await dbContext.Jobs.FindAsync(jobId);
    if (job is null)
        return Results.NotFound(new { id = jobId });
    if (job.Status != "FAILED")
        return Results.BadRequest(new { error = "Only FAILED jobs can be retried.", status = job.Status });

    if (string.IsNullOrEmpty(job.PayloadJson))
        return Results.BadRequest(new { error = "Job has no stored payload for retry." });

    using var payloadDoc = JsonDocument.Parse(job.PayloadJson);
    var payloadElement = payloadDoc.RootElement.Clone();

    var jobMessage = new JobMessage(
        Id: job.Id,
        Type: job.Type,
        Payload: payloadElement,
        Retry: 0,
        MaxRetry: 5,
        CreatedAt: DateTimeOffset.UtcNow);

    var redis = mux.GetDatabase();
    await redis.ListLeftPushAsync(RedisQueues.DefaultQueue, JsonSerializer.Serialize(jobMessage));
    await redis.StringSetAsync(RedisKeys.JobStatus(jobId), JobStatus.PENDING.ToString());
    await redis.StringSetAsync(RedisKeys.JobUpdatedAt(jobId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    job.Status = JobStatus.PENDING.ToString();
    job.RetryCount = 0;
    job.Result = null;
    job.ErrorMessage = null;
    job.StartedAt = null;
    job.FinishedAt = null;
    await dbContext.SaveChangesAsync();

    jobRetriesRequested.Add(1, new KeyValuePair<string, object?>("type", job.Type));

    return Results.Accepted($"/jobs/{jobId}", new { id = jobId });
})
.WithName("RetryJob")
.WithOpenApi();

app.MapGet("/jobs/queue-stats", async (IConnectionMultiplexer mux) =>
{
    var db = mux.GetDatabase();
    var endpoints = mux.GetEndPoints();
    var server = endpoints.Length > 0 ? mux.GetServer(endpoints[0]) : null;

    var highLen = await db.ListLengthAsync(RedisQueues.HighQueue);
    var defaultLen = await db.ListLengthAsync(RedisQueues.DefaultQueue);
    var lowLen = await db.ListLengthAsync(RedisQueues.LowQueue);
    var deadLetterLen = await db.ListLengthAsync(RedisQueues.DeadLetterQueue);

    var activeWorkers = 0;
    if (server != null)
    {
        var workerKeys = server.Keys(pattern: "worker_heartbeat:*");
        activeWorkers = workerKeys.Count();
    }

    return Results.Ok(new
    {
        queueHigh = highLen,
        queueDefault = defaultLen,
        queueLow = lowLen,
        deadLetter = deadLetterLen,
        activeWorkers
    });
})
.WithName("QueueStats")
.WithOpenApi();

app.Run();

static class RedisQueues
{
    public const string HighQueue = "job_queue:high";
    public const string DefaultQueue = "job_queue:default";
    public const string LowQueue = "job_queue:low";
    public const string DeadLetterQueue = "job_queue:dead_letter";

    public static string GetQueue(string? priority) =>
        string.Equals(priority, "high", StringComparison.OrdinalIgnoreCase) ? HighQueue
        : string.Equals(priority, "low", StringComparison.OrdinalIgnoreCase) ? LowQueue
        : DefaultQueue;
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

record SendEmailRequest(string To, string Subject, string? Body);
record ImageProcessingRequest(string ImageUrl, int? TargetWidth, int? TargetHeight);

record JobMessage(
    string Id,
    string Type,
    JsonElement Payload,
    int Retry,
    int MaxRetry,
    DateTimeOffset CreatedAt);
