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
    private int _lastPointerX = int.MinValue;
    private int _lastPointerY = int.MinValue;
    private bool _lastPointerVisible;

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

    /// <summary>Creates a duplication session for an output, or null when it cannot be duplicated.</summary>
    internal static DesktopDuplicator? TryCreate(DxgiOutputTarget target, out string? error)
    {
        error = null;
        FeatureLevel[] levels =
        {
            FeatureLevel.Level_11_1,
            FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1,
            FeatureLevel.Level_10_0,
        };

        if (D3D11.D3D11CreateDevice(
                target.Adapter,
                DriverType.Unknown,
                DeviceCreationFlags.BgraSupport,
                levels,
                out ID3D11Device? device,
                out FeatureLevel _,
                out ID3D11DeviceContext? context).Failure
            || device is null
            || context is null)
        {
            error = "D3D11CreateDevice failed";
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
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or InvalidCastException)
        {
            error = ex.Message;
            context.Dispose();
            device.Dispose();
            return null;
        }
    }

    /// <summary>Monitor this session captures.</summary>
    internal MonitorInfo Monitor => _target.Monitor;

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
            throw new DuplicationLostException($"AcquireNextFrame failed: {result.Description}");
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
        long readbackTicks = System.Diagnostics.Stopwatch.GetTimestamp();
        _context.CopyResource(staging.Texture, desktopTexture);
        _context.Flush();
        BufferMapper.CopyToPacked(
            staging,
            _context.Map(staging.Texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None),
            _context);
        double readbackMs = System.Diagnostics.Stopwatch.GetElapsedTime(readbackTicks).TotalMilliseconds;

        FrameRects rects = FrameRectsReader.Read(_duplication);

        // Cursor movement and shape updates arrive as metadata, never as dirty rects. Tracking them
        // lets the engine distinguish "the compositor presented for the cursor only" from "something
        // changed but the driver reported no regions", which is what decides whether a full-surface
        // rescan is actually needed.
        bool pointerUpdate =
            frameInfo.PointerShapeBufferSize > 0
            || frameInfo.PointerPosition.Position.X != _lastPointerX
            || frameInfo.PointerPosition.Position.Y != _lastPointerY
            || frameInfo.PointerPosition.Visible != _lastPointerVisible;
        _lastPointerX = frameInfo.PointerPosition.Position.X;
        _lastPointerY = frameInfo.PointerPosition.Position.Y;
        _lastPointerVisible = frameInfo.PointerPosition.Visible;

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
