using CsvHelper;
using InitialAttempt.Models;

namespace InitialAttempt.Services;

public interface IReadCsvService
{
    IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken);
    IEnumerable<MinimalTradingEntry> streamTradeEvents(string filePath, CancellationToken cancellationToken);
}

[CsvRecord]
public partial class MinimalTradingEntry
{
    [CsvColumn("ID")]
    public string Id { get; set; } = string.Empty;
    [CsvColumn("SecType")]
    public SecurityType SecType { get; set; }
    [CsvColumn("Last")]
    public decimal? Last { get; set; }
    [CsvColumn("Trading time")]
    [CsvFormat("HH:mm:ss.fff")]
    public TimeOnly? TradingTime { get; set; }
    [CsvColumn("Trading date")]
    [CsvFormat("dd-MM-yyyy")]
    public DateOnly? TradingDate { get; set; }
}

public class ReadCsvService : IReadCsvService
{
    public IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken)
    {
        return streamTradeEvents(filePath, cancellationToken);
    }

    public IEnumerable<MinimalTradingEntry> streamTradeEvents(string filePath, CancellationToken cancellationToken)
    {
        var config = new CsvReaderOptions
        {
            HasHeader = true,
            Separator = ',',
            CommentPrefix = '#'
        };

        using var reader = CsvReader.FromFile(filePath, config);

        foreach (MinimalTradingEntry record in reader.GetRecords<MinimalTradingEntry>())
        {
            if (cancellationToken.IsCancellationRequested)
            {
                yield break;
            }

            if (record.Last.HasValue)
            {
                yield return record;
            }
        }
    }
}
