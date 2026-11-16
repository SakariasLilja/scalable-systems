using System.Diagnostics;
using System.Threading.Channels;
using InitialAttempt.Models;
using InitialAttempt.Utils;

namespace InitialAttempt.Services;

public class SimulationEngine : ISimulationEngine
{
    private readonly IReadCsvService readCsvService;
    private Channel<MinimalTradingEntry> channel;
    private readonly object stateLock = new();
    private readonly ManualResetEventSlim pauseEvent = new(true);

    private SimulationState state = SimulationState.Stopped;
    private double speedFactor = Constants.DefaultSimulationSpeed;
    private long processedCount = 0;
    private DateTime? currentSimulationTime = null;
    private Stopwatch wallStopwatch = new();
    private CancellationTokenSource? playbackCts;

    public SimulationEngine(IReadCsvService readCsvService)
    {
        this.readCsvService = readCsvService;
        this.channel = CreateChannel();
    }

    private static Channel<MinimalTradingEntry> CreateChannel() =>
        Channel.CreateBounded<MinimalTradingEntry>(new BoundedChannelOptions(Constants.SimulationChannelCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleWriter = true
        });

    public static long CalculateDelay(long deltaMs, double speedFactor)
    {
        if (speedFactor <= 0.0 || deltaMs <= 0)
        {
            return 0;
        }

        double scaled = deltaMs / speedFactor;
        if (scaled > Constants.MaxSimulationDelayMs)
        {
            return Constants.MaxSimulationDelayMs;
        }

        return (long)scaled;
    }

    public Task StartSimulation(string filePath, double speedFactor, CancellationToken ct = default)
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

        _ = Task.Run(() => RunPlaybackLoop(filePath, playbackCts.Token), CancellationToken.None);
        return Task.CompletedTask;
    }

    public void PauseSimulation()
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

    public void ResumeSimulation()
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

    public void SetSpeedFactor(double speedFactor)
    {
        lock (stateLock)
        {
            this.speedFactor = speedFactor;
        }
    }

    public SimulationStatus GetStatus()
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

    public ChannelReader<MinimalTradingEntry> GetEventChannel()
    {
        return channel.Reader;
    }

    private async Task RunPlaybackLoop(string filePath, CancellationToken ct)
    {
        DateTime? previousEventTime = null;

        try
        {
            foreach (var record in readCsvService.StreamTradeEvents(filePath, ct))
            {
                if (ct.IsCancellationRequested)
                {
                    break;
                }

                pauseEvent.Wait(ct);

                DateTime currentEventTime;
                if (record.EffectiveDate.HasValue && record.EffectiveTime.HasValue)
                {
                    currentEventTime = record.EffectiveDate.Value.ToDateTime(record.EffectiveTime.Value);
                }
                else
                {
                    currentEventTime = previousEventTime ?? DateTime.UtcNow;
                }

                if (previousEventTime.HasValue)
                {
                    var deltaMs = (long)(currentEventTime - previousEventTime.Value).TotalMilliseconds;
                    double currentSpeed;
                    lock (stateLock)
                    {
                        currentSpeed = speedFactor;
                    }

                    var delayMs = CalculateDelay(deltaMs, currentSpeed);
                    if (delayMs > 0)
                    {
                        var clamped = (int)Math.Min(delayMs, int.MaxValue);
                        await Task.Delay(clamped, ct);
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
