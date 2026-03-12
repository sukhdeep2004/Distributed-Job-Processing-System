using DistributedJobSystem.Worker;
using Serilog;
using StackExchange.Redis;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .CreateLogger();
builder.Services.AddSerilog();

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisConnectionString =
        builder.Configuration.GetSection("Redis")["ConnectionString"] ?? "redis:6379";
    return ConnectionMultiplexer.Connect(redisConnectionString);
});

builder.Services.AddHostedService<Worker>();

var host = builder.Build();
host.Run();
