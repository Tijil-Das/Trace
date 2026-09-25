using ScreenRecall.Storage;
using Vortice.DXGI;

namespace ScreenRecall.CaptureService.Dxgi;

/// <summary>
/// A frame just read back from a monitor. The pixel buffer is owned by the duplicator and stays valid
/// only until <c>ReleaseFrame</c> — callers must finish reading tiles before releasing.
/// </summary>
internal sealed class DuplicatedFrame
{
    internal DuplicatedFrame(
        MonitorInfo monitor,
        int width,
        int height,
        int pitch,
        byte[] pixels,
        Vortice.RawRect[] dirtyRects,
        int dirtyCount,
        OutduplMoveRect[] moveRects,
        int moveCount,
        bool protectedContent,
        long lastPresentTime)
    {
        Monitor = monitor;
        Width = width;
        Height = height;
        Pitch = pitch;
        Pixels = pixels;
        DirtyRects = dirtyRects;
        DirtyCount = dirtyCount;
        MoveRects = moveRects;
        MoveCount = moveCount;
        ProtectedContentMaskedOut = protectedContent;
        LastPresentTime = lastPresentTime;
    }

    internal MonitorInfo Monitor { get; }

    internal int Width { get; }

    internal int Height { get; }

    internal int Pitch { get; }

    internal byte[] Pixels { get; }

    internal Vortice.RawRect[] DirtyRects { get; }

    internal int DirtyCount { get; }

    internal OutduplMoveRect[] MoveRects { get; }

    internal int MoveCount { get; }

    /// <summary>True when the compositor blanked DRM-protected content in this frame (spec 13).</summary>
    internal bool ProtectedContentMaskedOut { get; }

    internal long LastPresentTime { get; }

    /// <summary>True when DXGI reported any change at all for this frame.</summary>
    internal bool HasChanges => DirtyCount > 0 || MoveCount > 0 || LastPresentTime != 0;

    /// <summary>
    /// True when the frame carried no usable rect list: the compositor presented (often for the
    /// cursor or a layered overlay) without reporting regions, so the surface must be rescanned.
    /// </summary>
    internal bool NeedsFullRescan => DirtyCount == 0 && MoveCount == 0 && LastPresentTime != 0;

    /// <summary>True when the only thing DXGI reported is pointer metadata (no pixels changed).</summary>
    internal bool PointerOnly => DirtyCount == 0 && MoveCount == 0 && LastPresentTime == 0 && HasPointerUpdate;

    /// <summary>True when the frame carried pointer position or shape updates.</summary>
    internal bool HasPointerUpdate { get; init; }

    /// <summary>
    /// The pointer shape to draw from now on, when DXGI offered a new one with this frame. Null means "unchanged":
    /// the caller keeps the shape it already has. The pointer is not part of <see cref="Pixels"/>.
    /// </summary>
    internal PointerShape? PointerShape { get; init; }

    /// <summary>Pointer position in monitor pixels, as DXGI reported it.</summary>
    internal int PointerX { get; init; }

    /// <summary>Pointer position in monitor pixels, as DXGI reported it.</summary>
    internal int PointerY { get; init; }

    /// <summary>False while the pointer is hidden (a full-screen game, a touch session, a hidden cursor).</summary>
    internal bool PointerVisible { get; init; }

    /// <summary>Time spent copying the frame into a staging texture and reading it back, in ms.</summary>
    internal double ReadbackMs { get; init; }
}

/// <summary>Cached staging texture plus the reusable CPU readback buffer behind it.</summary>
internal sealed class StagingSurface : IDisposable
{
    internal StagingSurface(Vortice.Direct3D11.ID3D11Texture2D texture, int width, int height, byte[] buffer)
    {
        Texture = texture;
        Width = width;
        Height = height;
        Pitch = width * 4;
        Buffer = buffer;
    }

    internal Vortice.Direct3D11.ID3D11Texture2D Texture { get; }

    internal int Width { get; }

    internal int Height { get; }

    /// <summary>Row pitch of the tightly packed readback buffer (bytes per row).</summary>
    internal int Pitch { get; }

    /// <summary>Reusable BGRA buffer, size = Pitch * Height.</summary>
    internal byte[] Buffer { get; }

    public void Dispose() => Texture.Dispose();
}

/// <summary>Thrown when a duplication session must be rebuilt (mode change, session switch, …).</summary>
internal sealed class DuplicationLostException : Exception
{
    internal DuplicationLostException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Classified cause, when the throwing site knew it. ACCESS_LOST is routine - a desktop switch or a mode
    /// change - and is handled quietly, while a device error is not (spec 5.1 / 13).
    /// </summary>
    internal DxgiStatus.UnavailableReason Reason { get; init; }
}
