using StackExchange.Redis;
using Trading.Shared.Services;
using Trading.Worker.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();

var redisConnection = builder.Configuration["REDIS_CONNECTION"]
    ?? Environment.GetEnvironmentVariable("REDIS_CONNECTION")
    ?? "redis:6379";

var redisOptions = ConfigurationOptions.Parse(redisConnection);
redisOptions.AbortOnConnectFail = false;

builder.Services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(redisOptions));
builder.Services.AddSingleton<IWindowProcessor, WindowProcessor>();
builder.Services.AddSingleton<IWindowPublisher, RedisWindowPublisher>();

builder.Services.AddSingleton<AsyncWarningLogger>();
builder.Services.AddSingleton<IAsyncWarningLogger>(sp => sp.GetRequiredService<AsyncWarningLogger>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<AsyncWarningLogger>());

builder.Services.AddSingleton<DbBatchWriter>();
builder.Services.AddSingleton<IDbBatchWriter>(sp => sp.GetRequiredService<DbBatchWriter>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<DbBatchWriter>());

builder.Services.AddSingleton<TickIngestionService>();
builder.Services.AddSingleton<ITickIngestionService>(sp => sp.GetRequiredService<TickIngestionService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<TickIngestionService>());

builder.Services.AddControllers();

var app = builder.Build();
app.MapControllers();
app.Run();
