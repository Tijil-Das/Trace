namespace ScreenRecall.Storage;

/// <summary>
/// Geometry of a capture target (one DXGI output / monitor). Tiles are anchored to the absolute
/// virtual-desktop grid (spec 5.2), so a monitor whose origin is not a multiple of the tile size -
/// which is the normal case in a multi-monitor setup - still shares tile boundaries with its
/// neighbours, and the same visual element hashes identically on any day.
/// </summary>
public sealed record MonitorInfo(
    ushort Id,
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    int TileSize)
{
    /// <summary>Absolute grid column of this monitor's left edge.</summary>
    public int OriginCellX => TileGrid.CellOf(X, TileSize);

    /// <summary>Absolute grid row of this monitor's top edge.</summary>
    public int OriginCellY => TileGrid.CellOf(Y, TileSize);

    /// <summary>Number of tile columns covering this monitor.</summary>
    public int Columns => TileGrid.CellsAcross(X, Width, TileSize);

    /// <summary>Number of tile rows covering this monitor.</summary>
    public int Rows => TileGrid.CellsAcross(Y, Height, TileSize);

    /// <summary>Total tile slots, including partially covered edge tiles.</summary>
    public int TileCount => Columns * Rows;

    /// <summary>Dense index of an absolute grid cell, or -1 when the cell is outside this monitor.</summary>
    public int TileIndex(int absoluteCellX, int absoluteCellY)
    {
        int column = absoluteCellX - OriginCellX;
        int row = absoluteCellY - OriginCellY;
        if (column < 0 || row < 0 || column >= Columns || row >= Rows)
        {
            return -1;
        }

        return (row * Columns) + column;
    }

    /// <summary>Inverse of <see cref="TileIndex"/>: absolute grid cell of a dense tile index.</summary>
    public (int CellX, int CellY) CellOf(int tileIndex)
        => (OriginCellX + (tileIndex % Columns), OriginCellY + (tileIndex / Columns));

    /// <summary>
    /// Clipped pixel rect of an absolute grid cell inside this monitor, in monitor-local coordinates.
    /// Edge cells are partial; w or h &lt;= 0 means the cell does not intersect the monitor at all.
    /// </summary>
    public void TilePixelRect(int absoluteCellX, int absoluteCellY, out int x, out int y, out int w, out int h)
    {
        int left = (absoluteCellX * TileSize) - X;
        int top = (absoluteCellY * TileSize) - Y;
        int startX = Math.Max(left, 0);
        int startY = Math.Max(top, 0);
        int endX = Math.Min(left + TileSize, Width);
        int endY = Math.Min(top + TileSize, Height);
        x = startX;
        y = startY;
        w = endX - startX;
        h = endY - startY;
    }

    /// <summary>Absolute virtual-desktop pixel origin of a grid cell.</summary>
    public static (int X, int Y) CellScreenOrigin(int absoluteCellX, int absoluteCellY, int tileSize)
        => (absoluteCellX * tileSize, absoluteCellY * tileSize);

    /// <summary>True when another monitor has identical geometry and tiling.</summary>
    public bool SameGeometry(MonitorInfo other)
        => Id == other.Id && X == other.X && Y == other.Y && Width == other.Width
           && Height == other.Height && TileSize == other.TileSize;

    /// <summary>Number of tiles in a 64px grid for this monitor's pixel size.</summary>
    public static int ExpectedTiles(int width, int height, int tileSize)
        => TileGrid.CellsAcross(0, width, tileSize) * TileGrid.CellsAcross(0, height, tileSize);
}

