using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Cli;
using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.CaptureService.Ipc;
using ScreenRecall.Storage;

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
                Console.WriteLine("virtual display driver. Use --synthetic for validation on such machines.");
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
}
