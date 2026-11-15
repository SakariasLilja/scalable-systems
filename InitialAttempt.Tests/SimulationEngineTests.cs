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
    public async Task StartSimulation_CompletesChannel_WhenPlaybackExhausts()
    {
        var records = new List<MinimalTradingEntry>
        {
            new() { Id = "S1", Last = 10m, TradingDate = new DateOnly(2021, 11, 13), TradingTime = new TimeOnly(0, 0, 0) }
        };
        var dummyService = new DummyCsvService(records);
        var engine = new SimulationEngine(dummyService);

        await engine.StartSimulation("dummy.csv", 10000.0);
        var channel = engine.GetEventChannel();

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var readSuccess = await channel.WaitToReadAsync(cts.Token);
        Assert.True(readSuccess);
        Assert.True(channel.TryRead(out _));

        await channel.Completion.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.True(channel.Completion.IsCompleted);
    }
}
