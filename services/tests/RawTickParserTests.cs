using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Trading.Worker.Services;

namespace Trading.Tests;

public class RawTickParserTests
{
    private class RecordingWarningLogger : IAsyncWarningLogger
    {
        public ConcurrentQueue<string> Warnings { get; } = new();
        public long DroppedCount => 0;

        public void EnqueueWarning(string message) => Warnings.Enqueue(message);
    }

    [Fact]
    public void TryParseTradeTick_ValidTradeJson_ReturnsMinimalTradingEntry()
    {
        var logger = new RecordingWarningLogger();
        using var doc = JsonDocument.Parse("""
        {
            "id": "SAP.ETR",
            "Last": "125.45",
            "date": "08-11-2021",
            "time": "09:15:30.123",
            "Trading Date": "08-11-2021",
            "Trading Time": "09:15:30.456"
        }
        """);

        bool parsed = RawTickParser.TryParseTradeTick(doc.RootElement, logger, out var entry);

        Assert.True(parsed);
        Assert.NotNull(entry);
        Assert.Equal("SAP.ETR", entry.Id);
        Assert.Equal(125.45m, entry.Last);
        Assert.Equal(new DateOnly(2021, 11, 8), entry.Date);
        Assert.Equal(new TimeOnly(9, 15, 30, 123), entry.Time);
        Assert.Equal(new DateOnly(2021, 11, 8), entry.TradingDate);
        Assert.Equal(new TimeOnly(9, 15, 30, 456), entry.TradingTime);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void TryParseTradeTick_NumericLastJson_ReturnsMinimalTradingEntry()
    {
        var logger = new RecordingWarningLogger();
        using var doc = JsonDocument.Parse("""
        {
            "ID": "BMW.ETR",
            "Last": 88.25,
            "Trading date": "08-11-2021",
            "Trading time": "10:00:01"
        }
        """);

        bool parsed = RawTickParser.TryParseTradeTick(doc.RootElement, logger, out var entry);

        Assert.True(parsed);
        Assert.NotNull(entry);
        Assert.Equal("BMW.ETR", entry.Id);
        Assert.Equal(88.25m, entry.Last);
        Assert.Equal(new TimeOnly(10, 0, 1), entry.EffectiveTime);
        Assert.Empty(logger.Warnings);
    }

    [Theory]
    [InlineData("""{"ID":"A0V8JY.ETR","Last":""}""")]
    [InlineData("""{"ID":"A0V8JY.ETR","Last":null}""")]
    [InlineData("""{"ID":"A0V8JY.ETR","Time":"09:00:00.000"}""")]
    public void TryParseTradeTick_QuoteWithEmptyOrNullLast_ReturnsFalseWithoutWarning(string json)
    {
        var logger = new RecordingWarningLogger();
        using var doc = JsonDocument.Parse(json);

        bool parsed = RawTickParser.TryParseTradeTick(doc.RootElement, logger, out var entry);

        Assert.False(parsed);
        Assert.Null(entry);
        Assert.Empty(logger.Warnings);
    }

    [Fact]
    public void TryParseTradeTick_CorruptedFieldsAndWrongJsonTypes_NeverThrowsAndLogsWarning()
    {
        var logger = new RecordingWarningLogger();

        using var colonDoc = JsonDocument.Parse("""
        {
            "ID": "SAP.ETR",
            "Last": "100.5",
            "Time": "::",
            "Trading date": "08-11-2021",
            "Trading time": "10:00:00.000"
        }
        """);
        bool colonParsed = RawTickParser.TryParseTradeTick(colonDoc.RootElement, logger, out var colonEntry);
        Assert.True(colonParsed);
        Assert.NotNull(colonEntry);
        Assert.Null(colonEntry.Time);
        Assert.Equal(new TimeOnly(10, 0, 0), colonEntry.EffectiveTime);
        Assert.Empty(logger.Warnings);

        using var badDecimalDoc = JsonDocument.Parse("""{"ID":"SAP.ETR","Last":"not-a-decimal","Trading date":"08-11-2021","Trading time":"10:00:00"}""");
        Assert.False(RawTickParser.TryParseTradeTick(badDecimalDoc.RootElement, logger, out _));
        Assert.NotEmpty(logger.Warnings);

        logger.Warnings.Clear();
        using var mixedCorruptDoc = JsonDocument.Parse("""{"ID":"SAP.ETR","Last":[1,2],"Trading time":99999,"Date":"99-99-9999"}""");
        Assert.False(RawTickParser.TryParseTradeTick(mixedCorruptDoc.RootElement, logger, out _));
        Assert.NotEmpty(logger.Warnings);

        logger.Warnings.Clear();
        using var numericIdDoc = JsonDocument.Parse("""{"ID":12345,"Last":"50.0"}""");
        Assert.False(RawTickParser.TryParseTradeTick(numericIdDoc.RootElement, logger, out _));
        Assert.NotEmpty(logger.Warnings);

        logger.Warnings.Clear();
        using var arrayRootDoc = JsonDocument.Parse("[1, 2]");
        Assert.False(RawTickParser.TryParseTradeTick(arrayRootDoc.RootElement, logger, out _));
        Assert.NotEmpty(logger.Warnings);
    }

    [Fact]
    public void AsyncWarningLogger_WhenFloodedBeyondCapacity_DropsWithoutBlockingAndTracksDroppedCount()
    {
        var warningLogger = new AsyncWarningLogger(NullLogger<AsyncWarningLogger>.Instance, capacity: 16);

        for (int i = 0; i < 516; i++)
        {
            warningLogger.EnqueueWarning($"warning-{i}");
        }

        Assert.Equal(500, warningLogger.DroppedCount);
    }
}
