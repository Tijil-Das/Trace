using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.Storage;
using Vortice.DXGI;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>Translates a DXGI frame into the backend-independent frame the engine consumes.</summary>
internal static class FrameConverter
{
    internal static SourceFrame Convert(DuplicatedFrame acquired, MonitorInfo monitor)
    {
        List<IntRect> dirty = new(acquired.DirtyCount + (acquired.MoveCount * 2));
        List<IntRect> moves = new(acquired.MoveCount);
        double scaleX = monitor.Width / (double)acquired.Width;
        double scaleY = monitor.Height / (double)acquired.Height;

        for (int i = 0; i < acquired.DirtyCount; i++)
        {
            Vortice.RawRect rect = acquired.DirtyRects[i];
            dirty.Add(Scale(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top, scaleX, scaleY));
        }

        for (int i = 0; i < acquired.MoveCount; i++)
        {
            OutduplMoveRect move = acquired.MoveRects[i];
            int width = move.DestinationRect.Right - move.DestinationRect.Left;
            int height = move.DestinationRect.Bottom - move.DestinationRect.Top;
            IntRect destination = Scale(move.DestinationRect.Left, move.DestinationRect.Top, width, height, scaleX, scaleY);

            // A move dirties two regions: the destination (moved-in content) and the source (vacated).
            dirty.Add(destination);
            dirty.Add(Scale(move.SourcePoint.X, move.SourcePoint.Y, width, height, scaleX, scaleY));
            moves.Add(destination);
        }

        return new SourceFrame(
            monitor,
            acquired.Width,
            acquired.Height,
            acquired.Pitch,
            acquired.Pixels,
            dirty,
            moves,
            acquired.NeedsFullRescan,
            acquired.ProtectedContentMaskedOut,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000,
            acquired.ReadbackMs);
    }

    private static IntRect Scale(int x, int y, int width, int height, double scaleX, double scaleY)
    {
        if (scaleX == 1.0 && scaleY == 1.0)
        {
            return new IntRect(x, y, width, height);
        }

        int scaledX = (int)Math.Floor(x * scaleX);
        int scaledY = (int)Math.Floor(y * scaleY);
        int right = (int)Math.Ceiling((x + width) * scaleX);
        int bottom = (int)Math.Ceiling((y + height) * scaleY);
        return new IntRect(scaledX, scaledY, Math.Max(0, right - scaledX), Math.Max(0, bottom - scaledY));
    }
}
