using CsvHelper;
using InitialAttempt.Models;

namespace InitialAttempt.Services;

public interface IReadCsvService
{
    IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken);
    IEnumerable<MinimalTradingEntry> StreamTradeEvents(string filePath, CancellationToken cancellationToken);
}

public class ReadCsvService : IReadCsvService
{
    public IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken)
    {
        return StreamTradeEvents(filePath, cancellationToken);
    }

    public IEnumerable<MinimalTradingEntry> StreamTradeEvents(string filePath, CancellationToken cancellationToken)
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
