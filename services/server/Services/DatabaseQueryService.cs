using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using Trading.Shared.Models;
using Trading.Shared.Utils;

namespace Trading.Server.Services;

public interface IDatabaseQueryService
{
    Task<IReadOnlyList<FinalizedWindowRecord>> GetHistoryAsync(string symbol, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<string>> SearchSymbolsAsync(string? query, int limit, CancellationToken ct = default);
    Task TruncateTradingWindowsAsync(CancellationToken ct = default);
    void RecordLiveSymbol(string symbolId, DateTime windowEnd);
    void ClearLiveSymbols();
    int LiveSymbolCount { get; }
    DateTime? LatestSimulationTime { get; }
}

public class DatabaseQueryService(
    IConfiguration configuration,
    ILogger<DatabaseQueryService> logger
) : IDatabaseQueryService
{
    private readonly ConcurrentDictionary<string, byte> liveSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly object timeLock = new();
    private DateTime? latestSimulationTime;

    public int LiveSymbolCount => liveSymbols.Count;

    public DateTime? LatestSimulationTime
    {
        get
        {
            lock (timeLock)
            {
                return latestSimulationTime;
            }
        }
    }

    public void RecordLiveSymbol(string symbolId, DateTime windowEnd)
    {
        if (!string.IsNullOrWhiteSpace(symbolId))
        {
            liveSymbols.TryAdd(symbolId, 0);
        }

        lock (timeLock)
        {
            if (!latestSimulationTime.HasValue || windowEnd > latestSimulationTime.Value)
            {
                latestSimulationTime = windowEnd;
            }
        }
    }

    public void ClearLiveSymbols()
    {
        liveSymbols.Clear();
        lock (timeLock)
        {
            latestSimulationTime = null;
        }
    }

    public static IReadOnlyList<string> FilterSymbols(IEnumerable<string> symbols, string? query, int limit)
    {
        int effectiveLimit = limit <= 0 ? Constants.DefaultSymbolSearchLimit : limit;
        string trimmed = query?.Trim() ?? string.Empty;

        var filtered = symbols
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrEmpty(trimmed))
        {
            filtered = filtered.Where(s => s.Contains(trimmed, StringComparison.OrdinalIgnoreCase));
        }

        return filtered
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .Take(effectiveLimit)
            .ToList();
    }

    public async Task<IReadOnlyList<FinalizedWindowRecord>> GetHistoryAsync(string symbol, int limit, CancellationToken ct = default)
    {
        int effectiveLimit = limit <= 0 ? Constants.DefaultHistoryLimit : limit;
        var records = new List<FinalizedWindowRecord>();

        try
        {
            await using var conn = new NpgsqlConnection(BuildConnectionString());
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT symbol_id, window_start, window_end, ema_38, ema_100, last_price, buy_signal, sell_signal " +
                "FROM trading_windows WHERE symbol_id = @sym ORDER BY window_start DESC LIMIT @lim;", conn);

            cmd.Parameters.AddWithValue("@sym", symbol);
            cmd.Parameters.AddWithValue("@lim", effectiveLimit);

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

        return records;
    }

    public async Task<IReadOnlyList<string>> SearchSymbolsAsync(string? query, int limit, CancellationToken ct = default)
    {
        int effectiveLimit = limit <= 0 ? Constants.DefaultSymbolSearchLimit : limit;
        var dbSymbols = new List<string>();
        string trimmed = query?.Trim() ?? string.Empty;

        try
        {
            await using var conn = new NpgsqlConnection(BuildConnectionString());
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand(
                "SELECT DISTINCT symbol_id FROM trading_windows WHERE (@q = '' OR symbol_id ILIKE @pattern) ORDER BY symbol_id LIMIT @lim;",
                conn);

            cmd.Parameters.AddWithValue("@q", trimmed);
            cmd.Parameters.AddWithValue("@pattern", $"%{trimmed}%");
            cmd.Parameters.AddWithValue("@lim", effectiveLimit);

            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                dbSymbols.Add(reader.GetString(0));
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not search symbols in TimescaleDB; using live cache");
        }

        return FilterSymbols(liveSymbols.Keys.Concat(dbSymbols), trimmed, effectiveLimit);
    }

    public async Task TruncateTradingWindowsAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(BuildConnectionString());
        await conn.OpenAsync(ct);

        await using var cmd = new NpgsqlCommand("TRUNCATE TABLE trading_windows;", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private string BuildConnectionString()
    {
        var configured = configuration.GetConnectionString("TimescaleDb");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        var host = Environment.GetEnvironmentVariable("POSTGRES_HOST") ?? configuration["POSTGRES_HOST"] ?? "localhost";
        var port = Environment.GetEnvironmentVariable("POSTGRES_PORT") ?? configuration["POSTGRES_PORT"] ?? "5432";
        var db = Environment.GetEnvironmentVariable("POSTGRES_DB") ?? configuration["POSTGRES_DB"] ?? "trading_db";
        var user = Environment.GetEnvironmentVariable("POSTGRES_USER") ?? configuration["POSTGRES_USER"] ?? "postgres";
        var password = Environment.GetEnvironmentVariable("POSTGRES_PASSWORD") ?? configuration["POSTGRES_PASSWORD"] ?? "postgres_password";

        return $"Host={host};Port={port};Database={db};Username={user};Password={password};Timeout=5;Command Timeout=10";
    }
}
