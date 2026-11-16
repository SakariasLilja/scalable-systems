using CsvHelper;
using InitialAttempt.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace InitialAttempt.Services;

public interface IReadCsvService
{
    IEnumerable<MinimalTradingEntry> StreamData(string filePath, CancellationToken cancellationToken);
    IEnumerable<MinimalTradingEntry> StreamTradeEvents(string filePath, CancellationToken cancellationToken);
}

public class ReadCsvService(ILogger<ReadCsvService>? logger = null) : IReadCsvService
{
    private readonly ILogger<ReadCsvService> logger = logger ?? NullLogger<ReadCsvService>.Instance;

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
            CommentPrefix = '#',
            NullValues = ["", "::"],
            OnRecordError = error =>
            {
                this.logger.LogWarning(error.Exception, "Malformed CSV record at row {RowNumber}. Skipping: {RawRecord}", error.RowNumber, error.RawRecord);
                return true;
            }
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
