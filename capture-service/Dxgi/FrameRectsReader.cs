using ScreenRecall.CaptureService.Capture;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenRecall.CaptureService.Dxgi;

/// <summary>Copies a mapped staging surface into a tightly packed BGRA buffer.</summary>
internal static class BufferMapper
{
    /// <summary>Maps, copies and unmaps in one step (rows are re-packed when DXGI pads them).</summary>
    internal static void CopyToPacked(StagingSurface staging, MappedSubresource mapped, ID3D11DeviceContext context)
    {
        try
        {
            int rowBytes = staging.Width * 4;
            int sourcePitch = (int)mapped.RowPitch;

            if (sourcePitch == rowBytes)
            {
                Marshal.Copy(mapped.DataPointer, staging.Buffer, 0, staging.Buffer.Length);
                return;
            }

            for (int y = 0; y < staging.Height; y++)
            {
                Marshal.Copy(IntPtr.Add(mapped.DataPointer, y * sourcePitch), staging.Buffer, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            context.Unmap(staging.Texture, 0);
        }
    }

    /// <summary>
    /// Copies only the planned regions out of a mapped staging surface into the packed buffer, then unmaps.
    ///
    /// The regions are already snapped to tile boundaries, so what lands in the buffer is whole tiles, and the
    /// buffer keeps absolute addressing: pixels outside every region are left exactly as they were, which is
    /// correct because no tile outside them is about to be hashed. That is where the saving comes from - a
    /// one-corner change copies a few kilobytes instead of the whole framebuffer.
    /// </summary>
    internal static void CopyRegionsToPacked(
        StagingSurface staging,
        MappedSubresource mapped,
        ID3D11DeviceContext context,
        List<IntRect> regions)
    {
        try
        {
            int rowBytes = staging.Width * 4;
            int sourcePitch = (int)mapped.RowPitch;

            foreach (IntRect region in regions)
            {
                int bytes = region.Width * 4;
                for (int row = 0; row < region.Height; row++)
                {
                    int targetRow = region.Y + row;
                    Marshal.Copy(
                        IntPtr.Add(mapped.DataPointer, (targetRow * sourcePitch) + (region.X * 4)),
                        staging.Buffer,
                        (targetRow * rowBytes) + (region.X * 4),
                        bytes);
                }
            }
        }
        finally
        {
            context.Unmap(staging.Texture, 0);
        }
    }
}

/// <summary>Dirty and move rect buffers produced by DXGI for one frame.</summary>
internal readonly struct FrameRects
{
    internal FrameRects(Vortice.RawRect[] dirtyRects, int dirtyCount, OutduplMoveRect[] moveRects, int moveCount)
    {
        DirtyRects = dirtyRects;
        DirtyCount = dirtyCount;
        MoveRects = moveRects;
        MoveCount = moveCount;
    }

    internal Vortice.RawRect[] DirtyRects { get; }

    internal int DirtyCount { get; }

    internal OutduplMoveRect[] MoveRects { get; }

    internal int MoveCount { get; }
}

/// <summary>Reads dirty/move rects, growing the buffers when DXGI reports a bigger list.</summary>
internal static class FrameRectsReader
{
    private const int InitialDirtyCapacity = 512;
    private const int InitialMoveCapacity = 128;

    private static Vortice.RawRect[] _dirty = new Vortice.RawRect[InitialDirtyCapacity];
    private static OutduplMoveRect[] _move = new OutduplMoveRect[InitialMoveCapacity];
    private static int _lastDirtyCount;
    private static int _lastMoveCount;

    private static readonly int RawRectSize = Marshal.SizeOf<Vortice.RawRect>();
    private static readonly int MoveRectSize = Marshal.SizeOf<OutduplMoveRect>();

    /// <summary>Dirty rect count reported for the most recent frame (diagnostics).</summary>
    internal static int LastDirtyCount => _lastDirtyCount;

    /// <summary>Move rect count reported for the most recent frame (diagnostics).</summary>
    internal static int LastMoveCount => _lastMoveCount;

    internal static FrameRects Read(IDXGIOutputDuplication duplication)
    {
        int dirtyCount = ReadDirty(duplication);
        int moveCount = ReadMove(duplication);
        return new FrameRects(_dirty, dirtyCount, _move, moveCount);
    }

    /// <summary>Counts leading non-degenerate rects as a fallback for drivers that under-report.</summary>
    private static int CountNonEmptyRects()
    {
        int count = 0;
        for (int i = 0; i < _dirty.Length; i++)
        {
            Vortice.RawRect rect = _dirty[i];
            if (rect.Right <= rect.Left && rect.Bottom <= rect.Top)
            {
                break;
            }

            count++;
        }

        return count;
    }

    private static int ReadDirty(IDXGIOutputDuplication duplication)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            SharpGen.Runtime.Result result = duplication.GetFrameDirtyRects(
                (uint)(_dirty.Length * RawRectSize),
                _dirty,
                out uint required);

            if (result.Success)
            {
                int count = (int)(required / (uint)RawRectSize);
                if (count == 0)
                {
                    // Some drivers report the buffer size, not the written size, on success: fall back
                    // to scanning for a non-degenerate rect list so changes are never lost.
                    count = CountNonEmptyRects();
                }

                _lastDirtyCount = Math.Min(count, _dirty.Length);
                return _lastDirtyCount;
            }

            int needed = (int)(required / (uint)RawRectSize);
            if (needed <= _dirty.Length)
            {
                _lastDirtyCount = 0;
                return 0;
            }

            _dirty = new Vortice.RawRect[needed + 64];
        }

        _lastDirtyCount = 0;
        return 0;
    }

    private static int ReadMove(IDXGIOutputDuplication duplication)
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            SharpGen.Runtime.Result result = duplication.GetFrameMoveRects(
                (uint)(_move.Length * MoveRectSize),
                _move,
                out uint required);

            if (result.Success)
            {
                _lastMoveCount = Math.Min((int)(required / (uint)MoveRectSize), _move.Length);
                return _lastMoveCount;
            }

            int needed = (int)(required / (uint)MoveRectSize);
            if (needed <= _move.Length)
            {
                _lastMoveCount = 0;
                return 0;
            }

            _move = new OutduplMoveRect[needed + 32];
        }

        _lastMoveCount = 0;
        return 0;
    }
}
