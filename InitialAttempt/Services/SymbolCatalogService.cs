using InitialAttempt.Utils;
using Microsoft.Extensions.Logging;

namespace InitialAttempt.Services;

public class SymbolCatalogService : ISymbolCatalogService
{
    private readonly ILogger<SymbolCatalogService> logger;
    private readonly IConstants constants;
    private readonly Lazy<IReadOnlyList<string>> cachedSymbols;

    public SymbolCatalogService(ILogger<SymbolCatalogService> logger, IConstants constants)
    {
        this.logger = logger;
        this.constants = constants;
        this.cachedSymbols = new Lazy<IReadOnlyList<string>>(LoadSymbolsFromDisk);
    }

    public IReadOnlyList<string> SearchSymbols(string? query, int limit = Constants.DefaultSymbolSearchLimit)
    {
        var maxResults = Math.Clamp(limit, 1, 100);
        var allSymbols = cachedSymbols.Value;

        if (string.IsNullOrWhiteSpace(query))
        {
            return allSymbols.Take(maxResults).ToList();
        }

        var normalizedQuery = query.Trim();
        return allSymbols
            .Where(s => s.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
    }

    private IReadOnlyList<string> LoadSymbolsFromDisk()
    {
        var symbols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(constants.CsvDirectory))
        {
            logger.LogWarning("CSV directory '{CsvDirectory}' does not exist for symbol catalog", constants.CsvDirectory);
            return Array.Empty<string>();
        }

        var searchPattern = $"{constants.CsvPrefix}*{constants.CsvSuffix}";
        var files = Directory.GetFiles(constants.CsvDirectory, searchPattern);

        foreach (var file in files)
        {
            try
            {
                using var reader = new StreamReader(file);
                var lineCount = 0;

                while (reader.ReadLine() is { } rawLine && lineCount < Constants.MaxSymbolCatalogSampleLines)
                {
                    var line = rawLine.Trim();
                    if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                    {
                        continue;
                    }

                    lineCount++;
                    var commaIndex = line.IndexOf(',');
                    if (commaIndex > 0)
                    {
                        var symbol = line[..commaIndex].Trim();
                        if (!string.IsNullOrEmpty(symbol) && !symbol.Equals("ID", StringComparison.OrdinalIgnoreCase))
                        {
                            symbols.Add(symbol);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed scanning symbols from file {File}", file);
            }
        }

        return symbols.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
