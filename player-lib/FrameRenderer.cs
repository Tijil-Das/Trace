using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>A reconstructed monitor frame: tightly packed BGRA pixels.</summary>
public sealed record RenderedFrame(ushort MonitorId, int X, int Y, int Width, int Height, byte[] Bgra)
{
    /// <summary>Bytes per row.</summary>
    public int Stride => Width * 4;
}

/// <summary>
/// The mouse pointer as the recorder saw it: monitor-relative pixel position, the hotspot of the shape it was
/// drawn with, and the hash of that shape in the asset store. A pointer is state, not content — the pointer is
/// never part of the duplicated pixels, so replay draws it over the reconstructed screen instead.
/// </summary>
public sealed record PointerState(ushort MonitorId, int X, int Y, int HotspotX, int HotspotY, ulong ShapeHash);

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

    /// <summary>
    /// Draw the recorded mouse pointer over the picture. On by default: the pointer was on the screen, and a frame
    /// without it is a frame the user never saw. Turning it off is for comparisons and for exports that want the
    /// screen without a cursor in it.
    /// </summary>
    public bool IncludeCursor { get; set; } = true;

    /// <summary>
    /// Where the pointer is, as of the position being rendered. Set by the replay cursor as it applies log entries;
    /// null means the pointer was not visible.
    /// </summary>
    public PointerState? Pointer { get; set; }

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

        if (Pointer is { } pointer && pointer.MonitorId == monitor.Id)
        {
            // DXGI's own rule: the shape's top-left corner goes at the reported position, and the hot spot is not
            // applied when drawing (see DXGI_OUTDUPL_POINTER_SHAPE_INFO). The hot spot is still recorded, so the
            // other reading of that field stays a one-line change here.
            DrawPointer(buffer, stride, monitor.Width, monitor.Height, pointer.X, pointer.Y);
        }

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

        DrawPointerOverVirtualDesktop(canvas, composite, compositeStride, width, height, left, top);
        return new RenderedFrame(0, left, top, width, height, composite);
    }

    /// <summary>
    /// Renders every monitor into a caller-owned buffer, filling the same virtual-desktop rect that
    /// <see cref="RenderVirtualDesktop(ScreenCanvas)"/> would produce.
    /// </summary>
    /// <remarks>
    /// Playback calls this sixty times a second, and the allocating overload costs one monitor buffer plus a
    /// composite per call — megabytes of garbage per second, which is exactly the kind of garbage that turns
    /// into a GC pause in the middle of a scroll and reads to the user as lag. This one blits straight into
    /// the caller's buffer at the final offset: no intermediate composite, no per-monitor copy. Only the
    /// region about to be written is cleared, so a reused buffer never shows stale pixels from the frame
    /// before. Nothing here changes what is decoded — pixels still come from the same lossless tiles.
    ///
    /// The returned frame's <see cref="RenderedFrame.Bgra"/> is the whole buffer and may be longer than
    /// Width * Height * 4; consumers must use Width/Height/Stride and never Bgra.Length.
    /// </remarks>
    public RenderedFrame RenderVirtualDesktopInto(ScreenCanvas canvas, byte[] destination)
    {
        List<MonitorInfo> monitors = canvas.Monitors.ToList();
        if (monitors.Count == 0)
        {
            return new RenderedFrame(0, 0, 0, 1, 1, destination);
        }

        int left = monitors.Min(monitor => monitor.X);
        int top = monitors.Min(monitor => monitor.Y);
        int width = Math.Max(1, monitors.Max(monitor => monitor.X + monitor.Width) - left);
        int height = Math.Max(1, monitors.Max(monitor => monitor.Y + monitor.Height) - top);
        int needed = width * height * 4;
        if (destination.Length < needed)
        {
            throw new ArgumentException(
                $"Destination must hold at least {needed} bytes for a {width}x{height} frame.", nameof(destination));
        }

        Array.Clear(destination, 0, needed);

        foreach (MonitorInfo monitor in monitors)
        {
            RenderInto(canvas, monitor, destination, width * 4, monitor.X - left, monitor.Y - top);
        }

        DrawPointerOverVirtualDesktop(canvas, destination, width * 4, width, height, left, top);
        return new RenderedFrame(0, left, top, width, height, destination);
    }

    /// <summary>
    /// Places the pointer on a virtual-desktop frame: the pointer's own monitor says where that monitor's origin
    /// sits inside the composite, and the frame's own origin says where the composite starts.
    /// </summary>
    private void DrawPointerOverVirtualDesktop(
        ScreenCanvas canvas, byte[] destination, int stride, int width, int height, int frameLeft, int frameTop)
    {
        if (Pointer is not { } pointer || canvas.Monitor(pointer.MonitorId) is not { } monitor)
        {
            return;
        }

        DrawPointer(
            destination,
            stride,
            width,
            height,
            monitor.X - frameLeft + pointer.X,
            monitor.Y - frameTop + pointer.Y);
    }

    /// <summary>
    /// Blends the pointer shape into a finished frame, source-over. The shape is an ordinary small asset — encoded
    /// by whichever codec wrote the session, so it comes out of the same decode cache the tiles do — and a shape
    /// that never reached the store leaves the picture alone rather than drawing a guess.
    /// </summary>
    private void DrawPointer(byte[] destination, int stride, int width, int height, int x, int y)
    {
        if (!IncludeCursor || Pointer is not { } pointer)
        {
            return;
        }

        TileBitmap? shape = _cache.Get(pointer.ShapeHash);
        if (shape is null)
        {
            return;
        }

        for (int row = 0; row < shape.Height; row++)
        {
            int targetY = y + row;
            if (targetY < 0 || targetY >= height)
            {
                continue;
            }

            int sourceRow = row * shape.Stride;
            for (int column = 0; column < shape.Width; column++)
            {
                int source = sourceRow + (column * 4);
                byte alpha = shape.Bgra[source + 3];
                if (alpha == 0)
                {
                    continue;
                }

                int targetX = x + column;
                if (targetX < 0 || targetX >= width)
                {
                    continue;
                }

                int target = (targetY * stride) + (targetX * 4);
                if (alpha == 255)
                {
                    destination[target] = shape.Bgra[source];
                    destination[target + 1] = shape.Bgra[source + 1];
                    destination[target + 2] = shape.Bgra[source + 2];
                }
                else
                {
                    int inverse = 255 - alpha;
                    destination[target] = (byte)((shape.Bgra[source] * alpha + destination[target] * inverse + 127) / 255);
                    destination[target + 1] = (byte)((shape.Bgra[source + 1] * alpha + destination[target + 1] * inverse + 127) / 255);
                    destination[target + 2] = (byte)((shape.Bgra[source + 2] * alpha + destination[target + 2] * inverse + 127) / 255);
                }

                destination[target + 3] = 255;
            }
        }
    }

    /// <summary>Blits one monitor's canvas state into a larger buffer at a pixel offset.</summary>
    private void RenderInto(ScreenCanvas canvas, MonitorInfo monitor, byte[] destination, int destinationStride, int offsetX, int offsetY)
    {
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
                Blit(tile, destination, destinationStride, x + offsetX, y + offsetY, width, height);
            }
        });
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
