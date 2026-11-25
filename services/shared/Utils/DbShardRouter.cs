namespace Trading.Shared.Utils;

public sealed class DbShardRouter
{
    private readonly ConsistentHashRing ring;
    private readonly Dictionary<string, string> connectionByNode;
    private readonly IReadOnlyList<string> allConnectionStrings;

    public DbShardRouter(
        string? shardsCsv,
        string? fallbackConnectionString,
        string defaultHost = "localhost",
        string defaultPort = "5432",
        string db = "trading_db",
        string user = "postgres",
        string password = "postgres_password"
    )
    {
        var rawEndpoints = (shardsCsv ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        connectionByNode = new Dictionary<string, string>(StringComparer.Ordinal);

        if (rawEndpoints.Length > 0)
        {
            var list = new List<string>(rawEndpoints.Length);
            foreach (var endpoint in rawEndpoints)
            {
                var (host, port) = SplitHostPort(endpoint, defaultPort);
                var connStr = $"Host={host};Port={port};Database={db};Username={user};Password={password};Timeout=5;Command Timeout=10";
                connectionByNode[endpoint] = connStr;
                list.Add(connStr);
            }

            ring = new ConsistentHashRing(rawEndpoints);
            allConnectionStrings = list;
            return;
        }

        var single = !string.IsNullOrWhiteSpace(fallbackConnectionString)
            ? fallbackConnectionString
            : $"Host={defaultHost};Port={defaultPort};Database={db};Username={user};Password={password};Timeout=5;Command Timeout=10";

        const string defaultNode = "default";
        connectionByNode[defaultNode] = single;
        ring = new ConsistentHashRing([defaultNode]);
        allConnectionStrings = [single];
    }

    public IReadOnlyList<string> AllConnectionStrings => allConnectionStrings;

    public string GetConnectionStringForSymbol(string symbolId)
    {
        var node = ring.GetNode(symbolId);
        return connectionByNode[node];
    }

    private static (string Host, string Port) SplitHostPort(string endpoint, string defaultPort)
    {
        int colon = endpoint.LastIndexOf(':');
        if (colon > 0 && colon < endpoint.Length - 1)
        {
            return (endpoint[..colon], endpoint[(colon + 1)..]);
        }

        return (endpoint, defaultPort);
    }
}
