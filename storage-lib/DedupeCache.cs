namespace ScreenRecall.Storage;

/// <summary>
/// Bounded cache of hashes known to exist in the asset store, so the hot dedupe path usually answers
/// without touching the file system. It is a cache only: a miss falls back to an existence probe, so
/// clearing it can never cause data loss.
/// </summary>
public sealed class DedupeCache
{
    private readonly HashSet<ulong> _known = new();
    private readonly int _capacity;

    public DedupeCache(int capacity = 262_144)
    {
        _capacity = Math.Max(1024, capacity);
    }

    /// <summary>Hashes currently cached as present.</summary>
    public int Count => _known.Count;

    /// <summary>Cache lookups that hit.</summary>
    public long Hits { get; private set; }

    /// <summary>Cache lookups that missed (and therefore probed the store).</summary>
    public long Misses { get; private set; }

    /// <summary>True when the hash is known to be stored.</summary>
    public bool Contains(ulong hash)
    {
        if (_known.Contains(hash))
        {
            Hits++;
            return true;
        }

        Misses++;
        return false;
    }

    /// <summary>Records that an asset exists.</summary>
    public void Add(ulong hash)
    {
        if (_known.Count >= _capacity)
        {
            // Bounded memory beats a perfect hit rate here: correctness is preserved by the probe.
            _known.Clear();
        }

        _known.Add(hash);
    }

    /// <summary>Drops the cache (used after pruning rewrites the asset store).</summary>
    public void Clear() => _known.Clear();
}
