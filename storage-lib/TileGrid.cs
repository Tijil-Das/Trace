namespace ScreenRecall.Storage;

/// <summary>
/// Grid-aligned tile arithmetic (spec 5.2). Tiles are aligned to absolute screen coordinates and
/// never to a dirty rect: that alignment is what makes an icon sitting in the same screen position
/// hash identically on two different days and dedupe, instead of missing by a few pixels of offset.
/// </summary>
public static class TileGrid
{
    /// <summary>Default tile edge length in pixels.</summary>
    public const int DefaultTileSize = 64;

    /// <summary>Floored integer division (C# truncates toward zero, tiles need true floor for negative origins).</summary>
    public static int FloorDiv(int value, int divisor) => value >= 0 ? value / divisor : ~(~value / divisor);

    /// <summary>Grid cell index containing the given pixel coordinate.</summary>
    public static int CellOf(int pixel, int tileSize = DefaultTileSize) => FloorDiv(pixel, tileSize);

    /// <summary>Pixel origin (top-left) of a grid cell.</summary>
    public static int OriginOf(int cell, int tileSize = DefaultTileSize) => cell * tileSize;

    /// <summary>Inclusive cell range covering the pixel span [x, x + width).</summary>
    public static void CellRange(int x, int width, int tileSize, out int first, out int last)
    {
        if (width <= 0)
        {
            first = 0;
            last = -1;
            return;
        }

        first = CellOf(x, tileSize);
        last = CellOf(x + width - 1, tileSize);
    }

    /// <summary>Number of grid cells covering the pixel span [x, x + width).</summary>
    public static int CellsAcross(int x, int width, int tileSize)
    {
        CellRange(x, width, tileSize, out int first, out int last);
        return last - first + 1;
    }

    /// <summary>
    /// Clips a tile to the monitor pixel bounds. Edge tiles can be partial (e.g. 1366px wide screen
    /// with 64px tiles leaves a 22px tile on the right); w or h &lt;= 0 means fully off-screen.
    /// </summary>
    public static void ClipTile(
        int cellX,
        int cellY,
        int tileSize,
        int monitorWidth,
        int monitorHeight,
        out int x,
        out int y,
        out int w,
        out int h)
    {
        int tileX = OriginOf(cellX, tileSize);
        int tileY = OriginOf(cellY, tileSize);
        x = Math.Clamp(tileX, 0, monitorWidth);
        y = Math.Clamp(tileY, 0, monitorHeight);
        int right = Math.Clamp(tileX + tileSize, 0, monitorWidth);
        int bottom = Math.Clamp(tileY + tileSize, 0, monitorHeight);
        w = right - x;
        h = bottom - y;
    }

    /// <summary>Visitor invoked for each tile of a pixel rect.</summary>
    public delegate void TileVisitor(int cellX, int cellY);

    /// <summary>Enumerates every grid cell overlapping a pixel rect, clipped to the monitor bounds.</summary>
    public static void ForEachTile(
        int x,
        int y,
        int width,
        int height,
        int tileSize,
        int monitorWidth,
        int monitorHeight,
        TileVisitor visitor)
    {
        int left = Math.Clamp(x, 0, monitorWidth);
        int top = Math.Clamp(y, 0, monitorHeight);
        int right = Math.Clamp(x + width, 0, monitorWidth);
        int bottom = Math.Clamp(y + height, 0, monitorHeight);
        if (right <= left || bottom <= top)
        {
            return;
        }

        CellRange(left, right - left, tileSize, out int cx0, out int cx1);
        CellRange(top, bottom - top, tileSize, out int cy0, out int cy1);
        for (int cy = cy0; cy <= cy1; cy++)
        {
            for (int cx = cx0; cx <= cx1; cx++)
            {
                visitor(cx, cy);
            }
        }
    }
}
