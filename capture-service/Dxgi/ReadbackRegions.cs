using ScreenRecall.CaptureService.Capture;
using ScreenRecall.Storage;
using Vortice.DXGI;

namespace ScreenRecall.CaptureService.Dxgi;

/// <summary>
/// Decides which parts of the desktop surface the CPU actually needs this frame.
///
/// The point of the exercise (spec 5.1: "never process a full frame when only a corner of it changed") is
/// defeated if the readback copies the whole surface anyway, so the region list DXGI reports is turned into a
/// small set of tile-aligned rectangles and only those are copied out of the GPU.
///
/// Tile alignment is not cosmetic. The engine hashes whole tiles, so a region covering only the dirty *pixels*
/// of an edge tile would leave the rest of that tile holding older pixels, and the hash would describe a
/// mixture of two frames - which would then be logged as that tile's current content. Snapping every region
/// outward to the tile grid means the pixels read back always cover the tiles about to be hashed, exactly the
/// set <see cref="TileCellSet.AddRect"/> produces for the same rects.
/// </summary>
internal static class ReadbackRegions
{
    /// <summary>Above this many regions, one bounding box is cheaper than the calls it saves.</summary>
    internal const int MaxRegions = 8;

    /// <summary>Above this share of the surface, copying the whole frame is the simpler and faster path.</summary>
    internal const double MaxCoverageFraction = 0.5;

    /// <summary>
    /// Fills <paramref name="regions"/> with the surface rectangles to read back and returns true when a partial
    /// readback is worthwhile. False means "take the whole surface": a pending full rescan, an unexpected
    /// geometry, a scaled surface, or simply too much of the screen being dirty.
    /// </summary>
    internal static bool TryPlan(
        Vortice.RawRect[] dirtyRects,
        int dirtyCount,
        OutduplMoveRect[] moveRects,
        int moveCount,
        MonitorInfo monitor,
        int surfaceWidth,
        int surfaceHeight,
        bool fullFrameRequired,
        List<IntRect> regions)
    {
        regions.Clear();
        if (surfaceWidth <= 0 || surfaceHeight <= 0 || (dirtyCount == 0 && moveCount == 0))
        {
            return false;
        }

        // The engine is about to rescan every tile, so it needs every pixel: a partial readback here would
        // leave the untouched tiles holding pixels from an older frame.
        if (fullFrameRequired)
        {
            return false;
        }

        // Tile boundaries line up with surface pixels only at scale 1. A DPI-scaled surface keeps the
        // whole-surface path: it is rare, and reading back more than needed is cheaper than reasoning about two
        // coordinate systems on the hot path.
        if (surfaceWidth != monitor.Width || surfaceHeight != monitor.Height)
        {
            return false;
        }

        long covered = 0;
        for (int i = 0; i < dirtyCount; i++)
        {
            Vortice.RawRect rect = dirtyRects[i];
            if (!AddTileAligned(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                    monitor, regions, ref covered, surfaceWidth, surfaceHeight))
            {
                regions.Clear();
                return false;
            }
        }

        for (int i = 0; i < moveCount; i++)
        {
            OutduplMoveRect move = moveRects[i];
            int width = move.DestinationRect.Right - move.DestinationRect.Left;
            int height = move.DestinationRect.Bottom - move.DestinationRect.Top;

            // A move touches two places: where the content landed, and where it left from. The engine turns
            // both into dirty rects, so both have to be read back.
            if (!AddTileAligned(move.DestinationRect.Left, move.DestinationRect.Top, width, height,
                    monitor, regions, ref covered, surfaceWidth, surfaceHeight)
                || !AddTileAligned(move.SourcePoint.X, move.SourcePoint.Y, width, height,
                    monitor, regions, ref covered, surfaceWidth, surfaceHeight))
            {
                regions.Clear();
                return false;
            }
        }

        if (regions.Count == 0)
        {
            return false;
        }

        long surfaceArea = (long)surfaceWidth * surfaceHeight;
        if (regions.Count <= MaxRegions && covered <= surfaceArea * MaxCoverageFraction)
        {
            return true;
        }

        // Too many pieces, or too much of the screen dirty: one bounding box - unless even that box is most of
        // the surface, in which case the plain full copy wins.
        IntRect box = BoundingBox(regions);
        if ((long)box.Width * box.Height > surfaceArea * MaxCoverageFraction)
        {
            regions.Clear();
            return false;
        }

        regions.Clear();
        regions.Add(box);
        return true;
    }

    /// <summary>Expands a surface rect outward to whole tiles and records it. False means "use the full surface".</summary>
    private static bool AddTileAligned(
        int x,
        int y,
        int width,
        int height,
        MonitorInfo monitor,
        List<IntRect> regions,
        ref long covered,
        int surfaceWidth,
        int surfaceHeight)
    {
        if (width <= 0 || height <= 0)
        {
            return true; // degenerate rect: nothing to read, nothing to get wrong
        }

        if (x < 0 || y < 0 || x + width > surfaceWidth || y + height > surfaceHeight)
        {
            // DXGI should never report a rect outside the surface. If it does, the geometry is not what this
            // planner assumes, so take the path that cannot be wrong.
            return false;
        }

        // Mirrors TileCellSet.AddRect exactly, so the pixels read back always cover the tiles about to be hashed.
        TileGrid.CellRange(monitor.X + x, width, monitor.TileSize, out int cellX0, out int cellX1);
        TileGrid.CellRange(monitor.Y + y, height, monitor.TileSize, out int cellY0, out int cellY1);

        int left = Math.Max(0, (cellX0 * monitor.TileSize) - monitor.X);
        int top = Math.Max(0, (cellY0 * monitor.TileSize) - monitor.Y);
        int right = Math.Min(monitor.Width, ((cellX1 + 1) * monitor.TileSize) - monitor.X);
        int bottom = Math.Min(monitor.Height, ((cellY1 + 1) * monitor.TileSize) - monitor.Y);
        if (right <= left || bottom <= top)
        {
            return true;
        }

        regions.Add(new IntRect(left, top, right - left, bottom - top));

        // Overlapping regions are counted more than once on purpose: this figure only decides whether a partial
        // readback is still worth it, and over-counting errs towards copying the whole surface.
        covered += (long)(right - left) * (bottom - top);
        return true;
    }

    private static IntRect BoundingBox(List<IntRect> regions)
    {
        int left = int.MaxValue;
        int top = int.MaxValue;
        int right = int.MinValue;
        int bottom = int.MinValue;
        foreach (IntRect region in regions)
        {
            left = Math.Min(left, region.X);
            top = Math.Min(top, region.Y);
            right = Math.Max(right, region.Right);
            bottom = Math.Max(bottom, region.Bottom);
        }

        return new IntRect(left, top, Math.Max(0, right - left), Math.Max(0, bottom - top));
    }
}
