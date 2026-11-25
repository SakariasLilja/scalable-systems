namespace Trading.Shared.Utils;

public static class Constants
{
    public const int WindowSizeMs = 300000;
    public const int WindowDurationMinutes = 5;
    public const int WindowStartHour = 0;

    public const int EmaShortPeriod = 38;
    public const int EmaLongPeriod = 100;
    public const double EmaShortAlpha = 2.0 / (1.0 + 38.0);
    public const double EmaLongAlpha = 2.0 / (1.0 + 100.0);

    public const string RedisWindowsChannel = "ema:windows";
    public const string RedisResetChannel = "system:reset";
    public const int RedisSubscribeRetryDelayMs = 2000;

    public const int IngestChannelCapacity = 100000;
    public const int DbWriterChannelCapacity = 50000;
    public const int WarningLogChannelCapacity = 1024;

    public const int DbBatchFlushIntervalMs = 5000;
    public const int DbBatchMaxCapacity = 1000;
    public const int DbBatchMaxBufferSize = 5000;
    public const int DbBatchRetryDelayMs = 2000;
    public const int DbErrorBackoffMs = 1000;

    public const string SubscriptionHidden = "hidden";
    public const string SubscriptionTracked = "tracked";
    public const string SubscriptionActive = "active";

    public const int DefaultSymbolSearchLimit = 20;
    public const int DefaultMarketSymbolsLimit = 5000;
    public const int MarketDbCacheTtlSeconds = 10;
    public const int DefaultHistoryLimit = 50;
    public const int DefaultAdvisoriesLimit = 100;
    public const int MaxAdvisoriesLimit = 500;
    public const int SseBatchIntervalMs = 200;

    public const int ConsistentHashVirtualNodes = 128;
    public const int DefaultRedisCacheTtlSeconds = 120;
    public const string RedisDbCacheVersionKey = "dbcache:ver";
    public const string RedisDbCachePrefix = "dbcache";
}
