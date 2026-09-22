namespace ScreenRecall.Storage;

/// <summary>
/// Bounded cache of hashes known to exist in the asset store, so the hot dedupe path usually answers
/// without touching the file system. It is a cache only: a miss falls back to an existence probe, so
/// clearing it can never cause data loss.
/// </summary>
public sealed class DedupeCache
{
    // Generations are swapped by reference when the newer one fills, so these cannot be readonly.
    private HashSet<ulong> _current = new();
    private HashSet<ulong> _previous = new();
    private readonly int _segmentCapacity;

    public DedupeCache(int capacity = 262_144)
    {
        _segmentCapacity = Math.Max(512, Math.Max(1024, capacity) / 2);
    }

    /// <summary>Hashes currently cached as present, across both generations.</summary>
    public int Count => _current.Count + _previous.Count;

    /// <summary>
    /// Upper bound on <see cref="Count"/>: twice the per-generation segment capacity, since two generations are
    /// live. Callers seeding the cache (see <c>CaptureEngine.SeedDedupeCache</c>) stop here.
    /// </summary>
    public int Capacity => _segmentCapacity * 2;

    /// <summary>Cache lookups that hit.</summary>
    public long Hits { get; private set; }

    /// <summary>Cache lookups that missed (and therefore probed the store).</summary>
    public long Misses { get; private set; }

    /// <summary>True when the hash is known to be stored.</summary>
    public bool Contains(ulong hash)
    {
        // Two generations are checked, so a hash stays known for two full rotations of the newer set.
        if (_current.Contains(hash) || _previous.Contains(hash))
        {
            Hits++;
            return true;
        }

        Misses++;
        return false;
    }

    /// <summary>
    /// Records that an asset exists.
    ///
    /// At capacity the older generation is recycled rather than the whole cache being emptied. The old
    /// clear-everything behaviour was a cliff: on the hit immediately after it, every tile in the next rescan
    /// missed, and a miss on this path is a filesystem existence probe on the capture thread. With two generations
    /// a hash is forgotten only after the newer set has rotated twice, and the memory footprint is unchanged
    /// because the capacity is split between them.
    /// </summary>
    public void Add(ulong hash)
    {
        if (_current.Count >= _segmentCapacity)
        {
            _previous.Clear();
            (_previous, _current) = (_current, _previous);
        }

        _current.Add(hash);
    }

    /// <summary>Drops the cache (used after pruning rewrites the asset store).</summary>
    public void Clear()
    {
        _current.Clear();
        _previous.Clear();
    }

    /// <summary>
    /// Records that an asset exists without the rotation a hot-path add would do, and reports whether the hash
    /// was new to the cache.
    ///
    /// Fills the current generation, then the older one, and stops at <see cref="Capacity"/>: anything more would
    /// only be thrown away by the next <see cref="Add"/> rotation, exactly what happened to half the manifest seed
    /// before this cap existed. Idempotent — seeding the same hash twice costs one set lookup, never capacity.
    /// </summary>
    public bool AddIfNew(ulong hash)
    {
        if (_current.Contains(hash) || _previous.Contains(hash))
        {
            return false;
        }

        if (Count >= Capacity)
        {
            return false;
        }

        if (_current.Count >= _segmentCapacity)
        {
            _previous.Add(hash);
            return true;
        }

        _current.Add(hash);
        return true;
    }
}
