using DotNetEnv;
using InitialAttempt.Services;
using InitialAttempt.Utils;

Env.TraversePath().Load();

var builder = WebApplication.CreateBuilder(args);

builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

builder.Services.AddSingleton<IReadCsvService, ReadCsvService>();
builder.Services.AddSingleton<ISimulationEngine, SimulationEngine>();
builder.Services.AddSingleton<IWindowProcessor, WindowProcessor>();
builder.Services.AddSingleton<WindowStreamConsumer>();
builder.Services.AddSingleton<IWindowStreamConsumer>(sp => sp.GetRequiredService<WindowStreamConsumer>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<WindowStreamConsumer>());
builder.Services.AddHostedService<DbBatchWriter>();
builder.Services.AddSingleton<ISseService, SseService>();

builder.Services.AddSingleton<IConstants, Constants>(
    provider => new Constants(
        provider.GetRequiredService<ILogger<Constants>>(),
        provider.GetRequiredService<IConfiguration>()
    )
);
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

var consumer = app.Services.GetRequiredService<WindowStreamConsumer>();
var sseService = app.Services.GetRequiredService<ISseService>();
consumer.OnWindowFinalized += record => _ = sseService.BroadcastAdvisory(record);

app.UseCors();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
