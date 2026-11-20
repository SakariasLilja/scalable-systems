using System.Text.Json;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Trading.Shared.Models;
using Trading.Shared.Utils;

namespace Trading.Worker.Services;

public interface IWindowPublisher
{
    Task PublishWindowAsync(FinalizedWindowRecord record, CancellationToken ct = default);
}

public class RedisWindowPublisher(
    IConnectionMultiplexer redis,
    ILogger<RedisWindowPublisher> logger
) : IWindowPublisher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private static readonly RedisChannel WindowsChannel = RedisChannel.Literal(Constants.RedisWindowsChannel);

    public async Task PublishWindowAsync(FinalizedWindowRecord record, CancellationToken ct = default)
    {
        try
        {
            var payload = JsonSerializer.Serialize(record, JsonOptions);
            await redis.GetSubscriber().PublishAsync(WindowsChannel, payload);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish finalized window for {SymbolId} to Redis", record.SymbolId);
        }
    }
}
