using System.Text;
using InitialAttempt.Models;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace InitialAttempt.Services;

public class DbBatchWriter(
    IWindowStreamConsumer streamConsumer,
    IConfiguration configuration,
    ILogger<DbBatchWriter> logger
) : BackgroundService
{
    private readonly int maxBatchSize = 1000;
    private readonly TimeSpan flushInterval = TimeSpan.FromSeconds(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channel = streamConsumer.GetFinalizedChannel();
        var buffer = new List<FinalizedWindowRecord>(maxBatchSize);
        var lastFlush = DateTime.UtcNow;

        var connectionString = BuildConnectionString();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (channel.TryRead(out var record))
                {
                    buffer.Add(record);
                    if (buffer.Count >= maxBatchSize)
                    {
                        await FlushBatch(buffer, connectionString, stoppingToken);
                        lastFlush = DateTime.UtcNow;
                    }
                }

                if (buffer.Count > 0 && DateTime.UtcNow - lastFlush >= flushInterval)
                {
                    await FlushBatch(buffer, connectionString, stoppingToken);
                    lastFlush = DateTime.UtcNow;
                }

                if (await channel.WaitToReadAsync(stoppingToken))
                {
                    continue;
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error processing database batch queue");
                await Task.Delay(1000, stoppingToken);
            }
        }

        if (buffer.Count > 0)
        {
            try
            {
                await FlushBatch(buffer, connectionString, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error flushing remaining records during shutdown");
            }
        }
    }

    private string BuildConnectionString()
    {
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? configuration["POSTGRES_HOST"] ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? configuration["POSTGRES_PORT"] ?? "5432";
        var db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? configuration["POSTGRES_DB"] ?? "trading_db";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? configuration["POSTGRES_USER"] ?? "postgres";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? configuration["POSTGRES_PASSWORD"] ?? "postgres_password";

        return $"Host={host};Port={port};Database={db};Username={user};Password={password};Timeout=5;Command Timeout=10";
    }

    private async Task FlushBatch(List<FinalizedWindowRecord> batch, string connectionString, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand
            {
                Connection = connection
            };

            var sql = new StringBuilder();
            sql.AppendLine("INSERT INTO trading_windows (symbol_id, window_start, window_end, ema_38, ema_100, last_price, buy_signal, sell_signal) VALUES ");

            for (int i = 0; i < batch.Count; i++)
            {
                var r = batch[i];
                if (i > 0) sql.Append(", ");
                sql.Append($"(@s{i}, @ws{i}, @we{i}, @e38_{i}, @e100_{i}, @p{i}, @b{i}, @sell{i})");

                cmd.Parameters.AddWithValue($"@s{i}", r.SymbolId);
                cmd.Parameters.AddWithValue($"@ws{i}", r.WindowStart);
                cmd.Parameters.AddWithValue($"@we{i}", r.WindowEnd);
                cmd.Parameters.AddWithValue($"@e38_{i}", r.Ema38);
                cmd.Parameters.AddWithValue($"@e100_{i}", r.Ema100);
                cmd.Parameters.AddWithValue($"@p{i}", r.LastPrice);
                cmd.Parameters.AddWithValue($"@b{i}", r.BuySignal);
                cmd.Parameters.AddWithValue($"@sell{i}", r.SellSignal);
            }

            sql.AppendLine(" ON CONFLICT (symbol_id, window_start) DO NOTHING;");
            cmd.CommandText = sql.ToString();

            await cmd.ExecuteNonQueryAsync(ct);
            logger.LogDebug("Flushed batch of {Count} window records to TimescaleDB", batch.Count);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to flush batch of {Count} records to database. Will retry next cycle.", batch.Count);
            return;
        }

        batch.Clear();
    }
}
