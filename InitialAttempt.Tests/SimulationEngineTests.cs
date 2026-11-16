using InitialAttempt.Models;
using InitialAttempt.Services;
using Xunit;

namespace InitialAttempt.Tests;

public class SimulationEngineTests
{
    private class DummyCsvService(IEnumerable<MinimalTradingEntry> entries) : IReadCsvService
    {
        public IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken) => StreamTradeEvents(filePath, cancellationToken);

        public IEnumerable<MinimalTradingEntry> StreamTradeEvents(string filePath, CancellationToken cancellationToken)
        {
            foreach (var e in entries)
            {
                if (cancellationToken.IsCancellationRequested) yield break;
                yield return e;
            }
        }
    }

    [Theory]
    [InlineData(1000, 1.0, 1000)]
    [InlineData(1000, 2.0, 500)]
    [InlineData(1000, 10.0, 100)]
    [InlineData(1000, 0.0, 0)]
    public void CalculateDelay_ComputesCorrectScaledDelay(long deltaMs, double speedFactor, long expectedDelayMs)
    {
        var delay = SimulationEngine.CalculateDelay(deltaMs, speedFactor);
        Assert.Equal(expectedDelayMs, delay);
    }

    [Fact]
    public void CalculateDelay_WithHugeGap_CapsAtMaxSimulationDelay()
    {
        long fiveYearsMs = 5L * 365 * 24 * 3600 * 1000;
        var delay = SimulationEngine.CalculateDelay(fiveYearsMs, 1.0);
        Assert.Equal(InitialAttempt.Utils.Constants.MaxSimulationDelayMs, delay);
    }

    [Fact]
    public void CalculateDelay_WithZeroOrNegativeSpeed_ReturnsZero()
    {
        var delay = SimulationEngine.CalculateDelay(5000, 0.0);
        Assert.Equal(0, delay);

        var delayNegative = SimulationEngine.CalculateDelay(5000, -1.0);
        Assert.Equal(0, delayNegative);
    }

    [Fact]
    public async Task RunPlaybackLoop_WithMissingDates_DoesNotStall()
    {
        var records = new List<MinimalTradingEntry>
        {
            new() { Id = "S1", Last = 10m, Date = new DateOnly(2021, 11, 8), Time = new TimeOnly(1, 0, 0) },
            new() { Id = "S1", Last = 11m, Date = new DateOnly(2021, 11, 8), Time = new TimeOnly(1, 0, 1) },
            new() { Id = "S1", Last = 12m, TradingDate = new DateOnly(2021, 11, 8), TradingTime = new TimeOnly(1, 0, 2) },
            new() { Id = "S1", Last = 13m, Date = null, Time = null, TradingDate = null, TradingTime = null }
        };

        var dummyService = new DummyCsvService(records);
        var engine = new SimulationEngine(dummyService);

        var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await engine.StartSimulation("dummy.csv", 1000.0, cts.Token);

        await Task.Delay(200);
        var status = engine.GetStatus();

        Assert.Equal(4, status.ProcessedCount);
    }

    [Fact]
    public async Task StartSimulation_ReturnsImmediately_WithoutBlockingForPlayback()
    {
        var records = new List<MinimalTradingEntry>
        {
            new() { Id = "S1", Last = 10m, TradingDate = new DateOnly(2021, 11, 13), TradingTime = new TimeOnly(0, 0, 0) },
            new() { Id = "S1", Last = 11m, TradingDate = new DateOnly(2021, 11, 13), TradingTime = new TimeOnly(0, 1, 0) }
        };
        var dummyService = new DummyCsvService(records);
        var engine = new SimulationEngine(dummyService);

        var startTask = engine.StartSimulation("dummy.csv", 0.0001);
        var completedInTime = await Task.WhenAny(startTask, Task.Delay(500)) == startTask;

        Assert.True(completedInTime, "StartSimulation should return immediately rather than awaiting playback loop completion.");
        Assert.Equal(SimulationState.Running, engine.GetStatus().State);
    }

    [Fact]
    public async Task StartSimulation_CanBeRestarted_AndContinuesStreamingTicks()
    {
        var records = new List<MinimalTradingEntry>
        {
            new() { Id = "S1", Last = 10m, TradingDate = new DateOnly(2021, 11, 13), TradingTime = new TimeOnly(0, 0, 0) }
        };
        var dummyService = new DummyCsvService(records);
        var engine = new SimulationEngine(dummyService);

        // Run 1
        await engine.StartSimulation("dummy.csv", 10000.0);
        var channel = engine.GetEventChannel();

        using var cts1 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var readSuccess1 = await channel.WaitToReadAsync(cts1.Token);
        Assert.True(readSuccess1);
        Assert.True(channel.TryRead(out var readRecord1));
        Assert.Equal("S1", readRecord1.Id);

        // Wait until stopped
        while (engine.GetStatus().State == SimulationState.Running)
        {
            await Task.Delay(20);
        }

        // Run 2: Start again
        await engine.StartSimulation("dummy.csv", 10000.0);
        using var cts2 = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var readSuccess2 = await channel.WaitToReadAsync(cts2.Token);
        Assert.True(readSuccess2);
        Assert.True(channel.TryRead(out var readRecord2));
        Assert.Equal("S1", readRecord2.Id);
    }

    private class ThrowingCsvService(Exception exceptionToThrow) : IReadCsvService
    {
        public IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken) => StreamTradeEvents(filePath, cancellationToken);

        public IEnumerable<MinimalTradingEntry> StreamTradeEvents(string filePath, CancellationToken cancellationToken)
        {
            throw exceptionToThrow;
        }
    }

    [Fact]
    public async Task RunPlaybackLoop_WhenExceptionOccurs_SetsLastErrorAndStops()
    {
        var throwingService = new ThrowingCsvService(new InvalidOperationException("CSV disk error"));
        var engine = new SimulationEngine(throwingService);

        await engine.StartSimulation("error.csv", 1.0);

        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (engine.GetStatus().State == SimulationState.Running && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        var status = engine.GetStatus();
        Assert.Equal(SimulationState.Stopped, status.State);
        Assert.Equal("CSV disk error", status.LastError);
    }
}
