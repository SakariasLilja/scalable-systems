using System.Diagnostics;
using InitialAttempt.Services;
using InitialAttempt.Utils;
using Microsoft.AspNetCore.Mvc;

namespace InitialAttempt.Controllers;

[ApiController]
[Route("api")]
public class RequestSimulatorController(
    ILogger<RequestSimulatorController> logger,
    IReadCsvService readCsvService,
    IConstants constants
) : ControllerBase
{
    [HttpGet("first", Name = "GetFirstEntry")]
    public async Task<IEnumerable<string>> GetFirstEntry()
    {
        var filePath = Path.Combine(constants.CSV_DIRECTORY, $"{constants.CSV_PREFIX}08-11-21{constants.CSV_SUFFIX}");
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        var stopwatch = Stopwatch.StartNew();
        var records = readCsvService.StreamData(filePath, cancellationToken).Take(1).ToList();
        stopwatch.Stop();

        logger.LogInformation("Time taken to read first entry: {ElapsedMilliseconds} ms", stopwatch.ElapsedMilliseconds);

        return records.Select(record => $"{record.Id}, {record.SecType}, {record.Last}, {record.TradingTime}, {record.TradingDate}");
    }

    [HttpGet("one-sec", Name = "GetRecordsFor1Second")]
    public async Task<OneSecResponse> GetRecordsFor1Second()
    {
        var filePath = Path.Combine(constants.CSV_DIRECTORY, $"{constants.CSV_PREFIX}13-11-21{constants.CSV_SUFFIX}");
        var cancellationTokenSource = new CancellationTokenSource();
        var cancellationToken = cancellationTokenSource.Token;

        var stopwatch = Stopwatch.StartNew();
        var records = readCsvService.StreamData(filePath, cancellationToken)
            .TakeWhile(_ => stopwatch.Elapsed < TimeSpan.FromSeconds(1))
            .ToArray();
        stopwatch.Stop();

        var nullTimeEntries = records.Count(r => r.TradingTime == null);
        double nullTimePercentage = records.Length > 0 ? (double)nullTimeEntries / records.Length * 100 : 0;

        var timeTaken = stopwatch.ElapsedMilliseconds;

        logger.LogInformation("Time taken to read records for 1 second: {ElapsedMilliseconds} ms", timeTaken);

        return new OneSecResponse(
            Count: records.Length,
            FirstEntry: records.FirstOrDefault(),
            LastEntry: records.LastOrDefault(),
            NullTimeEntries: nullTimeEntries,
            NullTimePercentage: nullTimePercentage
        );
    }

    public record OneSecResponse(int Count, MinimalTradingEntry? FirstEntry, MinimalTradingEntry? LastEntry, int NullTimeEntries, double NullTimePercentage);
}