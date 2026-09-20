using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>
/// Bounded, thread-safe cache of decoded tiles. Playback touches the same few hundred tiles over and
/// over (icons, window chrome, static text), and re-decoding them from disk on every seek would make
/// scrubbing as slow as the file system — which, on a machine with real-time scanning, is the slowest
/// part of the whole pipeline.
/// </summary>
public sealed class TileCache
{
    private readonly Dictionary<ulong, LinkedListNode<Entry>> _map = new();
    private readonly LinkedList<Entry> _order = new();
    private readonly object _sync = new();
    private readonly long _capacityBytes;
    private readonly AssetStore _store;
    private long _bytes;
    private long _hits;
    private long _misses;

    public TileCache(AssetStore store, long capacityBytes = 256L * 1024 * 1024)
    {
        _store = store;
        _capacityBytes = Math.Max(capacityBytes, 8L * 1024 * 1024);
    }

    /// <summary>Cache hits since construction.</summary>
    public long Hits => Interlocked.Read(ref _hits);

    /// <summary>Cache misses (tiles decoded from disk) since construction.</summary>
    public long Misses => Interlocked.Read(ref _misses);

    /// <summary>Bytes currently held by decoded tiles.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Number of cached tiles.</summary>
    public int Count
    {
        get
        {
            lock (_sync)
            {
                return _map.Count;
            }
        }
    }

    /// <summary>Returns a decoded tile, reading and decoding it on a miss; null when it is missing.</summary>
    public TileBitmap? Get(ulong hash)
    {
        if (hash == TileHash.None)
        {
            return null;
        }

        lock (_sync)
        {
            if (_map.TryGetValue(hash, out LinkedListNode<Entry>? node))
            {
                _order.Remove(node);
                _order.AddFirst(node);
                Interlocked.Increment(ref _hits);
                return node.Value.Tile;
            }
        }

        Interlocked.Increment(ref _misses);
        TileBitmap tile;
        try
        {
            tile = _store.TryLoadTile(hash);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            // A missing or damaged asset must not stop playback: the tile renders as a hole.
            return null;
        }

        Insert(hash, tile);
        return tile;
    }

    /// <summary>Drops every cached tile (after pruning, or when switching to a different store).</summary>
    public void Clear()
    {
        lock (_sync)
        {
            _map.Clear();
            _order.Clear();
            _bytes = 0;
        }
    }

    /// <summary>Pre-warms the cache for a set of hashes, in parallel.</summary>
    public void Prefetch(IEnumerable<ulong> hashes)
    {
        List<ulong> pending = hashes.Where(hash => hash != TileHash.None).Distinct().ToList();
        Parallel.ForEach(pending, hash => Get(hash));
    }

    private void Insert(ulong hash, TileBitmap tile)
    {
        lock (_sync)
        {
            if (_map.ContainsKey(hash))
            {
                return;
            }

            LinkedListNode<Entry> node = _order.AddFirst(new Entry(hash, tile));
            _map[hash] = node;
            _bytes += tile.Bgra.Length;

            while (_bytes > _capacityBytes && _order.Last is { } last)
            {
                _order.RemoveLast();
                _map.Remove(last.Value.Hash);
                _bytes -= last.Value.Tile.Bgra.Length;
            }
        }
    }

    private readonly record struct Entry(ulong Hash, TileBitmap Tile);
}
