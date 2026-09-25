using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>
/// The reconstruction surface (spec 7): a per-monitor map of grid cell to asset hash. Replaying log
/// entries against it is what playback is — tile blits, never bitstream decoding — and a checkpoint is
/// simply a serialized copy of it.
/// </summary>
public sealed class ScreenCanvas
{
    private readonly Dictionary<ushort, ulong[]> _tiles = new();
    private readonly Dictionary<ushort, MonitorInfo> _monitors = new();

    /// <summary>Monitors currently laid out on the canvas.</summary>
    public IReadOnlyCollection<MonitorInfo> Monitors => _monitors.Values;

    /// <summary>Number of non-empty tiles across all monitors.</summary>
    public int NonEmptyTiles
    {
        get
        {
            int total = 0;
            foreach (ulong[] tiles in _tiles.Values)
            {
                foreach (ulong hash in tiles)
                {
                    if (hash != TileHash.None)
                    {
                        total++;
                    }
                }
            }

            return total;
        }
    }

    /// <summary>Registers (or re-registers) a monitor's geometry, preserving tile state when it matches.</summary>
    public void SetMonitor(MonitorInfo monitor)
    {
        if (_monitors.TryGetValue(monitor.Id, out MonitorInfo? existing) && existing.SameGeometry(monitor))
        {
            return;
        }

        bool preserve = _monitors.TryGetValue(monitor.Id, out MonitorInfo? previous)
                        && _tiles.TryGetValue(monitor.Id, out ulong[]? old)
                        && previous.Columns == monitor.Columns
                        && previous.Rows == monitor.Rows;

        if (preserve)
        {
            _monitors[monitor.Id] = monitor;
            return;
        }

        _monitors[monitor.Id] = monitor;
        _tiles[monitor.Id] = new ulong[Math.Max(monitor.TileCount, 1)];
    }

    /// <summary>Geometry of a monitor, or null when it is not part of this canvas.</summary>
    public MonitorInfo? Monitor(ushort monitorId)
        => _monitors.TryGetValue(monitorId, out MonitorInfo? monitor) ? monitor : null;

    /// <summary>Hash currently displayed for a grid cell (0 = empty).</summary>
    public ulong TileAt(ushort monitorId, int cellX, int cellY)
    {
        if (!_monitors.TryGetValue(monitorId, out MonitorInfo? monitor)
            || !_tiles.TryGetValue(monitorId, out ulong[]? tiles))
        {
            return TileHash.None;
        }

        int index = monitor.TileIndex(cellX, cellY);
        return index >= 0 && index < tiles.Length ? tiles[index] : TileHash.None;
    }

    /// <summary>Applies one log entry to the canvas. Entries that are not about tiles are ignored.</summary>
    public void Apply(in LogEntry entry)
    {
        // Ops that are not tile state (heartbeats, pointer moves) ride in the same 27-byte record and must never
        // reach the tile map: an unknown op is not a draw of whatever happens to be in its fields.
        if (entry.Op is not (LogOp.Draw or LogOp.Move or LogOp.Clear))
        {
            return;
        }

        if (!_tiles.TryGetValue(entry.MonitorId, out ulong[]? tiles)
            || !_monitors.TryGetValue(entry.MonitorId, out MonitorInfo? monitor))
        {
            return;
        }

        int index = monitor.TileIndex(entry.TileX, entry.TileY);
        if (index < 0 || index >= tiles.Length)
        {
            return;
        }

        tiles[index] = entry.Op == LogOp.Clear ? TileHash.None : entry.AssetHash;
    }

    /// <summary>Replaces all tile state from a checkpoint.</summary>
    public void Load(CheckpointData checkpoint)
    {
        foreach (CheckpointMonitorState state in checkpoint.Monitors)
        {
            SetMonitor(state.Monitor);
            if (_tiles.TryGetValue(state.Monitor.Id, out ulong[]? tiles) && tiles.Length == state.Tiles.Length)
            {
                Array.Copy(state.Tiles, tiles, tiles.Length);
            }
        }
    }

    /// <summary>Clears all monitors and tile state.</summary>
    public void Reset()
    {
        _tiles.Clear();
        _monitors.Clear();
    }

    /// <summary>Snapshot of one monitor's tile map (used to write checkpoints).</summary>
    public ulong[]? SnapshotTiles(ushort monitorId)
        => _tiles.TryGetValue(monitorId, out ulong[]? tiles) ? tiles : null;

    /// <summary>All hashes currently on the canvas.</summary>
    public IEnumerable<ulong> Hashes()
    {
        foreach (ulong[] tiles in _tiles.Values)
        {
            foreach (ulong hash in tiles)
            {
                if (hash != TileHash.None)
                {
                    yield return hash;
                }
            }
        }
    }
}
