using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;
using DistributedJobSystem.Api;

var builder = WebApplication.CreateBuilder(args);

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

var app = builder.Build();

// Ensure database exists
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<JobDbContext>();
    db.Database.EnsureCreated();
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("Dashboard");

app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
    .WithName("Health")
    .WithOpenApi();

app.MapPost("/jobs/send-email", async (SendEmailRequest req, IConnectionMultiplexer mux, JobDbContext dbContext) =>
{
    var jobId = $"job_{Guid.NewGuid():N}";
    var createdAt = DateTimeOffset.UtcNow;

    var job = new JobMessage(
        Id: jobId,
        Type: "send_email",
        Payload: JsonSerializer.SerializeToElement(req),
        Retry: 0,
        MaxRetry: 5,
        CreatedAt: createdAt);

    var redis = mux.GetDatabase();
    await redis.ListLeftPushAsync(RedisQueues.DefaultQueue, JsonSerializer.Serialize(job));
    await redis.StringSetAsync(RedisKeys.JobStatus(jobId), JobStatus.PENDING.ToString());
    await redis.StringSetAsync(RedisKeys.JobUpdatedAt(jobId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    dbContext.Jobs.Add(new Job
    {
        Id = jobId,
        Type = job.Type,
        Status = JobStatus.PENDING.ToString(),
        CreatedAt = createdAt,
        RetryCount = 0
    });
    await dbContext.SaveChangesAsync();

    return Results.Accepted($"/jobs/{jobId}", new { id = jobId });
})
.WithName("EnqueueSendEmail")
.WithOpenApi();

app.MapPost("/jobs/image-processing", async (ImageProcessingRequest req, IConnectionMultiplexer mux, JobDbContext dbContext) =>
{
    var jobId = $"job_{Guid.NewGuid():N}";
    var createdAt = DateTimeOffset.UtcNow;

    var job = new JobMessage(
        Id: jobId,
        Type: "image_processing",
        Payload: JsonSerializer.SerializeToElement(req),
        Retry: 0,
        MaxRetry: 5,
        CreatedAt: createdAt);

    var redis = mux.GetDatabase();
    await redis.ListLeftPushAsync(RedisQueues.DefaultQueue, JsonSerializer.Serialize(job));
    await redis.StringSetAsync(RedisKeys.JobStatus(jobId), JobStatus.PENDING.ToString());
    await redis.StringSetAsync(RedisKeys.JobUpdatedAt(jobId), DateTimeOffset.UtcNow.ToUnixTimeSeconds());

    dbContext.Jobs.Add(new Job
    {
        Id = jobId,
        Type = job.Type,
        Status = JobStatus.PENDING.ToString(),
        CreatedAt = createdAt,
        RetryCount = 0
    });
    await dbContext.SaveChangesAsync();

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
