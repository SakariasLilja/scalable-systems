using System.Text.Json;
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

public class RedisWindowSubscriber(
    IConnectionMultiplexer redis,
    ISseService sseService,
    IDatabaseQueryService dbQueryService,
    ILogger<RedisWindowSubscriber> logger
) : BackgroundService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly RedisChannel WindowsChannel = RedisChannel.Literal(Constants.RedisWindowsChannel);
    private static readonly RedisChannel ResetChannel = RedisChannel.Literal(Constants.RedisResetChannel);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var subscriber = redis.GetSubscriber();

        await subscriber.SubscribeAsync(WindowsChannel, async (_, value) =>
        {
            if (value.IsNullOrEmpty)
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

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
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
