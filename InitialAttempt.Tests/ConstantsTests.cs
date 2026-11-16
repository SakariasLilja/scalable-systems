using InitialAttempt.Utils;
using Microsoft.Extensions.Configuration;
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
        Assert.True(Math.Abs((2.0 / (1.0 + 38.0)) - Constants.EmaShortAlpha) < 1e-9);
        Assert.True(Math.Abs((2.0 / (1.0 + 100.0)) - Constants.EmaLongAlpha) < 1e-9);
    }

    [Fact]
    public void IntervalAndBatchConstants_MatchSpecification()
    {
        Assert.Equal(1.0, Constants.DefaultSimulationSpeed);
        Assert.Equal("08-11-21", Constants.DefaultTradingDate);
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
    public void IConstants_ProvidesPascalCaseProperties()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CSV_PREFIX"] = "custom-prefix-",
                ["CSV_SUFFIX"] = ".dat",
                ["DATA_DIRECTORY"] = "\"./QuotedData/\""
            })
            .Build();
        IConstants constants = new Constants(Microsoft.Extensions.Logging.Abstractions.NullLogger<Constants>.Instance, config);
        Assert.Equal("custom-prefix-", constants.CsvPrefix);
        Assert.Equal(".dat", constants.CsvSuffix);
        Assert.Equal("./QuotedData/", constants.CsvDirectory);
    }

    [Fact]
    public void SubscriptionConstants_MatchSpecification()
    {
        Assert.Equal("hidden", Constants.SubscriptionHidden);
        Assert.Equal("tracked", Constants.SubscriptionTracked);
        Assert.Equal("active", Constants.SubscriptionActive);
    }
}
