using System.Text.Json;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisConnectionString =
        builder.Configuration.GetSection("Redis")["ConnectionString"] ?? "redis:6379";
    return ConnectionMultiplexer.Connect(redisConnectionString);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("Health")
    .WithOpenApi();

app.MapPost("/jobs/send-email", async (SendEmailRequest req, IConnectionMultiplexer mux) =>
{
    var jobId = $"job_{Guid.NewGuid():N}";
    var job = new JobMessage(
        Id: jobId,
        Type: "send_email",
        Payload: JsonSerializer.SerializeToElement(req),
        Retry: 0,
        MaxRetry: 5,
        CreatedAt: DateTimeOffset.UtcNow);

    var db = mux.GetDatabase();
    await db.ListLeftPushAsync(RedisQueues.DefaultQueue, JsonSerializer.Serialize(job));
    await db.StringSetAsync(RedisKeys.JobStatus(jobId), JobStatus.PENDING.ToString());
    await db.StringSetAsync(RedisKeys.JobUpdatedAt(jobId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    return Results.Accepted($"/jobs/{jobId}", new { id = jobId });
})
.WithName("EnqueueSendEmail")
.WithOpenApi();

app.MapPost("/jobs/image-processing", async (ImageProcessingRequest req, IConnectionMultiplexer mux) =>
{
    var jobId = $"job_{Guid.NewGuid():N}";
    var job = new JobMessage(
        Id: jobId,
        Type: "image_processing",
        Payload: JsonSerializer.SerializeToElement(req),
        Retry: 0,
        MaxRetry: 5,
        CreatedAt: DateTimeOffset.UtcNow);

    var db = mux.GetDatabase();
    await db.ListLeftPushAsync(RedisQueues.DefaultQueue, JsonSerializer.Serialize(job));
    await db.StringSetAsync(RedisKeys.JobStatus(jobId), JobStatus.PENDING.ToString());
    await db.StringSetAsync(RedisKeys.JobUpdatedAt(jobId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    return Results.Accepted($"/jobs/{jobId}", new { id = jobId });
})
.WithName("EnqueueImageProcessing")
.WithOpenApi();

app.MapGet("/jobs/{jobId}", async (string jobId, IConnectionMultiplexer mux) =>
{
    var db = mux.GetDatabase();
    var status = await db.StringGetAsync(RedisKeys.JobStatus(jobId));
    if (status.IsNullOrEmpty)
        return Results.NotFound(new { id = jobId });

    var updatedAt = await db.StringGetAsync(RedisKeys.JobUpdatedAt(jobId));
    var result = await db.StringGetAsync(RedisKeys.JobResult(jobId));
    var error = await db.StringGetAsync(RedisKeys.JobError(jobId));

    return Results.Ok(new
    {
        id = jobId,
        status = status.ToString(),
        updatedAtUnixSeconds = updatedAt.IsNullOrEmpty ? null : (long?)updatedAt,
        result = result.IsNullOrEmpty ? null : result.ToString(),
        error = error.IsNullOrEmpty ? null : error.ToString()
    });
})
.WithName("GetJobStatus")
.WithOpenApi();

app.Run();

static class RedisQueues
{
    public const string DefaultQueue = "job_queue:default";
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
