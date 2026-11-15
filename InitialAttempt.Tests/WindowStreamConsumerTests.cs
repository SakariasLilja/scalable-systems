using System.Threading.Channels;
using InitialAttempt.Models;
using InitialAttempt.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InitialAttempt.Tests;

public class WindowStreamConsumerTests
{
    private class FakeSimulationEngine : ISimulationEngine
    {
        public readonly Channel<MinimalTradingEntry> InternalChannel = System.Threading.Channels.Channel.CreateUnbounded<MinimalTradingEntry>();

        public Task StartSimulation(string filePath, double speedFactor, CancellationToken ct = default) => Task.CompletedTask;
        public void PauseSimulation() { }
        public void ResumeSimulation() { }
        public void SetSpeedFactor(double speedFactor) { }
        public SimulationStatus GetStatus() => new(SimulationState.Running, 1.0, 0, null, TimeSpan.Zero);
        public ChannelReader<MinimalTradingEntry> GetEventChannel() => InternalChannel.Reader;
    }

    [Fact]
    public async Task ConsumeStream_EmitsFinalizedRecords()
    {
        var fakeEngine = new FakeSimulationEngine();
        var processor = new WindowProcessor();
        var consumer = new WindowStreamConsumer(fakeEngine, processor, NullLogger<WindowStreamConsumer>.Instance);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var consumerTask = consumer.StartAsync(cts.Token);

        var date = new DateOnly(2021, 11, 13);
        await fakeEngine.InternalChannel.Writer.WriteAsync(new MinimalTradingEntry
        {
            Id = "TEST.SYM",
            SecType = SecurityType.E,
            Last = 100m,
            TradingDate = date,
            TradingTime = new TimeOnly(1, 0, 0)
        });

        await fakeEngine.InternalChannel.Writer.WriteAsync(new MinimalTradingEntry
        {
            Id = "TEST.SYM",
            SecType = SecurityType.E,
            Last = 105m,
            TradingDate = date,
            TradingTime = new TimeOnly(1, 5, 0)
        });

        var reader = consumer.GetFinalizedChannel();
        var record = await reader.ReadAsync(cts.Token);

        Assert.NotNull(record);
        Assert.Equal("TEST.SYM", record.SymbolId);
        Assert.Equal(100.0, record.LastPrice);

        await consumer.StopAsync(CancellationToken.None);
    }
}
