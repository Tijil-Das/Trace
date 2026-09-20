using ScreenRecall.Storage;
using Vortice.DXGI;

namespace ScreenRecall.CaptureService.Dxgi;

/// <summary>One DXGI output paired with its adapter, ready to be duplicated.</summary>
internal sealed class DxgiOutputTarget : IDisposable
{
    internal DxgiOutputTarget(IDXGIFactory1 factory, IDXGIAdapter1 adapter, IDXGIOutput output, ushort id, MonitorInfo monitor)
    {
        Factory = factory;
        Adapter = adapter;
        Output = output;
        Id = id;
        Monitor = monitor;
    }

    internal IDXGIFactory1 Factory { get; }

    internal IDXGIAdapter1 Adapter { get; }

    internal IDXGIOutput Output { get; }

    internal ushort Id { get; }

    internal MonitorInfo Monitor { get; }

    public void Dispose()
    {
        Output.Dispose();
        Adapter.Dispose();
    }
}

/// <summary>Enumerates the desktop's DXGI outputs and maps them onto Screen Recall monitor geometry.</summary>
internal static class DxgiOutputEnumerator
{
    /// <summary>All attached outputs of every adapter, in stable (adapter, output) order.</summary>
    internal static List<DxgiOutputTarget> Enumerate(int tileSize = TileGrid.DefaultTileSize)
    {
        List<DxgiOutputTarget> targets = new();
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        ushort id = 0;

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Failure || adapter is null)
            {
                break;
            }

            bool adapterUsed = false;
            for (uint outputIndex = 0; ; outputIndex++)
            {
                if (adapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Failure || output is null)
                {
                    break;
                }

                OutputDescription description = output.Description;
                bool attached = description.AttachedToDesktop;
                int width = description.DesktopCoordinates.Right - description.DesktopCoordinates.Left;
                int height = description.DesktopCoordinates.Bottom - description.DesktopCoordinates.Top;
                if (!attached || width <= 0 || height <= 0)
                {
                    output.Dispose();
                    continue;
                }

                MonitorInfo monitor = new(
                    id,
                    description.DeviceName,
                    description.DesktopCoordinates.Left,
                    description.DesktopCoordinates.Top,
                    width,
                    height,
                    tileSize);

                targets.Add(new DxgiOutputTarget(factory, adapter, output, id, monitor));
                adapterUsed = true;
                id++;
            }

            if (!adapterUsed)
            {
                adapter.Dispose();
            }
        }

        return targets;
    }

    /// <summary>Monitor geometry only (no COM objects retained).</summary>
    internal static List<MonitorInfo> EnumerateMonitors(int tileSize = TileGrid.DefaultTileSize)
    {
        List<DxgiOutputTarget> targets = Enumerate(tileSize);
        try
        {
            return targets.Select(t => t.Monitor).ToList();
        }
        finally
        {
            foreach (DxgiOutputTarget target in targets)
            {
                target.Dispose();
            }
        }
    }
}
