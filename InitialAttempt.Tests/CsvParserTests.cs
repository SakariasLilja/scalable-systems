using CsvHelper;
using InitialAttempt.Services;
using InitialAttempt.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InitialAttempt.Tests;

public class CsvParserTests
{
    [Fact]
    public void StreamTradeEvents_FiltersOutNullLastAndComments()
    {
        var csvContent = """
        # Comment line
        ID,SecType,Date,Time,Ask,Ask volume,Bid,Bid volume,Ask time,Day's high ask,Close,Currency,Day's high ask time,Day's high,ISIN,Auction price,Day's low ask,Day's low,Day's low ask time,Open,Nominal value,Last,Last volume,Trading time,Total volume,Mid price,Trading date,Profit,Current price,Related indices,Day high bid time, Day low bid time,Open Time, Last trade time, Close Time, Day high Time, Day low Time,Bid time,Auction Time
        A0HN5C.ETR,E,13-11-2021,01:01:23.126,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,,
        RDSA.NL,E,13-11-2021,02:00:00.000,0.000000,0,0.000000,0,,0.000000,0.000000,,,0.000000,,,0.000000,0.000000,,0.000000,,100.50,10,02:00:00.000,0,,13-11-2021,,,,,,,00:00:00.000,00:00:00.000,,,,,
        """;
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, csvContent);

        try
        {
            var service = new ReadCsvService();
            var results = service.StreamTradeEvents(tempFile, CancellationToken.None).ToList();

            Assert.Single(results);
            Assert.Equal("RDSA.NL", results[0].Id);
            Assert.Equal(100.50m, results[0].Last);
            Assert.Equal(new TimeOnly(2, 0, 0), results[0].TradingTime);
            Assert.Equal(new DateOnly(2021, 11, 13), results[0].TradingDate);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void MinimalTradingEntry_ResolvesEffectiveDateAndTime_WhenTradingDateIsEmpty()
    {
        var csvContent = """
        # Comment
        ID,SecType,Date,Time,Ask,Ask volume,Bid,Bid volume,Ask time,Day's high ask,Close,Currency,Day's high ask time,Day's high,ISIN,Auction price,Day's low ask,Day's low,Day's low ask time,Open,Nominal value,Last,Last volume,Trading time,Total volume,Mid price,Trading date,Profit,Current price,Related indices,Day high bid time, Day low bid time,Open Time, Last trade time, Close Time, Day high Time, Day low Time,Bid time,Auction Time
        TEST.SYM,E,08-11-2021,05:10:04.000,,,,,,,,,,,,,,,,,,100.50,10,,0,,13-11-2021,,,,,,,00:00:00.000,00:00:00.000,,,,,
        """;
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, csvContent);

        try
        {
            var service = new ReadCsvService();
            var records = service.StreamTradeEvents(tempFile, CancellationToken.None).ToList();

            Assert.Single(records);
            var record = records[0];
            Assert.Equal(new DateOnly(2021, 11, 13), record.EffectiveDate);
            Assert.Equal(new TimeOnly(5, 10, 4), record.EffectiveTime);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void MinimalTradingEntry_ResolvesEffectiveDate_FromSystemDate_WhenTradingDateIsEmpty()
    {
        var csvContent = """
        # Comment
        ID,SecType,Date,Time,Ask,Ask volume,Bid,Bid volume,Ask time,Day's high ask,Close,Currency,Day's high ask time,Day's high,ISIN,Auction price,Day's low ask,Day's low,Day's low ask time,Open,Nominal value,Last,Last volume,Trading time,Total volume,Mid price,Trading date,Profit,Current price,Related indices,Day high bid time, Day low bid time,Open Time, Last trade time, Close Time, Day high Time, Day low Time,Bid time,Auction Time
        TEST.SYM,E,08-11-2021,05:10:04.000,,,,,,,,,,,,,,,,,,100.50,10,01:02:03.000,0,,,,,,,,,,00:00:00.000,00:00:00.000,,,,,
        """;
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, csvContent);

        try
        {
            var service = new ReadCsvService();
            var records = service.StreamTradeEvents(tempFile, CancellationToken.None).ToList();

            Assert.Single(records);
            var record = records[0];
            Assert.Equal(new DateOnly(2021, 11, 8), record.EffectiveDate);
            Assert.Equal(new TimeOnly(1, 2, 3), record.EffectiveTime);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [Fact]
    public void StreamTradeEvents_SkipsCorruptedRecordsAndParsesColonsAsNull()
    {
        var csvContent = """
        # Comment
        ID,SecType,Date,Time,Ask,Ask volume,Bid,Bid volume,Ask time,Day's high ask,Close,Currency,Day's high ask time,Day's high,ISIN,Auction price,Day's low ask,Day's low,Day's low ask time,Open,Nominal value,Last,Last volume,Trading time,Total volume,Mid price,Trading date,Profit,Current price,Related indices,Day high bid time, Day low bid time,Open Time, Last trade time, Close Time, Day high Time, Day low Time,Bid time,Auction Time
        T1.SYM,E,08-11-2021,::,,,,,,,,,,,,,,,,,,10.50,10,00:00:00.000,0,,08-11-2021,,,,,,,00:00:00.000,00:00:00.000,,,,,
        T2.CORRUPT,INVALID_SECTYPE,08-11-2021,05:10:04.000,,,,,,,,,,,,,,,,,,NOT_A_DECIMAL,,,,,,,,,,,,,,,,,,,
        T3.SYM,E,08-11-2021,05:10:05.000,,,,,,,,,,,,,,,,,,20.50,10,00:00:00.000,0,,08-11-2021,,,,,,,00:00:00.000,00:00:00.000,,,,,
        """;
        var tempFile = Path.GetTempFileName();
        File.WriteAllText(tempFile, csvContent);

        try
        {
            var service = new ReadCsvService(NullLogger<ReadCsvService>.Instance);
            var records = service.StreamTradeEvents(tempFile, CancellationToken.None).ToList();

            Assert.Equal(2, records.Count);
            Assert.Equal("T1.SYM", records[0].Id);
            Assert.Null(records[0].Time);
            Assert.Equal("T3.SYM", records[1].Id);
            Assert.Equal(20.50m, records[1].Last);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }
}
