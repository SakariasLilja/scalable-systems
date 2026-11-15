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
    private readonly int maxBufferSize = 5000;
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
                bool flushFailed = false;

                while (channel.TryRead(out var record))
                {
                    if (buffer.Count >= maxBufferSize)
                    {
                        buffer.RemoveRange(0, maxBatchSize);
                        logger.LogWarning("Database buffer full; dropped oldest {Count} records", maxBatchSize);
                    }

                    buffer.Add(record);
                    if (buffer.Count >= maxBatchSize)
                    {
                        var ok = await FlushBatch(buffer, connectionString, stoppingToken);
                        lastFlush = DateTime.UtcNow;
                        if (!ok)
                        {
                            flushFailed = true;
                            break;
                        }
                    }
                }

                if (flushFailed)
                {
                    await Task.Delay(2000, stoppingToken);
                    continue;
                }

                var elapsed = DateTime.UtcNow - lastFlush;
                if (buffer.Count > 0 && elapsed >= flushInterval)
                {
                    await FlushBatch(buffer, connectionString, stoppingToken);
                    lastFlush = DateTime.UtcNow;
                }

                var remainingDelay = flushInterval - (DateTime.UtcNow - lastFlush);
                if (remainingDelay <= TimeSpan.Zero)
                {
                    remainingDelay = flushInterval;
                }

                using var timeoutCts = new CancellationTokenSource(remainingDelay);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

                try
                {
                    if (!await channel.WaitToReadAsync(linkedCts.Token))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    if (buffer.Count > 0)
                    {
                        await FlushBatch(buffer, connectionString, stoppingToken);
                        lastFlush = DateTime.UtcNow;
                    }
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

        while (buffer.Count > 0)
        {
            try
            {
                var ok = await FlushBatch(buffer, connectionString, CancellationToken.None);
                if (!ok)
                {
                    break;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error flushing remaining records during shutdown");
                break;
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

    private async Task<bool> FlushBatch(List<FinalizedWindowRecord> batch, string connectionString, CancellationToken ct)
    {
        if (batch.Count == 0) return true;

        int recordsToFlush = Math.Min(batch.Count, maxBatchSize);

        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand { Connection = connection };
            var sql = new StringBuilder();
            sql.AppendLine("INSERT INTO trading_windows (symbol_id, window_start, window_end, ema_38, ema_100, last_price, buy_signal, sell_signal) VALUES ");

            for (int i = 0; i < recordsToFlush; i++)
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
            logger.LogDebug("Flushed batch of {Count} window records to TimescaleDB", recordsToFlush);
            batch.RemoveRange(0, recordsToFlush);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to flush batch of {Count} records to database. Will retry next cycle.", recordsToFlush);
            return false;
        }
    }
}
