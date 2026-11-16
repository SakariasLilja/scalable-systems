using InitialAttempt.Utils;

namespace InitialAttempt.Services;

public interface ISymbolCatalogService
{
    IReadOnlyList<string> SearchSymbols(string? query, int limit = Constants.DefaultSymbolSearchLimit);
}
