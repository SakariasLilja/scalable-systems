using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Trading.Shared.Models;
using Trading.Shared.Services;
using Trading.Shared.Utils;

namespace Trading.Worker.Services;

public interface ITickIngestionService
{
    ValueTask EnqueueAsync(MinimalTradingEntry tick, CancellationToken ct = default);
    void Reset();
    long IngestedCount { get; }
}

public class TickIngestionService(
    IWindowProcessor windowProcessor,
    IDbBatchWriter dbBatchWriter,
    IWindowPublisher windowPublisher,
    ILogger<TickIngestionService> logger
) : BackgroundService, ITickIngestionService
{
    private readonly Channel<MinimalTradingEntry> channel = Channel.CreateBounded<MinimalTradingEntry>(
        new BoundedChannelOptions(Constants.IngestChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly object processLock = new();
    private CancellationTokenSource resetCts = new();
    private long ingestedCount;

    public long IngestedCount => Interlocked.Read(ref ingestedCount);

    public async ValueTask EnqueueAsync(MinimalTradingEntry tick, CancellationToken ct = default)
    {
        await channel.Writer.WriteAsync(tick, ct);
        Interlocked.Increment(ref ingestedCount);
    }

    public void Reset()
    {
        CancellationTokenSource oldCts;
        lock (processLock)
        {
            while (channel.Reader.TryRead(out _))
            {
            }

            windowProcessor.Reset();
            dbBatchWriter.ClearBuffer();
            oldCts = resetCts;
            resetCts = new CancellationTokenSource();
            Interlocked.Exchange(ref ingestedCount, 0);
        }

        oldCts.Cancel();
        oldCts.Dispose();
        logger.LogInformation("Worker ingestion pipeline and EMA state reset");
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var tick in channel.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                FinalizedWindowRecord? finalized;
                CancellationToken publishResetToken = CancellationToken.None;
                lock (processLock)
                {
                    finalized = windowProcessor.ProcessTick(tick);
                    if (finalized is not null)
                    {
                        dbBatchWriter.Enqueue(finalized);
                        publishResetToken = resetCts.Token;
                    }
                }

                if (finalized is not null)
                {
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, publishResetToken);
                    await windowPublisher.PublishWindowAsync(finalized, linkedCts.Token);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unexpected error processing tick for {SymbolId}", tick.Id);
            }
        }
    }
}
