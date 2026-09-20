using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// Deduplicated set of absolute grid cells touched by one frame. Dirty rects, move-rect sources and
/// move-rect destinations all collapse into this set, so a tile covered by three overlapping rects is
/// processed (hashed, deduped, logged) exactly once. Reused across frames to keep the steady-state
/// path allocation-free.
/// </summary>
internal sealed class TileCellSet
{
    private readonly HashSet<long> _seen = new(4096);
    private readonly List<long> _cells = new(4096);

    /// <summary>Number of distinct cells in the set.</summary>
    internal int Count => _cells.Count;

    /// <summary>Distinct cells as (cellX, cellY) pairs in insertion order.</summary>
    internal IReadOnlyList<long> Packed => _cells;

    /// <summary>Clears the set for the next frame.</summary>
    internal void Clear()
    {
        _seen.Clear();
        _cells.Clear();
    }

    /// <summary>Adds a cell, ignoring duplicates.</summary>
    internal bool Add(int cellX, int cellY)
    {
        long packed = Pack(cellX, cellY);
        if (!_seen.Add(packed))
        {
            return false;
        }

        _cells.Add(packed);
        return true;
    }

    /// <summary>Adds every grid cell overlapped by a monitor-local rect.</summary>
    internal void AddRect(MonitorInfo monitor, IntRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        int absoluteX = monitor.X + rect.X;
        int absoluteY = monitor.Y + rect.Y;
        TileGrid.CellRange(absoluteX, rect.Width, monitor.TileSize, out int cellX0, out int cellX1);
        TileGrid.CellRange(absoluteY, rect.Height, monitor.TileSize, out int cellY0, out int cellY1);

        for (int cellY = cellY0; cellY <= cellY1; cellY++)
        {
            for (int cellX = cellX0; cellX <= cellX1; cellX++)
            {
                if (monitor.TileIndex(cellX, cellY) >= 0)
                {
                    Add(cellX, cellY);
                }
            }
        }
    }

    /// <summary>Adds every cell of the monitor.</summary>
    internal void AddAll(MonitorInfo monitor)
    {
        for (int row = 0; row < monitor.Rows; row++)
        {
            for (int column = 0; column < monitor.Columns; column++)
            {
                Add(monitor.OriginCellX + column, monitor.OriginCellY + row);
            }
        }
    }

    internal static long Pack(int cellX, int cellY) => ((long)cellX << 32) | (uint)cellY;

    internal static (int CellX, int CellY) Unpack(long packed)
        => ((int)(packed >> 32), (int)(packed & 0xFFFFFFFF));
}
