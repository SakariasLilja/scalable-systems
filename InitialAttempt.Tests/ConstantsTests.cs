using InitialAttempt.Utils;
using Xunit;

namespace InitialAttempt.Tests;

public class ConstantsTests
{
    [Fact]
    public void WindowConstants_MatchSpecification()
    {
        Assert.Equal(300000, Constants.WindowSizeMs);
        Assert.Equal(5, Constants.WindowDurationMinutes);
        Assert.Equal(0, Constants.WindowStartHour);
    }

    [Fact]
    public void EmaConstants_MatchSpecification()
    {
        Assert.Equal(38, Constants.EmaShortPeriod);
        Assert.Equal(100, Constants.EmaLongPeriod);
        Assert.Equal(2.0 / (1.0 + 38.0), Constants.EmaShortAlpha, precision: 6);
        Assert.Equal(2.0 / (1.0 + 100.0), Constants.EmaLongAlpha, precision: 6);
    }

    [Fact]
    public void IntervalAndBatchConstants_MatchSpecification()
    {
        Assert.Equal(1.0, Constants.DefaultSimulationSpeed);
        Assert.Equal(10000, Constants.TrackedPushIntervalMs);
        Assert.Equal(2000, Constants.ActivePushIntervalMs);
        Assert.Equal(5000, Constants.DbBatchFlushIntervalMs);
        Assert.Equal(1000, Constants.DbBatchMaxCapacity);
        Assert.Equal(5000, Constants.DbBatchMaxBufferSize);
        Assert.Equal(2000, Constants.DbBatchRetryDelayMs);
        Assert.Equal(100000, Constants.SimulationChannelCapacity);
        Assert.Equal(50000, Constants.WindowConsumerChannelCapacity);
    }

    [Fact]
    public void SubscriptionConstants_MatchSpecification()
    {
        Assert.Equal("hidden", Constants.SubscriptionHidden);
        Assert.Equal("tracked", Constants.SubscriptionTracked);
        Assert.Equal("active", Constants.SubscriptionActive);
    }
}
