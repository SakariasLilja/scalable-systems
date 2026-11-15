using System.Diagnostics;
using System.Threading.Channels;
using InitialAttempt.Models;

namespace InitialAttempt.Services;

public class SimulationEngine : ISimulationEngine
{
    private readonly IReadCsvService readCsvService;
    private readonly Channel<MinimalTradingEntry> channel;
    private readonly object stateLock = new();
    private readonly ManualResetEventSlim pauseEvent = new(true);

    private SimulationState state = SimulationState.Stopped;
    private double speedFactor = 1.0;
    private long processedCount = 0;
    private DateTime? currentSimulationTime = null;
    private Stopwatch wallStopwatch = new();
    private CancellationTokenSource? playbackCts;

    public SimulationEngine(IReadCsvService readCsvService)
    {
        this.readCsvService = readCsvService;
        this.channel = Channel.CreateBounded<MinimalTradingEntry>(new BoundedChannelOptions(100000)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true
        });
    }

    public static long calculateDelay(long deltaMs, double speedFactor)
    {
        if (speedFactor <= 0.0 || deltaMs <= 0)
        {
            return 0;
        }

        return (long)(deltaMs / speedFactor);
    }

    public Task startSimulation(string filePath, double speedFactor, CancellationToken ct = default)
    {
        lock (stateLock)
        {
            if (state == SimulationState.Running)
            {
                return Task.CompletedTask;
            }

            this.speedFactor = speedFactor;
            this.processedCount = 0;
            this.currentSimulationTime = null;
            this.state = SimulationState.Running;
            this.playbackCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            this.wallStopwatch.Restart();
            this.pauseEvent.Set();
        }

        return Task.Run(() => runPlaybackLoop(filePath, playbackCts.Token), CancellationToken.None);
    }

    public void pauseSimulation()
    {
        lock (stateLock)
        {
            if (state == SimulationState.Running)
            {
                state = SimulationState.Paused;
                pauseEvent.Reset();
                wallStopwatch.Stop();
            }
        }
    }

    public void resumeSimulation()
    {
        lock (stateLock)
        {
            if (state == SimulationState.Paused)
            {
                state = SimulationState.Running;
                pauseEvent.Set();
                wallStopwatch.Start();
            }
        }
    }

    public void setSpeedFactor(double speedFactor)
    {
        lock (stateLock)
        {
            this.speedFactor = speedFactor;
        }
    }

    public SimulationStatus getStatus()
    {
        lock (stateLock)
        {
            return new SimulationStatus(
                state,
                speedFactor,
                Interlocked.Read(ref processedCount),
                currentSimulationTime,
                wallStopwatch.Elapsed
            );
        }
    }

    public ChannelReader<MinimalTradingEntry> getEventChannel()
    {
        return channel.Reader;
    }

    private async Task runPlaybackLoop(string filePath, CancellationToken ct)
    {
        DateTime? previousEventTime = null;

        try
        {
            foreach (var record in readCsvService.streamTradeEvents(filePath, ct))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                pauseEvent.Wait(ct);

                DateTime currentEventTime;
                if (record.TradingDate.HasValue && record.TradingTime.HasValue)
                {
                    currentEventTime = record.TradingDate.Value.ToDateTime(record.TradingTime.Value);
                }
                else
                {
                    currentEventTime = DateTime.UtcNow;
                }

                if (previousEventTime.HasValue)
                {
                    var deltaMs = (long)(currentEventTime - previousEventTime.Value).TotalMilliseconds;
                    double currentSpeed;
                    lock (stateLock)
                    {
                        currentSpeed = speedFactor;
                    }

                    var delayMs = calculateDelay(deltaMs, currentSpeed);
                    if (delayMs > 0)
                    {
                        await Task.Delay((int)delayMs, ct);
                    }
                }

                previousEventTime = currentEventTime;
                lock (stateLock)
                {
                    currentSimulationTime = currentEventTime;
                }

                await channel.Writer.WriteAsync(record, ct);
                Interlocked.Increment(ref processedCount);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (stateLock)
            {
                state = SimulationState.Stopped;
                wallStopwatch.Stop();
            }
        }
    }
}
