using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    /// <summary>Copies a tile out of the (possibly strided, possibly DPI-scaled) frame buffer.</summary>
    private static void CopyTile(SourceFrame frame, int x, int y, int width, int height, Span<byte> destination)
    {
        int scaleX = Math.Max(1, frame.Width / Math.Max(1, frame.Monitor.Width));
        int scaleY = Math.Max(1, frame.Height / Math.Max(1, frame.Monitor.Height));
        int sourceX = x * scaleX;
        int sourceY = y * scaleY;
        int sourceWidth = width * scaleX;
        int sourceRowBytes = sourceWidth * 4;
        int destinationRowBytes = width * 4;

        for (int row = 0; row < height; row++)
        {
            int sourceOffset = ((sourceY + row) * frame.Pitch) + (sourceX * 4);
            Span<byte> target = destination[(row * destinationRowBytes)..];

            if (scaleX == 1)
            {
                frame.Pixels.AsSpan(sourceOffset, sourceRowBytes).CopyTo(target);
                continue;
            }

            // DPI-scaled surface: nearest-neighbour sample down to the logical tile width.
            for (int column = 0; column < width; column++)
            {
                int sample = sourceOffset + ((column * sourceWidth / width) * 4);
                frame.Pixels.AsSpan(sample, 4).CopyTo(target[(column * 4)..]);
            }
        }
    }

    /// <summary>Canvas for a monitor: dense tile map of the hash currently on screen for each tile.</summary>
    private ulong[] GetCanvas(MonitorInfo monitor)
    {
        if (_canvas.TryGetValue(monitor.Id, out ulong[]? canvas) && canvas.Length == monitor.TileCount)
        {
            return canvas;
        }

        canvas = new ulong[Math.Max(monitor.TileCount, 1)];
        _canvas[monitor.Id] = canvas;
        _forceFullRescan = true;
        return canvas;
    }

    /// <summary>Writes a full ground-truth frame dump (test-only mode, spec 12).</summary>
    private void WriteGroundTruth(SourceFrame frame, long timestampUs)
    {
        string path = Path.Combine(
            _session.GroundTruthDir,
            GroundTruthFrame.FileNameFor(timestampUs, frame.Monitor.Id));

        GroundTruthFrame.Write(
            path,
            frame.Monitor.Id,
            frame.Monitor.X,
            frame.Monitor.Y,
            frame.Width,
            frame.Height,
            frame.Pixels.AsSpan(0, frame.Pitch * frame.Height));
    }
}
