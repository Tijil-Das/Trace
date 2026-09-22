using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Cli;
using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.CaptureService.Ipc;
using ScreenRecall.Storage;
using Vortice.DXGI;

namespace ScreenRecall.CaptureService;

/// <summary>
/// <c>--probe</c>: reports what the machine offers before committing to a recording, which is also
/// the check the dashboard's first-run wizard uses (spec 10).
/// </summary>
internal static class ProbeCommand
{
    internal static int Run(RecallConfig config, CommandLine commandLine)
    {
        Console.WriteLine("Screen Recall — capture probe");
        Console.WriteLine($"tile size      : {config.TileSize}px");
        Console.WriteLine($"fidelity mode  : {config.FidelityMode}");
        Console.WriteLine();

        try
        {
            if (commandLine.Synthetic)
            {
                using SyntheticFrameSource synthetic = new(1024, 768, 16, config.TileSize);
                Console.WriteLine("source         : synthetic (requested)");
                PrintMonitors(synthetic.Monitors);
                return 0;
            }

            List<MonitorInfo> monitors = DxgiOutputEnumerator.EnumerateMonitors(config.TileSize);
            Console.WriteLine($"dxgi outputs   : {monitors.Count}");
            PrintMonitors(monitors);
            Console.WriteLine();
            PrintAdapters();

            using DxgiFrameSource source = DxgiFrameSource.Create(config.TileSize, null);
            Console.WriteLine();
            Console.WriteLine($"duplication    : {(source.Monitors.Count > 0 ? "ok" : "FAILED")}");
            foreach (string note in source.Notes)
            {
                Console.WriteLine($"  note: {note}");
            }

            if (source.Monitors.Count == 0)
            {
                Console.WriteLine();
                Console.WriteLine("Duplication is unavailable here: session 0, a locked workstation, or a");
                Console.WriteLine("virtual display driver. Use --synthetic-test for validation on such machines.");
                return 2;
            }

            Console.WriteLine();
            Console.WriteLine("Sampling a few frames to confirm the loop works…");
            using CancellationTokenSource cts = new(TimeSpan.FromSeconds(3));
            int frames = 0;
            int withChanges = 0;
            while (!cts.IsCancellationRequested && frames < 60)
            {
                if (!source.TryAcquire(TimeSpan.FromMilliseconds(200), out SourceFrame frame))
                {
                    continue;
                }

                frames++;
                if (frame.HasChanges)
                {
                    withChanges++;
                }

                source.Release(frame);
            }

            Console.WriteLine($"acquired       : {frames} frame(s), {withChanges} with reported changes");
            return frames > 0 ? 0 : 3;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"probe failed: {ex.Message}");
            return 1;
        }
    }

    private static void PrintMonitors(IReadOnlyList<MonitorInfo> monitors)
    {
        foreach (MonitorInfo monitor in monitors)
        {
            Console.WriteLine(
                $"  #{monitor.Id} {monitor.DeviceName,-28} {monitor.Width}x{monitor.Height} at ({monitor.X},{monitor.Y}) "
                + $"grid {monitor.Columns}x{monitor.Rows} = {monitor.TileCount} tiles");
        }
    }

    /// <summary>
    /// Lists the GPU adapters DXGI sees, with the two facts a compute-offload decision needs: whether the adapter
    /// carries real (or dedicated) memory rather than being a software fallback, and which one Windows prefers for
    /// GPU work. This is inventory for that decision, not a migration to one.
    /// </summary>
    private static void PrintAdapters()
    {
        using IDXGIFactory1 factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
        int index = 0;

        for (uint adapterIndex = 0; ; adapterIndex++)
        {
            if (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? adapter).Failure || adapter is null)
            {
                break;
            }

            try
            {
                AdapterDescription1 description = adapter.Description1;
                string kind = description.Flags == AdapterFlags.None ? "hardware" : description.Flags.ToString().ToLowerInvariant();
                Console.WriteLine(
                    $"adapter #{index}    : {description.Description.Trim()} ({kind}, "
                    + $"dedicated {description.DedicatedVideoMemory / 1024 / 1024} MB, "
                    + $"shared {description.SharedSystemMemory / 1024 / 1024} MB)");
                index++;
            }
            finally
            {
                adapter.Dispose();
            }
        }

        if (index == 0)
        {
            Console.WriteLine("adapters       : none reported by DXGI");
        }
    }
}
