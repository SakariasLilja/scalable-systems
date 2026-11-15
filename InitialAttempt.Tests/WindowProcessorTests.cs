using InitialAttempt.Models;
using InitialAttempt.Services;
using Xunit;

namespace InitialAttempt.Tests;

public class WindowProcessorTests
{
    [Fact]
    public void ProcessTick_SameWindow_DoesNotFinalize()
    {
        var processor = new WindowProcessor();
        var date = new DateOnly(2021, 11, 13);
        
        var tick1 = new MinimalTradingEntry
        {
            Id = "RDSA.NL",
            SecType = SecurityType.E,
            Last = 100.0m,
            TradingDate = date,
            TradingTime = new TimeOnly(1, 0, 0)
        };

        var tick2 = new MinimalTradingEntry
        {
            Id = "RDSA.NL",
            SecType = SecurityType.E,
            Last = 102.0m,
            TradingDate = date,
            TradingTime = new TimeOnly(1, 4, 30)
        };

        Assert.Null(processor.ProcessTick(tick1));
        Assert.Null(processor.ProcessTick(tick2));
    }

    [Fact]
    public void ProcessTick_NextWindowArrives_FinalizesPreviousWindowWithLastPrice()
    {
        var processor = new WindowProcessor();
        var date = new DateOnly(2021, 11, 13);

        var tick1 = new MinimalTradingEntry
        {
            Id = "RDSA.NL",
            SecType = SecurityType.E,
            Last = 100.0m,
            TradingDate = date,
            TradingTime = new TimeOnly(1, 1, 0)
        };

        var tick2 = new MinimalTradingEntry
        {
            Id = "RDSA.NL",
            SecType = SecurityType.E,
            Last = 105.0m,
            TradingDate = date,
            TradingTime = new TimeOnly(1, 4, 0)
        };

        var tickNextWindow = new MinimalTradingEntry
        {
            Id = "RDSA.NL",
            SecType = SecurityType.E,
            Last = 108.0m,
            TradingDate = date,
            TradingTime = new TimeOnly(1, 5, 10)
        };

        processor.ProcessTick(tick1);
        processor.ProcessTick(tick2);
        var finalized = processor.ProcessTick(tickNextWindow);

        Assert.NotNull(finalized);
        Assert.Equal("RDSA.NL", finalized.SymbolId);
        Assert.Equal(105.0, finalized.LastPrice);
        Assert.Equal(new DateTime(2021, 11, 13, 1, 0, 0), finalized.WindowStart);
        Assert.Equal(new DateTime(2021, 11, 13, 1, 5, 0), finalized.WindowEnd);
    }
}
