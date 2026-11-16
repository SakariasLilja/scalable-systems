using InitialAttempt.Services;
using InitialAttempt.Utils;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InitialAttempt.Tests;

public class SymbolCatalogServiceTests
{
    private class FakeConstants : IConstants
    {
        public string CsvPrefix { get; set; } = "debs2022-gc-trading-day-";
        public string CsvSuffix { get; set; } = ".csv";
        public string CsvDirectory { get; set; } = ".";
    }

    [Fact]
    public void SearchSymbols_WithEmptyQuery_ReturnsTopSymbolsAlphabetically()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var csvPath = Path.Combine(tempDir, "debs2022-gc-trading-day-test.csv");
            File.WriteAllText(csvPath, "Id,SecType\nMSFT,1\nAAPL,1\nGOOG,1\nAMZN,1\nAAPL,1\n");

            var constants = new FakeConstants { CsvDirectory = tempDir };
            var service = new SymbolCatalogService(NullLogger<SymbolCatalogService>.Instance, constants);
            var results = service.SearchSymbols("", 3);

            Assert.Equal(3, results.Count);
            Assert.Equal(["AAPL", "AMZN", "GOOG"], results);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SearchSymbols_WithSubstringQuery_FiltersCaseInsensitively()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var csvPath = Path.Combine(tempDir, "debs2022-gc-trading-day-test.csv");
            File.WriteAllText(csvPath, "Id,SecType\nRDSA.NL,1\nA0HN5C.ETR,1\nRDSB.NL,1\n");

            var constants = new FakeConstants { CsvDirectory = tempDir };
            var service = new SymbolCatalogService(NullLogger<SymbolCatalogService>.Instance, constants);
            var results = service.SearchSymbols("rds", 10);

            Assert.Equal(2, results.Count);
            Assert.Contains("RDSA.NL", results);
            Assert.Contains("RDSB.NL", results);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void SearchSymbols_WithNonExistentDirectory_ReturnsEmptyList()
    {
        var constants = new FakeConstants { CsvDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()) };
        var service = new SymbolCatalogService(NullLogger<SymbolCatalogService>.Instance, constants);
        var results = service.SearchSymbols("test", 10);

        Assert.Empty(results);
    }

    [Fact]
    public void SearchSymbols_RespectsMaxLimit()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        try
        {
            var csvPath = Path.Combine(tempDir, "debs2022-gc-trading-day-test.csv");
            File.WriteAllText(csvPath, "Id,SecType\nSYM1,1\nSYM2,1\nSYM3,1\nSYM4,1\nSYM5,1\n");

            var constants = new FakeConstants { CsvDirectory = tempDir };
            var service = new SymbolCatalogService(NullLogger<SymbolCatalogService>.Instance, constants);
            var results = service.SearchSymbols(null, 2);

            Assert.Equal(2, results.Count);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }
}
