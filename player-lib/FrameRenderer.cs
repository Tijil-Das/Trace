using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>A reconstructed monitor frame: tightly packed BGRA pixels.</summary>
public sealed record RenderedFrame(ushort MonitorId, int X, int Y, int Width, int Height, byte[] Bgra)
{
    /// <summary>Bytes per row.</summary>
    public int Stride => Width * 4;
}

/// <summary>
/// Turns a canvas into pixels (spec 7). Tile blits only — no bitstream decode — with tiles fetched
/// from the decode cache and blitted in parallel across tile rows, which is what makes scrubbing and
/// faster-than-real-time playback cheap on any modern machine.
/// </summary>
public sealed class FrameRenderer
{
    private readonly TileCache _cache;

    public FrameRenderer(AssetStore store, TileCache? cache = null)
    {
        _cache = cache ?? new TileCache(store);
    }

    /// <summary>Decode cache backing this renderer.</summary>
    public TileCache Cache => _cache;

    /// <summary>Renders one monitor's canvas state.</summary>
    public RenderedFrame Render(ScreenCanvas canvas, MonitorInfo monitor)
    {
        byte[] buffer = new byte[Math.Max(1, monitor.Width * monitor.Height * 4)];
        int stride = monitor.Width * 4;

        Parallel.For(0, monitor.Rows, row =>
        {
            int cellY = monitor.OriginCellY + row;
            for (int column = 0; column < monitor.Columns; column++)
            {
                int cellX = monitor.OriginCellX + column;
                ulong hash = canvas.TileAt(monitor.Id, cellX, cellY);
                if (hash == TileHash.None)
                {
                    continue;
                }

                TileBitmap? tile = _cache.Get(hash);
                if (tile is null)
                {
                    continue; // missing asset: leave a hole rather than failing the whole frame
                }

                monitor.TilePixelRect(cellX, cellY, out int x, out int y, out int width, out int height);
                Blit(tile, buffer, stride, x, y, width, height);
            }
        });

        return new RenderedFrame(monitor.Id, monitor.X, monitor.Y, monitor.Width, monitor.Height, buffer);
    }

    /// <summary>Renders every monitor and composes them into one virtual-desktop frame.</summary>
    public RenderedFrame RenderVirtualDesktop(ScreenCanvas canvas)
    {
        List<RenderedFrame> frames = canvas.Monitors.Select(monitor => Render(canvas, monitor)).ToList();
        if (frames.Count == 0)
        {
            return new RenderedFrame(0, 0, 0, 1, 1, new byte[4]);
        }

        int left = frames.Min(frame => frame.X);
        int top = frames.Min(frame => frame.Y);
        int right = frames.Max(frame => frame.X + frame.Width);
        int bottom = frames.Max(frame => frame.Y + frame.Height);
        int width = Math.Max(1, right - left);
        int height = Math.Max(1, bottom - top);
        byte[] composite = new byte[width * height * 4];
        int compositeStride = width * 4;

        foreach (RenderedFrame frame in frames)
        {
            int offsetX = frame.X - left;
            int offsetY = frame.Y - top;
            for (int row = 0; row < frame.Height; row++)
            {
                int sourceOffset = row * frame.Stride;
                int destinationOffset = ((offsetY + row) * compositeStride) + (offsetX * 4);
                frame.Bgra.AsSpan(sourceOffset, frame.Stride).CopyTo(composite.AsSpan(destinationOffset));
            }
        }

        return new RenderedFrame(0, left, top, width, height, composite);
    }

    /// <summary>Copies the intersecting part of a tile into the frame buffer.</summary>
    private static void Blit(TileBitmap tile, byte[] destination, int destinationStride, int x, int y, int width, int height)
    {
        int copyWidth = Math.Min(width, tile.Width);
        int copyHeight = Math.Min(height, tile.Height);
        int copyBytes = copyWidth * 4;
        for (int row = 0; row < copyHeight; row++)
        {
            int sourceOffset = row * tile.Stride;
            int destinationOffset = ((y + row) * destinationStride) + (x * 4);
            tile.Bgra.AsSpan(sourceOffset, copyBytes).CopyTo(destination.AsSpan(destinationOffset));
        }
    }
}
