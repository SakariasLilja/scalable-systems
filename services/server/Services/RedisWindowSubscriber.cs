using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Trading.Shared.Models;
using Trading.Shared.Utils;

namespace Trading.Server.Services;

public interface IRedisResetPublisher
{
    Task PublishResetAsync(CancellationToken ct = default);
}

public class RedisResetPublisher(
    IConnectionMultiplexer redis,
    ILogger<RedisResetPublisher> logger
) : IRedisResetPublisher
{
    private static readonly RedisChannel ResetChannel = RedisChannel.Literal(Constants.RedisResetChannel);

    public async Task PublishResetAsync(CancellationToken ct = default)
    {
        try
        {
            await redis.GetSubscriber().PublishAsync(ResetChannel, "reset");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish system reset event to Redis");
        }
    }
}

public class RedisWindowSubscriber : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly RedisChannel WindowsChannel = RedisChannel.Literal(Constants.RedisWindowsChannel);
    private static readonly RedisChannel ResetChannel = RedisChannel.Literal(Constants.RedisResetChannel);

    private readonly IConnectionMultiplexer redis;
    private readonly ISseService sseService;
    private readonly IDatabaseQueryService dbQueryService;
    private readonly ILogger<RedisWindowSubscriber> logger;
    private readonly int retryDelayMs;
    private readonly SemaphoreSlim windowsSubLock = new(1, 1);
    private bool windowsSubscribed;

    [ActivatorUtilitiesConstructor]
    public RedisWindowSubscriber(
        IConnectionMultiplexer redis,
        ISseService sseService,
        IDatabaseQueryService dbQueryService,
        ILogger<RedisWindowSubscriber> logger)
        : this(redis, sseService, dbQueryService, logger, Constants.RedisSubscribeRetryDelayMs)
    {
    }

    public RedisWindowSubscriber(
        IConnectionMultiplexer redis,
        ISseService sseService,
        IDatabaseQueryService dbQueryService,
        ILogger<RedisWindowSubscriber> logger,
        int retryDelayMs)
    {
        this.redis = redis;
        this.sseService = sseService;
        this.dbQueryService = dbQueryService;
        this.logger = logger;
        this.retryDelayMs = retryDelayMs;
    }

    private async Task SyncWindowsSubscriptionAsync(ISubscriber subscriber, CancellationToken ct)
    {
        await windowsSubLock.WaitAsync(ct);
        try
        {
            bool shouldSubscribe = sseService.ActiveConnectionCount > 0;
            if (shouldSubscribe && !windowsSubscribed)
            {
                await subscriber.SubscribeAsync(WindowsChannel, async (_, value) =>
                {
                    if (value.IsNullOrEmpty || sseService.ActiveConnectionCount == 0)
                    {
                        return;
                    }

                    try
                    {
                        var record = JsonSerializer.Deserialize<FinalizedWindowRecord>(value.ToString(), JsonOptions);
                        if (record is null)
                        {
                            return;
                        }

                        dbQueryService.RecordLiveSymbol(record.SymbolId, record.WindowEnd);
                        await sseService.BroadcastWindowUpdate(record);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to process Redis finalized window message");
                    }
                });
                windowsSubscribed = true;
            }
            else if (!shouldSubscribe && windowsSubscribed)
            {
                await subscriber.UnsubscribeAsync(WindowsChannel);
                windowsSubscribed = false;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to toggle Redis windows subscription");
        }
        finally
        {
            windowsSubLock.Release();
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();
        bool subscribed = false;

        while (!stoppingToken.IsCancellationRequested && !subscribed)
        {
            try
            {
                await subscriber.SubscribeAsync(ResetChannel, async (_, _) =>
                {
                    try
                    {
                        dbQueryService.ClearLiveSymbols();
                        await sseService.BroadcastEvent("reset", new { cleared = true, time = DateTime.UtcNow });
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Failed to process Redis system reset message");
                    }
                });

                subscribed = true;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Redis subscription failed; retrying in {DelayMs}ms", retryDelayMs);
                try
                {
                    await subscriber.UnsubscribeAllAsync();
                }
                catch
                {
                }

                try
                {
                    await Task.Delay(retryDelayMs, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        if (!subscribed)
        {
            return;
        }

        Action<int> onCountChanged = count => _ = SyncWindowsSubscriptionAsync(subscriber, stoppingToken);
        sseService.ActiveConnectionCountChanged += onCountChanged;
        await SyncWindowsSubscriptionAsync(subscriber, stoppingToken);

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            sseService.ActiveConnectionCountChanged -= onCountChanged;
            try
            {
                await subscriber.UnsubscribeAllAsync();
            }
            catch
            {
            }
        }
    }
}
