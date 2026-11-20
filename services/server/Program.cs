using StackExchange.Redis;
using Trading.Server.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var redisConnection = builder.Configuration["REDIS_CONNECTION"]
    ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION")
    ?? "redis:6379";

var redisOptions = ConfigurationOptions.Parse(redisConnection);
redisOptions.AbortOnConnectFail = false;

var workerUrl = builder.Configuration["WORKER_URL"]
    ?? Environment.GetEnvironmentVariable("WORKER_URL")
    ?? "http://worker:8080";

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddHttpClient("worker", client =>
{
    client.BaseAddress = new Uri(workerUrl);
});

builder.Services.AddSingleton<ISseService, SseService>();
builder.Services.AddSingleton<IDatabaseQueryService, DatabaseQueryService>();
builder.Services.AddSingleton<IWorkerResetClient, WorkerResetClient>();
builder.Services.AddSingleton<IRedisResetPublisher, RedisResetPublisher>();
builder.Services.AddHostedService<RedisWindowSubscriber>();

builder.Services.AddControllers();

var app = builder.Build();

app.UseCors();
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
