using DotNetEnv;
using InitialAttempt.Services;
using InitialAttempt.Utils;

Env.Load();

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Logging.AddConsole();
builder.Logging.AddDebug();

builder.Services.AddScoped<IReadCsvService, ReadCsvService>();
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

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseAuthorization();

app.MapControllers();

app.Run();
