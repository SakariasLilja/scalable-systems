using System.Text;

namespace Trading.Shared.Utils;

public sealed class ConsistentHashRing
{
    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;

    private readonly ulong[] ringHashes;
    private readonly string[] ringOwners;
    private readonly IReadOnlyList<string> nodes;

    public ConsistentHashRing(IEnumerable<string> shardNodes, int virtualNodesPerShard = Constants.ConsistentHashVirtualNodes)
    {
        var distinct = shardNodes
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        if (distinct.Length == 0)
        {
            throw new ArgumentException("At least one shard node is required.", nameof(shardNodes));
        }

        int vnodes = Math.Max(1, virtualNodesPerShard);
        nodes = distinct;

        var entries = new List<(ulong Hash, string Owner)>(distinct.Length * vnodes);
        foreach (var node in distinct)
        {
            for (int i = 0; i < vnodes; i++)
            {
                entries.Add((HashFnv1a64($"{node}#{i}"), node));
            }
        }

        entries.Sort(static (a, b) => a.Hash.CompareTo(b.Hash));
        ringHashes = new ulong[entries.Count];
        ringOwners = new string[entries.Count];
        for (int i = 0; i < entries.Count; i++)
        {
            ringHashes[i] = entries[i].Hash;
            ringOwners[i] = entries[i].Owner;
        }
    }

    public IReadOnlyList<string> Nodes => nodes;

    public string GetNode(string key)
    {
        if (nodes.Count == 1)
        {
            return nodes[0];
        }

        var normalized = (key ?? string.Empty).Trim().ToUpperInvariant();
        ulong hash = HashFnv1a64(normalized);
        int idx = Array.BinarySearch(ringHashes, hash);
        if (idx < 0)
        {
            idx = ~idx;
            if (idx >= ringHashes.Length)
            {
                idx = 0;
            }
        }

        return ringOwners[idx];
    }

    public static ulong HashFnv1a64(string value)
    {
        ulong hash = FnvOffsetBasis;
        byte[] bytes = Encoding.UTF8.GetBytes(value);
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= FnvPrime;
        }

        return hash;
    }
}
