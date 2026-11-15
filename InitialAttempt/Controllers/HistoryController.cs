using InitialAttempt.Models;
using Microsoft.AspNetCore.Mvc;
using Npgsql;

namespace InitialAttempt.Controllers;

[ApiController]
[Route("api/history")]
public class HistoryController(IConfiguration configuration, ILogger<HistoryController> logger) : ControllerBase
{
    [HttpGet("{symbol}")]
    public async Task<IActionResult> GetHistory(string symbol, [FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var records = new List<FinalizedWindowRecord>();
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? configuration["POSTGRES_HOST"] ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? configuration["POSTGRES_PORT"] ?? "5432";
        var db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? configuration["POSTGRES_DB"] ?? "trading_db";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? configuration["POSTGRES_USER"] ?? "postgres";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? configuration["POSTGRES_PASSWORD"] ?? "postgres_password";

        var connectionString = $"Host={host};Port={port};Database={db};Username={user};Password={password};Timeout=5;Command Timeout=10";

        try
        {
            await using var conn = new NpgsqlConnection(connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT symbol_id, window_start, window_end, ema_38, ema_100, last_price, buy_signal, sell_signal " +
                "FROM trading_windows WHERE symbol_id = @sym ORDER BY window_start DESC LIMIT @lim;", conn);

            cmd.Parameters.AddWithValue("@sym", symbol);
            cmd.Parameters.AddWithValue("@lim", limit);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                records.Add(new FinalizedWindowRecord(
                    reader.GetString(0),
                    reader.GetDateTime(1),
                    reader.GetDateTime(2),
                    reader.GetDouble(3),
                    reader.GetDouble(4),
                    reader.GetDouble(5),
                    reader.GetBoolean(6),
                    reader.GetBoolean(7)
                ));
            }

            records.Reverse();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not fetch historical records for symbol {Symbol}", symbol);
        }

        return Ok(records);
    }
}
