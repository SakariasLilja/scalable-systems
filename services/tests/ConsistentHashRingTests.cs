using Trading.Shared.Utils;
using Xunit;

namespace Trading.Tests;

public class ConsistentHashRingTests
{
    [Fact]
    public void GetNode_IsDeterministicAndCaseInsensitive()
    {
        var ring = new ConsistentHashRing(["timescaledb-0:5432", "timescaledb-1:5432"]);

        var upper = ring.GetNode("SAP.ETR");
        var lower = ring.GetNode("sap.etr");
        var padded = ring.GetNode("  Sap.Etr ");

        Assert.Equal(upper, lower);
        Assert.Equal(upper, padded);
    }

    [Fact]
    public void GetNode_DistributesAcrossTwoShards()
    {
        var ring = new ConsistentHashRing(["timescaledb-0:5432", "timescaledb-1:5432"]);
        var counts = new Dictionary<string, int>
        {
            ["timescaledb-0:5432"] = 0,
            ["timescaledb-1:5432"] = 0
        };

        for (int i = 0; i < 500; i++)
        {
            var node = ring.GetNode($"SYM{i:D4}.ETR");
            counts[node]++;
        }

        Assert.InRange(counts["timescaledb-0:5432"], 175, 325);
        Assert.InRange(counts["timescaledb-1:5432"], 175, 325);
    }

    [Fact]
    public void GetNode_MinimalRemappingWhenAddingThirdShard()
    {
        var ring2 = new ConsistentHashRing(["timescaledb-0:5432", "timescaledb-1:5432"]);
        var ring3 = new ConsistentHashRing(["timescaledb-0:5432", "timescaledb-1:5432", "timescaledb-2:5432"]);

        int moved = 0;
        const int total = 600;
        for (int i = 0; i < total; i++)
        {
            var key = $"STOCK{i:D4}.ETR";
            if (ring2.GetNode(key) != ring3.GetNode(key))
            {
                moved++;
            }
        }

        Assert.InRange(moved, 100, 300);
    }

    [Fact]
    public void DbShardRouter_ParsesTimescaleDbShardsCsv()
    {
        var router = new DbShardRouter(
            "timescaledb-0.timescaledb:5432,timescaledb-1.timescaledb:5432",
            null,
            db: "trading_db",
            user: "postgres",
            password: "secret"
        );

        Assert.Equal(2, router.AllConnectionStrings.Count);
        Assert.Contains("Host=timescaledb-0.timescaledb;Port=5432;", router.AllConnectionStrings[0]);
        Assert.Contains("Host=timescaledb-1.timescaledb;Port=5432;", router.AllConnectionStrings[1]);

        var conn = router.GetConnectionStringForSymbol("SAP.ETR");
        Assert.Contains(conn, router.AllConnectionStrings);
    }

    [Fact]
    public void DbShardRouter_FallsBackToSingleConnectionString()
    {
        const string fallback = "Host=single-db;Port=5432;Database=trading_db;Username=u;Password=p";
        var router = new DbShardRouter("", fallback);

        Assert.Single(router.AllConnectionStrings);
        Assert.Equal(fallback, router.AllConnectionStrings[0]);
        Assert.Equal(fallback, router.GetConnectionStringForSymbol("SIE.ETR"));
    }
}
