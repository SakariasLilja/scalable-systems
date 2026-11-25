using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trading.Shared.Models;
using Trading.Shared.Utils;

namespace Trading.Worker.Services;

public interface IDbBatchWriter
{
    void Enqueue(FinalizedWindowRecord record);
    void ClearBuffer();
}

public class DbBatchWriter(
    IConfiguration configuration,
    ILogger<DbBatchWriter> logger
) : BackgroundService, IDbBatchWriter
{
    private readonly int maxBatchSize = Constants.DbBatchMaxCapacity;
    private readonly int maxBufferSize = Constants.DbBatchMaxBufferSize;
    private readonly TimeSpan flushInterval = TimeSpan.FromMilliseconds(Constants.DbBatchFlushIntervalMs);
    private readonly Channel<FinalizedWindowRecord> channel = Channel.CreateBounded<FinalizedWindowRecord>(
        new BoundedChannelOptions(Constants.DbWriterChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly List<FinalizedWindowRecord> buffer = new(Constants.DbBatchMaxCapacity);
    private readonly object bufferLock = new();
    private readonly SemaphoreSlim flushLock = new(1, 1);
    private CancellationTokenSource flushCts = new();
    private long clearGeneration;

    public void Enqueue(FinalizedWindowRecord record)
    {
        channel.Writer.TryWrite(record);
    }

    public void ClearBuffer()
    {
        while (channel.Reader.TryRead(out _))
        {
        }

        CancellationTokenSource oldCts;
        lock (bufferLock)
        {
            buffer.Clear();
            clearGeneration++;
            oldCts = flushCts;
            flushCts = new CancellationTokenSource();
        }

        oldCts.Cancel();
        flushLock.Wait();
        try
        {
            lock (bufferLock)
            {
                buffer.Clear();
            }
        }
        finally
        {
            flushLock.Release();
            oldCts.Dispose();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var lastFlush = DateTime.UtcNow;
        var shardRouter = BuildShardRouter();

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool flushFailed = false;

                while (channel.Reader.TryRead(out var record))
                {
                    int currentCount;
                    lock (bufferLock)
                    {
                        if (buffer.Count >= maxBufferSize)
                        {
                            buffer.RemoveRange(0, maxBatchSize);
                            logger.LogWarning("Database buffer full; dropped oldest {Count} records", maxBatchSize);
                        }

                        buffer.Add(record);
                        currentCount = buffer.Count;
                    }

                    if (currentCount >= maxBatchSize)
                    {
                        var ok = await FlushBatch(shardRouter, stoppingToken);
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
                    await Task.Delay(Constants.DbBatchRetryDelayMs, stoppingToken);
                    continue;
                }

                if (HasPendingRecords() && DateTime.UtcNow - lastFlush >= flushInterval)
                {
                    await FlushBatch(shardRouter, stoppingToken);
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
                    if (!await channel.Reader.WaitToReadAsync(linkedCts.Token))
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    if (HasPendingRecords())
                    {
                        await FlushBatch(shardRouter, stoppingToken);
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
                await Task.Delay(Constants.DbErrorBackoffMs, stoppingToken);
            }
        }

        while (HasPendingRecords())
        {
            var ok = await FlushBatch(shardRouter, CancellationToken.None);
            if (!ok)
            {
                break;
            }
        }
    }

    private bool HasPendingRecords()
    {
        lock (bufferLock)
        {
            return buffer.Count > 0;
        }
    }

    private DbShardRouter BuildShardRouter()
    {
        var shardsCsv = Environment.GetEnvironmentVariable("TIMESCALEDB_SHARDS") ?? configuration["TIMESCALEDB_SHARDS"];
        var configured = configuration.GetConnectionString("TimescaleDb");
        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? configuration["POSTGRES_HOST"] ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? configuration["POSTGRES_PORT"] ?? "5432";
        var db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? configuration["POSTGRES_DB"] ?? "trading_db";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? configuration["POSTGRES_USER"] ?? "postgres";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? configuration["POSTGRES_PASSWORD"] ?? "postgres_password";

        return new DbShardRouter(shardsCsv, configured, host, port, db, user, password);
    }

    private async Task<bool> FlushBatch(DbShardRouter shardRouter, CancellationToken ct)
    {
        await flushLock.WaitAsync(ct);
        try
        {
            List<FinalizedWindowRecord> snapshot;
            CancellationToken flushToken;
            long gen;

            lock (bufferLock)
            {
                if (buffer.Count == 0)
                {
                    return true;
                }

                int count = Math.Min(buffer.Count, maxBatchSize);
                snapshot = buffer.GetRange(0, count);
                gen = clearGeneration;
                flushToken = flushCts.Token;
            }

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, flushToken);

            try
            {
                var groups = snapshot
                    .GroupBy(r => shardRouter.GetConnectionStringForSymbol(r.SymbolId))
                    .Select(g => WriteShardBatchAsync(g.Key, g.ToList(), linkedCts.Token))
                    .ToArray();

                await Task.WhenAll(groups);

                lock (bufferLock)
                {
                    if (gen == clearGeneration && buffer.Count >= snapshot.Count)
                    {
                        buffer.RemoveRange(0, snapshot.Count);
                    }
                }

                return true;
            }
            catch (OperationCanceledException) when (flushToken.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                return true;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to flush batch of {Count} records to database. Will retry next cycle.", snapshot.Count);
                return false;
            }
        }
        finally
        {
            flushLock.Release();
        }
    }

    private static async Task WriteShardBatchAsync(string connectionString, List<FinalizedWindowRecord> rows, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand { Connection = connection };
        var sql = new StringBuilder();
        sql.AppendLine("INSERT INTO trading_windows (symbol_id, window_start, window_end, ema_38, ema_100, last_price, buy_signal, sell_signal) VALUES ");

        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
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

        sql.AppendLine(" ON CONFLICT (symbol_id, window_start) DO UPDATE SET window_end = EXCLUDED.window_end, ema_38 = EXCLUDED.ema_38, ema_100 = EXCLUDED.ema_100, last_price = EXCLUDED.last_price, buy_signal = EXCLUDED.buy_signal, sell_signal = EXCLUDED.sell_signal;");
        cmd.CommandText = sql.ToString();

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
