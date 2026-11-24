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
    Task<int> FlushAsync(CancellationToken ct = default);
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
    private readonly record struct IngestWorkItem(MinimalTradingEntry? Tick, TaskCompletionSource<int>? FlushTcs);

    private readonly Channel<IngestWorkItem> channel = Channel.CreateBounded<IngestWorkItem>(
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
        await channel.Writer.WriteAsync(new IngestWorkItem(tick, null), ct);
        Interlocked.Increment(ref ingestedCount);
    }

    public async Task<int> FlushAsync(CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        await channel.Writer.WriteAsync(new IngestWorkItem(null, tcs), ct);
        return await tcs.Task.WaitAsync(ct);
    }

    public void Reset()
    {
        CancellationTokenSource oldCts;
        lock (processLock)
        {
            while (channel.Reader.TryRead(out var item))
            {
                item.FlushTcs?.TrySetResult(0);
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
        await foreach (var item in channel.Reader.ReadAllAsync(stoppingToken))
        {
            if (item.FlushTcs is not null)
            {
                try
                {
                    IReadOnlyList<FinalizedWindowRecord> flushed;
                    CancellationToken publishResetToken;
                    lock (processLock)
                    {
                        flushed = windowProcessor.FlushAll();
                        foreach (var record in flushed)
                        {
                            dbBatchWriter.Enqueue(record);
                        }
                        publishResetToken = resetCts.Token;
                    }

                    if (flushed.Count > 0)
                    {
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, publishResetToken);
                        await Task.WhenAll(flushed.Select(r => windowPublisher.PublishWindowAsync(r, linkedCts.Token)));
                    }

                    logger.LogInformation("Flushed {Count} open windows at end of stream", flushed.Count);
                    item.FlushTcs.TrySetResult(flushed.Count);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    item.FlushTcs.TrySetCanceled(stoppingToken);
                    break;
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Unexpected error flushing open windows");
                    item.FlushTcs.TrySetException(ex);
                }
                continue;
            }

            var tick = item.Tick!;
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
