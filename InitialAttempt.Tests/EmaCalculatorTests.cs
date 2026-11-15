using InitialAttempt.Services;
using Xunit;

namespace InitialAttempt.Tests;

public class EmaCalculatorTests
{
    [Fact]
    public void CalculateEma_FirstWindow_ComputesFromZero()
    {
        double closePrice = 100.0;
        double previousEma = 0.0;
        
        double ema38 = EmaCalculator.CalculateEma(closePrice, previousEma, 38);
        double expected38 = 100.0 * (2.0 / 39.0);
        Assert.Equal(expected38, ema38, 5);

        double ema100 = EmaCalculator.CalculateEma(closePrice, previousEma, 100);
        double expected100 = 100.0 * (2.0 / 101.0);
        Assert.Equal(expected100, ema100, 5);
    }

    [Fact]
    public void CalculateEma_SubsequentWindow_WeightsProperly()
    {
        double closePrice = 110.0;
        double previousEma = 100.0;

        double ema38 = EmaCalculator.CalculateEma(closePrice, previousEma, 38);
        double alpha38 = 2.0 / 39.0;
        double expected38 = (110.0 * alpha38) + (100.0 * (1.0 - alpha38));
        Assert.Equal(expected38, ema38, 5);
    }

    [Fact]
    public void EvaluateBreakout_BullishCross_ReturnsBuySignal()
    {
        var (buy, sell) = EmaCalculator.EvaluateBreakout(105.0, 100.0, 98.0, 99.0);
        Assert.True(buy);
        Assert.False(sell);
    }

    [Fact]
    public void EvaluateBreakout_BearishCross_ReturnsSellSignal()
    {
        var (buy, sell) = EmaCalculator.EvaluateBreakout(95.0, 100.0, 102.0, 101.0);
        Assert.False(buy);
        Assert.True(sell);
    }

    [Fact]
    public void EvaluateBreakout_NoCross_ReturnsFalse()
    {
        var (buy, sell) = EmaCalculator.EvaluateBreakout(105.0, 100.0, 104.0, 99.0);
        Assert.False(buy);
        Assert.False(sell);
    }
}
