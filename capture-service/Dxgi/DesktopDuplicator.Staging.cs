using Vortice.Direct3D11;
using Vortice.DXGI;

namespace ScreenRecall.CaptureService.Dxgi;

internal sealed partial class DesktopDuplicator
{
    /// <summary>Creates or resizes the staging texture used for CPU readback.</summary>
    private StagingSurface EnsureStagingCore(Texture2DDescription desktop)
    {
        if (_staging is not null
            && _staging.Width == (int)desktop.Width
            && _staging.Height == (int)desktop.Height)
        {
            return _staging;
        }

        _staging?.Dispose();

        Texture2DDescription stagingDescription = new()
        {
            Width = desktop.Width,
            Height = desktop.Height,
            MipLevels = 1,
            ArraySize = 1,
            Format = desktop.Format,
            SampleDescription = new SampleDescription(1, 0),
            Usage = ResourceUsage.Staging,
            BindFlags = BindFlags.None,
            CPUAccessFlags = CpuAccessFlags.Read,
            MiscFlags = ResourceOptionFlags.None,
        };

        ID3D11Texture2D texture = _device.CreateTexture2D(stagingDescription);
        int width = (int)stagingDescription.Width;
        int height = (int)stagingDescription.Height;
        _staging = new StagingSurface(texture, width, height, new byte[width * 4 * height]);
        return _staging;
    }
}
