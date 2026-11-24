using Trading.Shared.Models;
using Trading.Shared.Services;

namespace Trading.Tests;

public class WindowAndEmaTests
{
    [Fact]
    public void CalculateShortEma_AndLongEma_MatchSpecFormulas()
    {
        double shortAlpha = 2.0 / (1.0 + 38.0);
        double longAlpha = 2.0 / (1.0 + 100.0);

        double firstShort = EmaCalculator.CalculateShortEma(100.0, 0.0);
        double firstLong = EmaCalculator.CalculateLongEma(100.0, 0.0);

        Assert.Equal(100.0 * shortAlpha, firstShort, 6);
        Assert.Equal(100.0 * longAlpha, firstLong, 6);

        double secondShort = EmaCalculator.CalculateShortEma(120.0, firstShort);
        double secondLong = EmaCalculator.CalculateLongEma(120.0, firstLong);

        Assert.Equal((120.0 * shortAlpha) + (firstShort * (1.0 - shortAlpha)), secondShort, 6);
        Assert.Equal((120.0 * longAlpha) + (firstLong * (1.0 - longAlpha)), secondLong, 6);
    }

    [Fact]
    public void EvaluateBreakout_DetectsBuyAndSellCrossovers()
    {
        var initial = EmaCalculator.EvaluateBreakout(10.0, 5.0, 0.0, 0.0);
        Assert.Equal((false, false), initial);

        var buy = EmaCalculator.EvaluateBreakout(12.0, 10.0, 10.0, 10.0);
        Assert.Equal((true, false), buy);

        var sell = EmaCalculator.EvaluateBreakout(9.0, 10.0, 10.0, 10.0);
        Assert.Equal((false, true), sell);

        var none = EmaCalculator.EvaluateBreakout(15.0, 10.0, 12.0, 10.0);
        Assert.Equal((false, false), none);
    }

    [Fact]
    public void ProcessTick_FinalizesFiveMinuteWindow_AndResetClearsEmaState()
    {
        var processor = new WindowProcessor();
        var date = new DateOnly(2021, 11, 8);

        Assert.Null(processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "SYM1",
            Last = 100m,
            TradingDate = date,
            TradingTime = new TimeOnly(10, 0, 10)
        }));

        Assert.Null(processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "SYM1",
            Last = 110m,
            TradingDate = date,
            TradingTime = new TimeOnly(10, 4, 50)
        }));

        var win1 = processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "SYM1",
            Last = 120m,
            TradingDate = date,
            TradingTime = new TimeOnly(10, 5, 5)
        });

        Assert.NotNull(win1);
        Assert.Equal("SYM1", win1.SymbolId);
        Assert.Equal(new DateTime(2021, 11, 8, 8, 0, 0, DateTimeKind.Utc), win1.WindowStart);
        Assert.Equal(new DateTime(2021, 11, 8, 8, 5, 0, DateTimeKind.Utc), win1.WindowEnd);
        Assert.Equal(110.0, win1.LastPrice, 6);
        Assert.Equal(EmaCalculator.CalculateShortEma(110.0, 0.0), win1.Ema38, 6);

        var win2 = processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "SYM1",
            Last = 130m,
            TradingDate = date,
            TradingTime = new TimeOnly(10, 10, 5)
        });

        Assert.NotNull(win2);
        Assert.Equal(EmaCalculator.CalculateShortEma(120.0, win1.Ema38), win2.Ema38, 6);

        processor.Reset();

        Assert.Null(processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "SYM1",
            Last = 50m,
            TradingDate = date,
            TradingTime = new TimeOnly(10, 0, 10)
        }));

        var afterResetWin = processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "SYM1",
            Last = 60m,
            TradingDate = date,
            TradingTime = new TimeOnly(10, 5, 5)
        });

        Assert.NotNull(afterResetWin);
        Assert.Equal(50.0, afterResetWin.LastPrice, 6);
        Assert.Equal(EmaCalculator.CalculateShortEma(50.0, 0.0), afterResetWin.Ema38, 6);
    }

    [Fact]
    public void ProcessTick_ZeroOrNegativePrice_IgnoresUnseenAndFallsBackToPreviousPrice()
    {
        var processor = new WindowProcessor();
        var date = new DateOnly(2021, 11, 8);

        Assert.Null(processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "ZERO.ETR",
            Last = 0m,
            TradingDate = date,
            TradingTime = new TimeOnly(9, 0, 5)
        }));

        Assert.Null(processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "ZERO.ETR",
            Last = -1m,
            TradingDate = date,
            TradingTime = new TimeOnly(9, 0, 10)
        }));

        Assert.Null(processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "ZERO.ETR",
            Last = 50m,
            TradingDate = date,
            TradingTime = new TimeOnly(9, 5, 5)
        }));

        Assert.Null(processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "FALLBACK.ETR",
            Last = 100m,
            TradingDate = date,
            TradingTime = new TimeOnly(9, 0, 10)
        }));

        Assert.Null(processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "FALLBACK.ETR",
            Last = 0m,
            TradingDate = date,
            TradingTime = new TimeOnly(9, 4, 50)
        }));

        var win1 = processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "FALLBACK.ETR",
            Last = 0m,
            TradingDate = date,
            TradingTime = new TimeOnly(9, 5, 10)
        });

        Assert.NotNull(win1);
        Assert.Equal(100.0, win1.LastPrice, 6);
        Assert.False(win1.SellSignal);

        var win2 = processor.ProcessTick(new MinimalTradingEntry
        {
            Id = "FALLBACK.ETR",
            Last = 105m,
            TradingDate = date,
            TradingTime = new TimeOnly(9, 10, 10)
        });

        Assert.NotNull(win2);
        Assert.Equal(100.0, win2.LastPrice, 6);
    }
}
