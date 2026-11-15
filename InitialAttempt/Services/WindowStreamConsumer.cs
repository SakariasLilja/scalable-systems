using System.Threading.Channels;
using InitialAttempt.Models;
using Microsoft.Extensions.Hosting;

namespace InitialAttempt.Services;

public class WindowStreamConsumer(
    ISimulationEngine simulationEngine,
    IWindowProcessor windowProcessor,
    ILogger<WindowStreamConsumer> logger
) : BackgroundService, IWindowStreamConsumer
{
    private readonly Channel<FinalizedWindowRecord> finalizedChannel = Channel.CreateBounded<FinalizedWindowRecord>(new BoundedChannelOptions(50000)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleWriter = true
    });

    public event Action<FinalizedWindowRecord>? OnWindowFinalized;

    public ChannelReader<FinalizedWindowRecord> GetFinalizedChannel()
    {
        return finalizedChannel.Reader;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tickReader = simulationEngine.GetEventChannel();

        try
        {
            while (await tickReader.WaitToReadAsync(stoppingToken))
            {
                while (tickReader.TryRead(out var tick))
                {
                    var finalized = windowProcessor.ProcessTick(tick);
                    if (finalized != null)
                    {
                        await finalizedChannel.Writer.WriteAsync(finalized, stoppingToken);

                        try
                        {
                            OnWindowFinalized?.Invoke(finalized);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error executing window finalized event callback");
                        }

                        if (finalized.BuySignal || finalized.SellSignal)
                        {
                            var signal = finalized.BuySignal ? "BUY" : "SELL";
                            logger.LogInformation("[{Signal}] Advisory for {Symbol} at {Time}. Close: {Price}, EMA38: {Ema38:F4}, EMA100: {Ema100:F4}",
                                signal, finalized.SymbolId, finalized.WindowEnd, finalized.LastPrice, finalized.Ema38, finalized.Ema100);
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            finalizedChannel.Writer.TryComplete();
        }
    }
}
