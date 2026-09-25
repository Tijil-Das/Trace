using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>Axis-aligned rect in monitor-local pixels.</summary>
internal readonly record struct IntRect(int X, int Y, int Width, int Height)
{
    internal int Right => X + Width;

    internal int Bottom => Y + Height;
}

/// <summary>
/// Backend-independent frame. <see cref="Pixels"/> is borrowed from the capture backend and is only
/// valid until <see cref="IFrameSource.Release"/> — the engine consumes tiles before releasing so no
/// full-frame copy is ever made on the steady-state path.
/// </summary>
internal sealed class SourceFrame
{
    internal SourceFrame(
        MonitorInfo monitor,
        int width,
        int height,
        int pitch,
        byte[] pixels,
        IReadOnlyList<IntRect> dirtyRects,
        IReadOnlyList<IntRect> moveRects,
        bool fullRescan,
        bool protectedContent,
        long timestampUs,
        double readbackMs = 0)
    {
        Monitor = monitor;
        Width = width;
        Height = height;
        Pitch = pitch;
        Pixels = pixels;
        DirtyRects = dirtyRects;
        MoveRects = moveRects;
        FullRescan = fullRescan;
        ProtectedContent = protectedContent;
        TimestampUs = timestampUs;
        ReadbackMs = readbackMs;
    }

    internal MonitorInfo Monitor { get; }

    internal int Width { get; }

    internal int Height { get; }

    internal int Pitch { get; }

    internal byte[] Pixels { get; }

    internal IReadOnlyList<IntRect> DirtyRects { get; }

    internal IReadOnlyList<IntRect> MoveRects { get; }

    /// <summary>True when DXGI gave no usable rects and the whole surface must be rescanned.</summary>
    internal bool FullRescan { get; }

    /// <summary>True when the compositor blanked DRM-protected content (spec 13).</summary>
    internal bool ProtectedContent { get; }

    internal long TimestampUs { get; }

    /// <summary>Time the backend spent producing this frame (GPU copy + CPU readback), in ms.</summary>
    internal double ReadbackMs { get; }

    /// <summary>True when DXGI reported a pointer position, visibility or shape change with this frame.</summary>
    internal bool HasPointerUpdate { get; init; }

    /// <summary>
    /// Pointer shape to draw from now on, or null when the shape did not change. The pointer is separate state:
    /// it never appears in <see cref="Pixels"/>, because the compositor draws it above the desktop.
    /// </summary>
    internal PointerShape? PointerShape { get; init; }

    /// <summary>Pointer position in monitor pixels, as DXGI reported it.</summary>
    internal int PointerX { get; init; }

    /// <summary>Pointer position in monitor pixels, as DXGI reported it.</summary>
    internal int PointerY { get; init; }

    /// <summary>False while the pointer is hidden.</summary>
    internal bool PointerVisible { get; init; }

    /// <summary>True when there is anything to record.</summary>
    internal bool HasChanges => FullRescan || DirtyRects.Count > 0 || MoveRects.Count > 0;
}

/// <summary>
/// A source's "I cannot capture right now" state, so the service can say why instead of substituting something
/// else (spec 5.7 / 13). <see cref="IsExpected"/> distinguishes the reasons that clear on their own - a locked
/// session, a disconnected Remote Desktop session, a second instance - from genuine failures.
/// </summary>
internal readonly record struct SourceBlock(DxgiStatus.UnavailableReason Reason, string Detail, int RetryInMs)
{
    /// <summary>True for causes that are normal on a working machine and fix themselves.</summary>
    internal bool IsExpected => DxgiStatus.IsExpected(Reason);

    /// <summary>One line for the dashboard's state readout.</summary>
    internal string Describe()
        => Detail.Length == 0 ? DxgiStatus.Describe(Reason) : $"{DxgiStatus.Describe(Reason)}: {Detail}";
}

/// <summary>A capture backend: DXGI desktop duplication, or the synthetic generator used in tests.</summary>
internal interface IFrameSource : IDisposable
{
    /// <summary>Human-readable backend name for logs and the dashboard.</summary>
    string Name { get; }

    /// <summary>Monitors this source can serve.</summary>
    IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>
    /// Why this source cannot deliver frames right now (null while it can), and when it will try again. The
    /// service surfaces this rather than recording something else: a source that cannot capture says so.
    /// </summary>
    SourceBlock? Block { get; }

    /// <summary>
    /// True while the engine needs every pixel of the next frame, because a full-surface rescan is pending. A
    /// backend that reads back only the regions DXGI reports must honour this: otherwise the tiles outside those
    /// regions still hold pixels from an earlier frame and would be hashed - and logged - as current content.
    /// </summary>
    bool FullFrameRequired { get; set; }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a frame. False means "nothing changed in time";
    /// callers must release every frame they receive before acquiring again.
    /// </summary>
    bool TryAcquire(TimeSpan timeout, out SourceFrame frame);

    /// <summary>Returns a frame to the backend.</summary>
    void Release(SourceFrame frame);

    /// <summary>Rebuilds backend state after a lost session or a display topology change.</summary>
    bool TryRecreate(out string? error);
}
