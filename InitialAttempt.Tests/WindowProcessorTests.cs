using InitialAttempt.Models;
using InitialAttempt.Services;
using Xunit;

namespace InitialAttempt.Tests;

public class WindowProcessorTests
{
    private static readonly TimeSpan CestOffset = TimeSpan.FromHours(2);

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

        var expectedStartUtc = new DateTimeOffset(2021, 11, 13, 1, 0, 0, CestOffset).UtcDateTime;
        var expectedEndUtc = expectedStartUtc.AddMinutes(5);

        Assert.Equal(expectedStartUtc, finalized.WindowStart);
        Assert.Equal(expectedEndUtc, finalized.WindowEnd);
        Assert.Equal(DateTimeKind.Utc, finalized.WindowStart.Kind);
    }

    [Fact]
    public void ProcessTick_LateTickFromPastWindow_IsIgnored()
    {
        var processor = new WindowProcessor();
        var date = new DateOnly(2021, 11, 13);

        var tick1 = new MinimalTradingEntry
        {
            Id = "RDSA.NL",
            SecType = SecurityType.E,
            Last = 100.0m,
            TradingDate = date,
            TradingTime = new TimeOnly(1, 6, 0)
        };

        var lateTick = new MinimalTradingEntry
        {
            Id = "RDSA.NL",
            SecType = SecurityType.E,
            Last = 50.0m,
            TradingDate = date,
            TradingTime = new TimeOnly(1, 2, 0)
        };

        processor.ProcessTick(tick1);
        var result = processor.ProcessTick(lateTick);

        Assert.Null(result);
    }
}
