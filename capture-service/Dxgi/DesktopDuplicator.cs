using ScreenRecall.CaptureService.Capture;
using ScreenRecall.Storage;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenRecall.CaptureService.Dxgi;

/// <summary>
/// One monitor's desktop duplication session (spec 5.1): owns the DXGI duplication interface, a
/// single-threaded D3D11 device and a staging texture used for CPU readback. Dirty and move rects come
/// straight from DXGI, so a frame that changed in one corner never costs a full-frame diff.
/// </summary>
internal sealed partial class DesktopDuplicator : IDisposable
{
    private readonly DxgiOutputTarget _target;
    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly IDXGIOutputDuplication _duplication;
    private StagingSurface? _staging;
    private bool _frameAcquired;
    private bool _disposed;

    /// <summary>Reused region list for the partial readback: the steady-state path allocates nothing per frame.</summary>
    private readonly List<IntRect> _readbackRegions = new(ReadbackRegions.MaxRegions);
    private int _lastPointerX = int.MinValue;
    private int _lastPointerY = int.MinValue;
    private bool _lastPointerVisible;
    private PointerShape? _pointerShape;

    private DesktopDuplicator(
        DxgiOutputTarget target,
        ID3D11Device device,
        ID3D11DeviceContext context,
        IDXGIOutputDuplication duplication)
    {
        _target = target;
        _device = device;
        _context = context;
        _duplication = duplication;
    }

    /// <summary>Creates a duplication session for an output, or reports why not.</summary>
    internal static DesktopDuplicator? TryCreate(DxgiOutputTarget target, out DxgiStatus.Failure failure)
    {
        failure = default;
        FeatureLevel[] levels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };

        SharpGen.Runtime.Result created = D3D11.D3D11CreateDevice(
            target.Adapter,
            DriverType.Unknown,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device? device,
            out FeatureLevel _,
            out ID3D11DeviceContext? context);

        if (created.Failure || device is null || context is null)
        {
            failure = new DxgiStatus.Failure(
                DxgiStatus.Classify(created),
                $"D3D11CreateDevice failed: {created.Description}");
            device?.Dispose();
            context?.Dispose();
            return null;
        }

        try
        {
            using IDXGIOutput1 output1 = target.Output.QueryInterface<IDXGIOutput1>();
            IDXGIOutputDuplication duplication = output1.DuplicateOutput(device);
            return new DesktopDuplicator(target, device, context, duplication);
        }
        catch (SharpGen.Runtime.SharpGenException ex)
        {
            // The HRESULT decides how the retry loop treats this: E_ACCESSDENIED means the desktop is not
            // visible to us right now (locked, secure desktop, another session) and is expected, while a device
            // or driver error is a genuine fault. Both are reported honestly, neither is faked (spec 13).
            // SharpGenException carries the code in HResult rather than a Result property.
            failure = new DxgiStatus.Failure(
                DxgiStatus.Classify(ex.HResult),
                $"{target.Monitor.DeviceName}: {ex.Message}");
        }
        catch (InvalidCastException ex)
        {
            failure = new DxgiStatus.Failure(
                DxgiStatus.UnavailableReason.Unknown,
                $"{target.Monitor.DeviceName}: {ex.Message}");
        }

        context.Dispose();
        device.Dispose();
        return null;
    }

    /// <summary>Monitor this session captures.</summary>
    internal MonitorInfo Monitor => _target.Monitor;

    /// <summary>
    /// Set by the engine while a full-surface rescan is pending. A partial readback would leave every tile
    /// outside the dirty rects holding pixels from an earlier frame, and those tiles are about to be hashed -
    /// so a pending rescan is the one case where the whole surface has to be read.
    /// </summary>
    internal bool FullFrameRequired { get; set; }

    /// <summary>
    /// Waits up to <paramref name="timeout"/> for a frame. Returns null on timeout (nothing changed);
    /// throws <see cref="DuplicationLostException"/> when the session must be rebuilt.
    /// </summary>
    internal DuplicatedFrame? TryAcquireFrame(TimeSpan timeout)
    {
        if (_disposed)
        {
            return null;
        }

        if (_frameAcquired)
        {
            ReleaseFrame();
        }

        SharpGen.Runtime.Result result = _duplication.AcquireNextFrame(
            (uint)Math.Clamp(timeout.TotalMilliseconds, 0, int.MaxValue),
            out OutduplFrameInfo frameInfo,
            out IDXGIResource? desktopResource);

        if (DxgiStatus.IsTimeout(result))
        {
            return null;
        }

        if (result.Failure || desktopResource is null)
        {
            throw new DuplicationLostException($"AcquireNextFrame failed: {result.Description}")
            {
                Reason = DxgiStatus.Classify(result),
            };
        }

        _frameAcquired = true;

        using ID3D11Texture2D? desktopTexture = desktopResource.QueryInterfaceOrNull<ID3D11Texture2D>();
        desktopResource.Dispose();
        if (desktopTexture is null)
        {
            throw new DuplicationLostException("Desktop resource was not a D3D11 texture.");
        }

        Texture2DDescription description = desktopTexture.Description;
        StagingSurface staging = EnsureStaging(description);

        // Rect first, pixels second: the region list is what decides which pixels are needed at all, and
        // reading it costs nothing. A frame whose regions cannot be trusted - a pending full rescan, a scaled
        // surface, a dirty area covering most of the screen - falls back to the whole-surface copy.
        FrameRects rects = FrameRectsReader.Read(_duplication);
        bool partial = ReadbackRegions.TryPlan(
            rects.DirtyRects,
            rects.DirtyCount,
            rects.MoveRects,
            rects.MoveCount,
            Monitor,
            staging.Width,
            staging.Height,
            FullFrameRequired,
            _readbackRegions);

        long readbackTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _context.CopyResource(staging.Texture, desktopTexture);
        _context.Flush();
        MappedSubresource mapped = _context.Map(staging.Texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        if (partial)
        {
            // Only the rows DXGI reported are copied out of the mapped surface. The GPU copy above stays a single
            // whole-surface blit on purpose: that is one driver call instead of one per region, and GPU bandwidth
            // is the budget the spec explicitly allows to be spent (spec 3) while CPU is the one that is not.
            BufferMapper.CopyRegionsToPacked(staging, mapped, _context, _readbackRegions);
        }
        else
        {
            BufferMapper.CopyToPacked(staging, mapped, _context);
        }

        double readbackMs = System.Diagnostics.Stopwatch.GetElapsedTime(readbackTicks).TotalMilliseconds;

        // Cursor movement and shape updates arrive as metadata, never as dirty rects. Tracking them
        // lets the engine distinguish "the compositor presented for the cursor only" from "something
        // changed but the driver reported no regions", which is what decides whether a full-surface
        // rescan is actually needed - and it is also the only place the pointer reaches the recording at all.
        bool shapeOffered = frameInfo.PointerShapeBufferSize > 0;
        bool pointerUpdate =
            shapeOffered
            || frameInfo.PointerPosition.Position.X != _lastPointerX
            || frameInfo.PointerPosition.Position.Y != _lastPointerY
            || frameInfo.PointerPosition.Visible != _lastPointerVisible;
        _lastPointerX = frameInfo.PointerPosition.Position.X;
        _lastPointerY = frameInfo.PointerPosition.Position.Y;
        _lastPointerVisible = frameInfo.PointerPosition.Visible;

        if (shapeOffered)
        {
            // Offered only when it changed, so this decode happens a handful of times per session rather than per
            // frame. A shape that cannot be decoded keeps the previous one: an older cursor is closer to the truth
            // than no cursor.
            _pointerShape = PointerShapeReader.Read(_duplication) ?? _pointerShape;
        }

        return new DuplicatedFrame(
            Monitor,
            staging.Width,
            staging.Height,
            staging.Pitch,
            staging.Buffer,
            rects.DirtyRects,
            rects.DirtyCount,
            rects.MoveRects,
            rects.MoveCount,
            frameInfo.ProtectedContentMaskedOut,
            frameInfo.LastPresentTime)
        {
            HasPointerUpdate = pointerUpdate,
            ReadbackMs = readbackMs,
            PointerShape = shapeOffered ? _pointerShape : null,
            PointerX = frameInfo.PointerPosition.Position.X,
            PointerY = frameInfo.PointerPosition.Position.Y,
            PointerVisible = frameInfo.PointerPosition.Visible,
        };
    }

    /// <summary>Hands the frame back to DXGI; must run before the next acquire.</summary>
    internal void ReleaseFrame()
    {
        if (!_frameAcquired)
        {
            return;
        }

        _frameAcquired = false;
        _duplication.ReleaseFrame();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            ReleaseFrame();
        }
        catch (SharpGen.Runtime.SharpGenException)
        {
        }

        _staging?.Dispose();
        _duplication.Dispose();
        _context.Dispose();
        _device.Dispose();
    }

    /// <summary>Creates or resizes the staging texture used for CPU readback (see DesktopDuplicator.Staging.cs).</summary>
    private StagingSurface EnsureStaging(Texture2DDescription desktop) => EnsureStagingCore(desktop);
}
