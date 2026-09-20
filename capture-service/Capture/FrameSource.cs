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

    /// <summary>True when there is anything to record.</summary>
    internal bool HasChanges => FullRescan || DirtyRects.Count > 0 || MoveRects.Count > 0;
}

/// <summary>A capture backend: DXGI desktop duplication, or the synthetic generator used in tests.</summary>
internal interface IFrameSource : IDisposable
{
    /// <summary>Human-readable backend name for logs and the dashboard.</summary>
    string Name { get; }

    /// <summary>Monitors this source can serve.</summary>
    IReadOnlyList<MonitorInfo> Monitors { get; }

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
