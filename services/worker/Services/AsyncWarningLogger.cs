using System.Threading.Channels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trading.Shared.Utils;

namespace Trading.Worker.Services;

public interface IAsyncWarningLogger
{
    void EnqueueWarning(string message);
    long DroppedCount { get; }
}

public class AsyncWarningLogger : BackgroundService, IAsyncWarningLogger
{
    private readonly ILogger<AsyncWarningLogger> logger;
    private readonly Channel<string> channel;
    private long droppedCount;
    private long droppedSinceLastLog;

    [ActivatorUtilitiesConstructor]
    public AsyncWarningLogger(ILogger<AsyncWarningLogger> logger)
        : this(logger, Constants.WarningLogChannelCapacity)
    {
    }

    public AsyncWarningLogger(ILogger<AsyncWarningLogger> logger, int capacity)
    {
        this.logger = logger;
        channel = Channel.CreateBounded<string>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    }

    public long DroppedCount => Interlocked.Read(ref droppedCount);

    public void EnqueueWarning(string message)
    {
        if (!channel.Writer.TryWrite(message))
        {
            Interlocked.Increment(ref droppedCount);
            Interlocked.Increment(ref droppedSinceLastLog);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var message in channel.Reader.ReadAllAsync(stoppingToken))
        {
            logger.LogWarning("{WarningMessage}", message);

            long dropped = Interlocked.Exchange(ref droppedSinceLastLog, 0);
            if (dropped > 0)
            {
                logger.LogWarning("Async warning logger dropped {DroppedCount} warnings under backpressure", dropped);
            }
        }
    }
}
