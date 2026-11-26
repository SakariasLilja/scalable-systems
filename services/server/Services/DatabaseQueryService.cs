using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;
using Trading.Shared.Models;
using Trading.Shared.Utils;

namespace Trading.Server.Services;

public interface IDatabaseQueryService
{
    Task<IReadOnlyList<FinalizedWindowRecord>> GetHistoryAsync(string symbol, int limit, DateTime? before = null, CancellationToken ct = default);
    Task<IReadOnlyList<AdvisoryRecord>> GetRecentAdvisoriesAsync(int limit, CancellationToken ct = default);
    Task<(int KnownSymbols, DateTime? LatestSimulationTime)> GetStatusSummaryAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> SearchSymbolsAsync(string? query, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetMarketsAsync(CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetSymbolsByMarketAsync(string market, string? query, int limit, CancellationToken ct = default);
    Task TruncateTradingWindowsAsync(CancellationToken ct = default);
    void RecordLiveSymbol(string symbolId, DateTime windowEnd);
    void ClearLiveSymbols();
    int LiveSymbolCount { get; }
    DateTime? LatestSimulationTime { get; }
}

public class DatabaseQueryService : IDatabaseQueryService
{
    private readonly IConfiguration configuration;
    private readonly ILogger<DatabaseQueryService> logger;
    private readonly IConnectionMultiplexer? redis;
    private readonly DbShardRouter shardRouter;
    private readonly int cacheTtlSeconds;

    private readonly ConcurrentDictionary<string, byte> liveSymbols = new(StringComparer.OrdinalIgnoreCase);
    private readonly object timeLock = new();
    private readonly object marketCacheLock = new();
    private readonly object statusCacheLock = new();
    private DateTime? latestSimulationTime;
    private int cachedDbSymbolCount;
    private DateTime? cachedDbLatestTime;
    private DateTime cachedDbStatusAtUtc = DateTime.MinValue;
    private IReadOnlyList<string> cachedDbMarkets = Array.Empty<string>();
    private DateTime cachedDbMarketsAtUtc = DateTime.MinValue;
    private readonly ConcurrentDictionary<string, (IReadOnlyList<string> Symbols, DateTime CachedAtUtc)> cachedDbMarketSymbols = new(StringComparer.OrdinalIgnoreCase);

    public DatabaseQueryService(
        IConfiguration configuration,
        ILogger<DatabaseQueryService> logger,
        IConnectionMultiplexer? redis = null
    )
    {
        this.configuration = configuration;
        this.logger = logger;
        this.redis = redis;
        shardRouter = BuildShardRouter();
        cacheTtlSeconds = ParseCacheTtlSeconds();
    }

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
        cachedDbMarketSymbols.Clear();
        lock (marketCacheLock)
        {
            cachedDbMarkets = Array.Empty<string>();
            cachedDbMarketsAtUtc = DateTime.MinValue;
        }
        lock (timeLock)
        {
            latestSimulationTime = null;
        }
        lock (statusCacheLock)
        {
            cachedDbSymbolCount = 0;
            cachedDbLatestTime = null;
            cachedDbStatusAtUtc = DateTime.MinValue;
        }
    }

    public async Task<IReadOnlyList<AdvisoryRecord>> GetRecentAdvisoriesAsync(int limit, CancellationToken ct = default)
    {
        int effectiveLimit = Math.Clamp(limit <= 0 ? Constants.DefaultAdvisoriesLimit : limit, 1, Constants.MaxAdvisoriesLimit);
        var cacheKey = await BuildCacheKeyAsync($"adv:{effectiveLimit}");
        var cached = await TryGetCacheAsync<List<AdvisoryRecord>>(cacheKey);
        if (cached is not null)
        {
            return cached;
        }

        var shardTasks = shardRouter.AllConnectionStrings.Select(async connStr =>
        {
            var list = new List<AdvisoryRecord>();
            try
            {
                await using var conn = new NpgsqlConnection(connStr);
                await conn.OpenAsync(ct);

                await using var cmd = new NpgsqlCommand(
                    "SELECT symbol_id, buy_signal, sell_signal, last_price, ema_38, ema_100, window_end " +
                    "FROM trading_windows WHERE buy_signal = TRUE OR sell_signal = TRUE " +
                    "ORDER BY window_end DESC LIMIT @lim;", conn);

                cmd.Parameters.AddWithValue("@lim", effectiveLimit);

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    bool buy = reader.GetBoolean(1);
                    bool sell = reader.GetBoolean(2);
                    string alertType = buy ? "BUY" : sell ? "SELL" : "NONE";
                    if (alertType == "NONE") continue;

                    list.Add(new AdvisoryRecord(
                        reader.GetString(0),
                        alertType,
                        reader.GetDouble(3),
                        reader.GetDouble(4),
                        reader.GetDouble(5),
                        reader.GetDateTime(6)
                    ));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not fetch recent advisories from TimescaleDB shard");
            }
            return list;
        });

        var results = await Task.WhenAll(shardTasks);
        var merged = results
            .SelectMany(x => x)
            .OrderByDescending(x => x.Time)
            .Take(effectiveLimit)
            .ToList();

        await TrySetCacheAsync(cacheKey, merged);

        return merged;
    }

    public async Task<(int KnownSymbols, DateTime? LatestSimulationTime)> GetStatusSummaryAsync(CancellationToken ct = default)
    {
        int liveCount = liveSymbols.Count;
        DateTime? liveTime = LatestSimulationTime;

        int dbCount;
        DateTime? dbLatest;
        bool cacheFresh;
        lock (statusCacheLock)
        {
            cacheFresh = (DateTime.UtcNow - cachedDbStatusAtUtc).TotalSeconds < Constants.MarketDbCacheTtlSeconds;
            dbCount = cachedDbSymbolCount;
            dbLatest = cachedDbLatestTime;
        }

        if (!cacheFresh)
        {
            var shardTasks = shardRouter.AllConnectionStrings.Select(async connStr =>
            {
                try
                {
                    await using var conn = new NpgsqlConnection(connStr);
                    await conn.OpenAsync(ct);

                    await using var cmd = new NpgsqlCommand(
                        "SELECT COUNT(DISTINCT symbol_id), MAX(window_end) FROM trading_windows;", conn);

                    await using var reader = await cmd.ExecuteReaderAsync(ct);
                    if (await reader.ReadAsync(ct))
                    {
                        int c = reader.IsDBNull(0) ? 0 : (int)reader.GetInt64(0);
                        DateTime? t = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
                        return (c, t);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Could not fetch status summary from TimescaleDB shard");
                }
                return (0, (DateTime?)null);
            });

            var results = await Task.WhenAll(shardTasks);
            dbCount = results.Sum(r => r.Item1);
            dbLatest = results.Where(r => r.Item2.HasValue).Select(r => r.Item2).DefaultIfEmpty(null).Max();

            lock (statusCacheLock)
            {
                cachedDbSymbolCount = dbCount;
                cachedDbLatestTime = dbLatest;
                cachedDbStatusAtUtc = DateTime.UtcNow;
            }
        }

        int mergedCount = Math.Max(liveCount, dbCount);
        DateTime? mergedTime = liveTime.HasValue && dbLatest.HasValue
            ? (liveTime.Value >= dbLatest.Value ? liveTime : dbLatest)
            : (liveTime ?? dbLatest);

        return (mergedCount, mergedTime);
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

    public async Task<IReadOnlyList<FinalizedWindowRecord>> GetHistoryAsync(string symbol, int limit, DateTime? before = null, CancellationToken ct = default)
    {
        int effectiveLimit = limit <= 0 ? Constants.DefaultHistoryLimit : limit;
        string? cacheKey = null;
        if (!before.HasValue)
        {
            cacheKey = await BuildCacheKeyAsync($"hist:{symbol.Trim().ToUpperInvariant()}:{effectiveLimit}");
            var cached = await TryGetCacheAsync<List<FinalizedWindowRecord>>(cacheKey);
            if (cached is not null)
            {
                return cached;
            }
        }

        var records = new List<FinalizedWindowRecord>();

        try
        {
            var connStr = shardRouter.GetConnectionStringForSymbol(symbol);
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            string sql = before.HasValue
                ? "SELECT symbol_id, window_start, window_end, ema_38, ema_100, last_price, buy_signal, sell_signal " +
                  "FROM trading_windows WHERE symbol_id = @sym AND window_start < @before ORDER BY window_start DESC LIMIT @lim;"
                : "SELECT symbol_id, window_start, window_end, ema_38, ema_100, last_price, buy_signal, sell_signal " +
                  "FROM trading_windows WHERE symbol_id = @sym ORDER BY window_start DESC LIMIT @lim;";

            await using var cmd = new NpgsqlCommand(sql, conn);
            cmd.Parameters.AddWithValue("@sym", symbol);
            if (before.HasValue)
            {
                cmd.Parameters.AddWithValue("@before", DateTime.SpecifyKind(before.Value.ToUniversalTime(), DateTimeKind.Utc));
            }
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

        if (cacheKey is not null)
        {
            await TrySetCacheAsync(cacheKey, records);
        }

        return records;
    }

    public async Task<IReadOnlyList<string>> SearchSymbolsAsync(string? query, int limit, CancellationToken ct = default)
    {
        int effectiveLimit = limit <= 0 ? Constants.DefaultSymbolSearchLimit : limit;
        string trimmed = query?.Trim() ?? string.Empty;

        var cacheKey = await BuildCacheKeyAsync($"search:{trimmed.ToUpperInvariant()}:{effectiveLimit}");
        var cachedDb = await TryGetCacheAsync<List<string>>(cacheKey);
        if (cachedDb is not null)
        {
            return FilterSymbols(liveSymbols.Keys.Concat(cachedDb), trimmed, effectiveLimit);
        }

        var shardTasks = shardRouter.AllConnectionStrings.Select(async connStr =>
        {
            var list = new List<string>();
            try
            {
                await using var conn = new NpgsqlConnection(connStr);
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
                    list.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not search symbols in TimescaleDB shard; using live cache");
            }
            return list;
        });

        var dbSymbols = (await Task.WhenAll(shardTasks)).SelectMany(x => x).ToList();
        var mergedDb = FilterSymbols(dbSymbols, trimmed, effectiveLimit).ToList();
        await TrySetCacheAsync(cacheKey, mergedDb);
        return FilterSymbols(liveSymbols.Keys.Concat(mergedDb), trimmed, effectiveLimit);
    }

    public static string ExtractMarket(string symbolId)
    {
        if (string.IsNullOrWhiteSpace(symbolId))
        {
            return string.Empty;
        }
        int dot = symbolId.LastIndexOf('.');
        if (dot < 0 || dot + 1 >= symbolId.Length)
        {
            return string.Empty;
        }
        return symbolId[(dot + 1)..].ToUpperInvariant();
    }

    public static string EscapeLikePattern(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal);
    }

    public async Task<IReadOnlyList<string>> GetMarketsAsync(CancellationToken ct = default)
    {
        IReadOnlyList<string> dbMarkets;
        bool cacheFresh;
        lock (marketCacheLock)
        {
            cacheFresh = (DateTime.UtcNow - cachedDbMarketsAtUtc).TotalSeconds < Constants.MarketDbCacheTtlSeconds;
            dbMarkets = cachedDbMarkets;
        }

        if (!cacheFresh)
        {
            var cacheKey = await BuildCacheKeyAsync("markets");
            var redisMarkets = await TryGetCacheAsync<List<string>>(cacheKey);
            List<string> fetched;

            if (redisMarkets is not null)
            {
                fetched = redisMarkets;
            }
            else
            {
                var shardTasks = shardRouter.AllConnectionStrings.Select(async connStr =>
                {
                    var list = new List<string>();
                    try
                    {
                        await using var conn = new NpgsqlConnection(connStr);
                        await conn.OpenAsync(ct);

                        await using var cmd = new NpgsqlCommand(
                            "SELECT DISTINCT UPPER(SPLIT_PART(symbol_id, '.', ARRAY_LENGTH(STRING_TO_ARRAY(symbol_id, '.'), 1))) AS market " +
                            "FROM trading_windows WHERE POSITION('.' IN symbol_id) > 0 ORDER BY market;",
                            conn);

                        await using var reader = await cmd.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct))
                        {
                            var market = reader.GetString(0);
                            if (!string.IsNullOrWhiteSpace(market))
                            {
                                list.Add(market);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not fetch markets from TimescaleDB shard; using live cache");
                    }
                    return list;
                });

                fetched = (await Task.WhenAll(shardTasks))
                    .SelectMany(x => x)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                await TrySetCacheAsync(cacheKey, fetched);
            }

            lock (marketCacheLock)
            {
                cachedDbMarkets = fetched;
                cachedDbMarketsAtUtc = DateTime.UtcNow;
                dbMarkets = cachedDbMarkets;
            }
        }

        var liveMarkets = liveSymbols.Keys
            .Select(ExtractMarket)
            .Where(m => !string.IsNullOrWhiteSpace(m));

        return liveMarkets
            .Concat(dbMarkets)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(m => m, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> GetSymbolsByMarketAsync(string market, string? query, int limit, CancellationToken ct = default)
    {
        string cleanMarket = market?.Trim().ToUpperInvariant() ?? string.Empty;
        if (string.IsNullOrEmpty(cleanMarket))
        {
            return Array.Empty<string>();
        }

        int effectiveLimit = limit <= 0 ? Constants.DefaultMarketSymbolsLimit : limit;
        string trimmed = query?.Trim() ?? string.Empty;

        IReadOnlyList<string> dbSymbols;
        if (string.IsNullOrEmpty(trimmed) &&
            cachedDbMarketSymbols.TryGetValue(cleanMarket, out var cached) &&
            (DateTime.UtcNow - cached.CachedAtUtc).TotalSeconds < Constants.MarketDbCacheTtlSeconds)
        {
            dbSymbols = cached.Symbols;
        }
        else
        {
            string? cacheKey = string.IsNullOrEmpty(trimmed)
                ? await BuildCacheKeyAsync($"mkt:{cleanMarket}:{effectiveLimit}")
                : null;
            var redisSymbols = cacheKey is not null ? await TryGetCacheAsync<List<string>>(cacheKey) : null;
            List<string> fetched;

            if (redisSymbols is not null)
            {
                fetched = redisSymbols;
            }
            else
            {
                var shardTasks = shardRouter.AllConnectionStrings.Select(async connStr =>
                {
                    var list = new List<string>();
                    try
                    {
                        await using var conn = new NpgsqlConnection(connStr);
                        await conn.OpenAsync(ct);

                        await using var cmd = new NpgsqlCommand(
                            "SELECT DISTINCT symbol_id FROM trading_windows " +
                            "WHERE symbol_id ILIKE @marketPattern ESCAPE '\\' AND (@q = '' OR symbol_id ILIKE @queryPattern ESCAPE '\\') " +
                            "ORDER BY symbol_id LIMIT @lim;",
                            conn);

                        cmd.Parameters.AddWithValue("@marketPattern", $"%.{EscapeLikePattern(cleanMarket)}");
                        cmd.Parameters.AddWithValue("@q", trimmed);
                        cmd.Parameters.AddWithValue("@queryPattern", $"%{EscapeLikePattern(trimmed)}%");
                        cmd.Parameters.AddWithValue("@lim", effectiveLimit);

                        await using var reader = await cmd.ExecuteReaderAsync(ct);
                        while (await reader.ReadAsync(ct))
                        {
                            var sym = reader.GetString(0);
                            if (string.Equals(ExtractMarket(sym), cleanMarket, StringComparison.OrdinalIgnoreCase))
                            {
                                list.Add(sym);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Could not fetch symbols for market {Market} from TimescaleDB shard; using live cache", cleanMarket);
                    }
                    return list;
                });

                fetched = (await Task.WhenAll(shardTasks)).SelectMany(x => x).ToList();
                if (cacheKey is not null)
                {
                    await TrySetCacheAsync(cacheKey, FilterSymbols(fetched, string.Empty, effectiveLimit).ToList());
                }
            }

            if (string.IsNullOrEmpty(trimmed))
            {
                cachedDbMarketSymbols[cleanMarket] = (fetched, DateTime.UtcNow);
            }
            dbSymbols = fetched;
        }

        var liveMatching = liveSymbols.Keys
            .Where(s => string.Equals(ExtractMarket(s), cleanMarket, StringComparison.OrdinalIgnoreCase));

        return FilterSymbols(liveMatching.Concat(dbSymbols), trimmed, effectiveLimit);
    }

    public async Task TruncateTradingWindowsAsync(CancellationToken ct = default)
    {
        var tasks = shardRouter.AllConnectionStrings.Select(async connStr =>
        {
            await using var conn = new NpgsqlConnection(connStr);
            await conn.OpenAsync(ct);

            await using var cmd = new NpgsqlCommand("TRUNCATE TABLE trading_windows;", conn);
            await cmd.ExecuteNonQueryAsync(ct);
        });

        await Task.WhenAll(tasks);
        await BumpCacheVersionAsync();
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

    private int ParseCacheTtlSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("REDIS_CACHE_TTL_SECONDS") ?? configuration["REDIS_CACHE_TTL_SECONDS"];
        return int.TryParse(raw, out int parsed) ? parsed : Constants.DefaultRedisCacheTtlSeconds;
    }

    private async Task<string?> BuildCacheKeyAsync(string suffix)
    {
        if (cacheTtlSeconds <= 0 || redis is null)
        {
            return null;
        }

        try
        {
            var db = redis.GetDatabase();
            var ver = await db.StringGetAsync(Constants.RedisDbCacheVersionKey);
            var verStr = ver.HasValue ? ver.ToString() : "0";
            return $"{Constants.RedisDbCachePrefix}:v{verStr}:{suffix}";
        }
        catch
        {
            return null;
        }
    }

    private async Task<T?> TryGetCacheAsync<T>(string? key) where T : class
    {
        if (string.IsNullOrEmpty(key) || cacheTtlSeconds <= 0 || redis is null)
        {
            return null;
        }

        try
        {
            var raw = await redis.GetDatabase().StringGetAsync(key);
            if (!raw.HasValue)
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(raw.ToString());
        }
        catch
        {
            return null;
        }
    }

    private async Task TrySetCacheAsync<T>(string? key, T value)
    {
        if (string.IsNullOrEmpty(key) || cacheTtlSeconds <= 0 || redis is null)
        {
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(value);
            await redis.GetDatabase().StringSetAsync(key, json, TimeSpan.FromSeconds(cacheTtlSeconds));
        }
        catch
        {
        }
    }

    private async Task BumpCacheVersionAsync()
    {
        if (redis is null)
        {
            return;
        }

        try
        {
            await redis.GetDatabase().StringIncrementAsync(Constants.RedisDbCacheVersionKey);
        }
        catch
        {
        }
    }
}
