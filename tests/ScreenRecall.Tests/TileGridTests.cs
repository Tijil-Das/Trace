using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>Tile grid alignment and tile hashing — the rules the whole dedupe scheme rests on.</summary>
public sealed class TileGridTests
{
    [Fact]
    public void TilesAreAlignedToTheAbsoluteVirtualDesktop()
    {
        // Two monitors side by side, the second starting mid-cell. A tile at the shared boundary must
        // be the same grid cell for both, which is what makes content hash identically across monitors
        // and across days.
        MonitorInfo left = new(0, "left", 0, 0, 1366, 768, 64);
        MonitorInfo right = new(1, "right", 1366, 0, 1366, 768, 64);

        Assert.Equal(0, left.OriginCellX);
        Assert.Equal(21, right.OriginCellX); // 1366 / 64 = 21.34 -> cell 21
        Assert.Equal(22, left.Columns);
        Assert.Equal(22, right.Columns);
        Assert.Equal(12, left.Rows);

        // The cell that straddles the seam is in range for both monitors.
        Assert.True(left.TileIndex(21, 0) >= 0);
        Assert.True(right.TileIndex(21, 0) >= 0);
        Assert.Equal(-1, right.TileIndex(20, 0)); // before this monitor's first column
    }

    [Fact]
    public void NegativeOriginsWorkForMonitorsLeftOfThePrimary()
    {
        MonitorInfo monitor = new(2, "left-of-primary", -1920, 0, 1920, 1080, 64);
        Assert.Equal(-30, monitor.OriginCellX);
        Assert.Equal(30, monitor.Columns);
        Assert.Equal(0, monitor.TileIndex(-30, 0));
        Assert.Equal(509, monitor.TileIndex(-1, 16)); // (row 16 * 30 columns) + last column

        monitor.TilePixelRect(-30, 0, out int x, out int y, out int w, out int h);
        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(64, w);
        Assert.Equal(64, h);
    }

    [Fact]
    public void TileIndexAndCellOfAreInverses()
    {
        MonitorInfo monitor = new(1, "test", 1366, 0, 1366, 768, 64);
        for (int index = 0; index < monitor.TileCount; index++)
        {
            (int cellX, int cellY) = monitor.CellOf(index);
            Assert.Equal(index, monitor.TileIndex(cellX, cellY));
        }
    }

    [Fact]
    public void HashIsStableAndSensitiveToDimensions()
    {
        byte[] tile = new byte[64 * 64 * 4];
        for (int i = 0; i < tile.Length; i++)
        {
            tile[i] = (byte)(i % 251);
        }

        ulong a = TileHash.Compute(tile, 64, 64);
        ulong b = TileHash.Compute(tile, 64, 64);
        Assert.Equal(a, b);

        // A 22px-wide edge tile sharing the same pixel prefix must not collide with the full tile.
        byte[] partial = new byte[22 * 64 * 4];
        for (int row = 0; row < 64; row++)
        {
            Array.Copy(tile, row * 64 * 4, partial, row * 22 * 4, 22 * 4);
        }

        Assert.NotEqual(a, TileHash.Compute(partial, 22, 64));

        // Zero tiles are legal content but must not collide with the "no asset" sentinel.
        Assert.NotEqual(TileHash.None, TileHash.Compute(new byte[64 * 64 * 4], 64, 64));
    }

    [Fact]
    public void CellSetDeduplicatesOverlappingRects()
    {
        MonitorInfo monitor = new(0, "test", 0, 0, 256, 256, 64);
        CaptureService.Capture.TileCellSet cells = new();

        cells.AddRect(monitor, new CaptureService.Capture.IntRect(0, 0, 100, 100));
        cells.AddRect(monitor, new CaptureService.Capture.IntRect(50, 50, 100, 100));
        cells.AddRect(monitor, new CaptureService.Capture.IntRect(64, 64, 64, 64));

        // Cells covering (0,0)-(150,150): rows 0..2, columns 0..2 = 3x3 = 9 distinct cells.
        Assert.Equal(9, cells.Count);
    }
}
