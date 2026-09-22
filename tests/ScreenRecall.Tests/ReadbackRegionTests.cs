using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.Storage;
using Vortice.DXGI;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// A partial readback is only correct while the pixels it leaves behind are never hashed. These tests pin that
/// down: every region has to cover at least the tiles the engine will hash for the same rects, and anything the
/// planner is unsure about has to fall back to the whole surface rather than risk hashing a tile assembled from
/// two different frames.
/// </summary>
public sealed class ReadbackRegionTests
{
    private static MonitorInfo TestMonitor => new(0, "test", 0, 0, 256, 256, 64);

    /// <summary>
    /// RawRect's fields are readonly and it has no public constructor, so rectangles go through the
    /// implicit System.Drawing conversion the type itself offers.
    /// </summary>
    private static Vortice.RawRect Rect(int x, int y, int width, int height)
        => new System.Drawing.Rectangle(x, y, width, height);

    private static OutduplMoveRect Move(int destinationX, int destinationY, int width, int height)
    {
        // SourcePoint defaults to (0, 0): the moved band vacates the top-left tile, which is exactly the case the
        // planner must cover alongside the destination.
        OutduplMoveRect move = default;
        move.DestinationRect = Rect(destinationX, destinationY, width, height);
        return move;
    }

    [Fact]
    public void ADirtyPatchReadsBackOnlyTheTilesItTouches()
    {
        List<IntRect> regions = new();
        bool partial = ReadbackRegions.TryPlan(
            new[] { Rect(70, 70, 10, 10) },
            1,
            Array.Empty<OutduplMoveRect>(),
            0,
            TestMonitor,
            256,
            256,
            fullFrameRequired: false,
            regions);

        Assert.True(partial);
        // Pixels 70..80 land in cell 1 (64..128), so the region is snapped outward to that whole tile.
        IntRect region = Assert.Single(regions);
        Assert.Equal(64, region.X);
        Assert.Equal(64, region.Y);
        Assert.Equal(64, region.Width);
        Assert.Equal(64, region.Height);
    }

    [Fact]
    public void EveryRegionCoversTheTilesTheEngineWillHash()
    {
        MonitorInfo monitor = TestMonitor;
        Vortice.RawRect[] rects = { Rect(3, 3, 5, 5), Rect(120, 190, 30, 40) };
        List<IntRect> regions = new();

        Assert.True(ReadbackRegions.TryPlan(
            rects, rects.Length, Array.Empty<OutduplMoveRect>(), 0, monitor, 256, 256, false, regions));

        // The engine turns the same rects into cells; each of those cells has to be inside a readback region, or a
        // tile would be hashed from pixels of an earlier frame.
        TileCellSet cells = new();
        foreach (Vortice.RawRect rect in rects)
        {
            cells.AddRect(monitor, new IntRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top));
        }

        Assert.True(cells.Count > 0);
        foreach (long packed in cells.Packed)
        {
            (int cellX, int cellY) = TileCellSet.Unpack(packed);
            monitor.TilePixelRect(cellX, cellY, out int x, out int y, out int width, out int height);
            Assert.Contains(regions, region => region.X <= x
                                                && region.Y <= y
                                                && region.Right >= x + width
                                                && region.Bottom >= y + height);
        }
    }

    [Fact]
    public void APendingRescanTakesTheWholeSurface()
    {
        List<IntRect> regions = new();
        bool partial = ReadbackRegions.TryPlan(
            new[] { Rect(0, 0, 8, 8) },
            1,
            Array.Empty<OutduplMoveRect>(),
            0,
            TestMonitor,
            256,
            256,
            fullFrameRequired: true,
            regions);

        Assert.False(partial);
        Assert.Empty(regions);
    }

    [Fact]
    public void ChangesAcrossTheWholeScreenTakeTheWholeSurface()
    {
        List<IntRect> regions = new();
        bool partial = ReadbackRegions.TryPlan(
            new[] { Rect(0, 0, 256, 256) },
            1,
            Array.Empty<OutduplMoveRect>(),
            0,
            TestMonitor,
            256,
            256,
            false,
            regions);

        Assert.False(partial);
    }

    [Fact]
    public void ADpiScaledSurfaceTakesTheWholeSurface()
    {
        // The surface is bigger than the monitor, so tile boundaries and surface pixels do not line up: the planner
        // declines rather than reasoning about two coordinate systems on the hot path.
        List<IntRect> regions = new();
        bool partial = ReadbackRegions.TryPlan(
            new[] { Rect(0, 0, 8, 8) },
            1,
            Array.Empty<OutduplMoveRect>(),
            0,
            TestMonitor,
            512,
            512,
            false,
            regions);

        Assert.False(partial);
    }

    [Fact]
    public void ARectOutsideTheSurfaceTakesTheWholeSurface()
    {
        List<IntRect> regions = new();
        bool partial = ReadbackRegions.TryPlan(
            new[] { Rect(250, 250, 40, 40) },
            1,
            Array.Empty<OutduplMoveRect>(),
            0,
            TestMonitor,
            256,
            256,
            false,
            regions);

        Assert.False(partial);
    }

    [Fact]
    public void AMoveReadsBackBothWhereContentLeftAndWhereItLanded()
    {
        // A move is the classic scroll: the destination receives content and the source is vacated, and the engine
        // dirty-rects both ends, so both have to be read back.
        OutduplMoveRect move = Move(64, 128, 64, 64);

        List<IntRect> regions = new();
        bool partial = ReadbackRegions.TryPlan(
            Array.Empty<Vortice.RawRect>(),
            0,
            new[] { move },
            1,
            TestMonitor,
            256,
            256,
            false,
            regions);

        Assert.True(partial);
        Assert.Equal(2, regions.Count);
        Assert.Contains(regions, region => region.X == 64 && region.Y == 128);
        Assert.Contains(regions, region => region.X == 0 && region.Y == 0);
    }

    [Fact]
    public void NoRectAtAllTakesTheWholeSurface()
    {
        // A present with no region list is DXGI saying "something changed and I am not saying what": that is a full
        // rescan, not an empty frame.
        List<IntRect> regions = new();
        bool partial = ReadbackRegions.TryPlan(
            Array.Empty<Vortice.RawRect>(),
            0,
            Array.Empty<OutduplMoveRect>(),
            0,
            TestMonitor,
            256,
            256,
            false,
            regions);

        Assert.False(partial);
    }
}
