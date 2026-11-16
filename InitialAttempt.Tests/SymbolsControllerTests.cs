using InitialAttempt.Controllers;
using InitialAttempt.Services;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace InitialAttempt.Tests;

public class SymbolsControllerTests
{
    private class FakeSymbolCatalogService : ISymbolCatalogService
    {
        public string? LastQuery;
        public int LastLimit;
        public List<string> ReturnedSymbols = ["AAPL", "MSFT"];

        public IReadOnlyList<string> SearchSymbols(string? query, int limit)
        {
            LastQuery = query;
            LastLimit = limit;
            return ReturnedSymbols;
        }
    }

    [Fact]
    public void Search_CallsCatalogServiceAndReturnsOk()
    {
        var fakeService = new FakeSymbolCatalogService();
        var controller = new SymbolsController(fakeService);

        var result = controller.Search("aapl", 10);
        var okResult = Assert.IsType<OkObjectResult>(result);
        var symbols = Assert.IsAssignableFrom<IReadOnlyList<string>>(okResult.Value);

        Assert.Equal("aapl", fakeService.LastQuery);
        Assert.Equal(10, fakeService.LastLimit);
        Assert.Equal(2, symbols.Count);
    }
}
