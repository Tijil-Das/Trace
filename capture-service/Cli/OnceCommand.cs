using System.Diagnostics;

using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Cli;
using ScreenRecall.CaptureService.Ipc;
using ScreenRecall.CaptureService.Service;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService;

/// <summary>
/// <c>--once &lt;seconds&gt;</c>: captures for a fixed window and prints the numbers the spec cares about
/// (CPU, tiles/sec, dedupe rate, bytes written and the daily extrapolation). This is the quick
/// validation harness behind spec 12's budget checks, and the same engine path the service uses.
/// </summary>
internal static class OnceCommand
{
    internal static int Run(RecallConfig config, CommandLine commandLine)
    {
        RecallConfig effective = config.Clone().Normalize();
        if (commandLine.RootOverride is null && !commandLine.Synthetic && !commandLine.ConfigExplicit)
        {
            // Throwaway validation runs stay out of the real store unless a root or config was named.
            effective.StoragePath = Path.Combine(
                Path.GetTempPath(),
                "screen-recall-once",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        IFrameSource source = CaptureWorker.CreateSource(effective, commandLine, out string? note);
        Console.WriteLine($"source      : {source.Name}{(note is null ? string.Empty : $" ({note})")}");
        Console.WriteLine($"root        : {effective.StoragePath}");
        Console.WriteLine($"monitors    : {source.Monitors.Count}");
        foreach (MonitorInfo monitor in source.Monitors)
        {
            Console.WriteLine($"  #{monitor.Id} {monitor.Width}x{monitor.Height} grid {monitor.Columns}x{monitor.Rows}");
        }

        CaptureController controller = new(commandLine.ConfigPath, effective);
        controller.Start(source, out CaptureIpcServer server);
        Stopwatch clock = Stopwatch.StartNew();

        try
        {
            TimeSpan duration = TimeSpan.FromSeconds(commandLine.OnceSeconds);
            while (clock.Elapsed < duration)
            {
                Thread.Sleep(1000);
                StatusDto live = controller.GetStatus();
                Console.WriteLine(
                    $"[{clock.Elapsed.TotalSeconds,5:0}s] frames={live.FramesAcquired,6} changed={live.FramesWithChanges,6} "
                    + $"tiles={live.TilesHashed,8} stored={live.TilesStored,7} deduped={live.TilesDeduped,8} "
                    + $"log={live.LogEntries,8} cpu={live.CpuPercent:0.00}% mem={live.WorkingSetMb:0}MB");
            }

            controller.Flush();
            StatusDto status = controller.GetStatus();
            PrintSummary(status, clock.Elapsed, effective, controller.Engine);
            return 0;
        }
        finally
        {
            server.Dispose();
            controller.Stop();
        }
    }

    private static void PrintSummary(StatusDto status, TimeSpan elapsed, RecallConfig config, CaptureEngine? engine)
    {
        double seconds = Math.Max(1, elapsed.TotalSeconds);
        double tilesPerSecond = status.TilesHashed / seconds;
        double dedupeRate = status.TilesHashed == 0
            ? 0
            : 100.0 * status.TilesDeduped / status.TilesHashed;
        double bytesPerHour = status.AssetBytesWritten / seconds * 3600;
        double assetsBytesPerHour = status.AssetBytesOnDisk / seconds * 3600;
        double entriesPerSecond = status.LogEntries / seconds;

        Console.WriteLine();
        Console.WriteLine("=== summary ===");
        Console.WriteLine($"duration            : {seconds:0.0}s");
        Console.WriteLine($"frames acquired     : {status.FramesAcquired} ({status.FramesWithChanges} with changes, {status.FullFrameRescans} full rescans)");
        Console.WriteLine($"rects last frame    : {status.LastDirtyRects} dirty, {status.LastMoveRects} move");
        Console.WriteLine($"skipped (no change) : {status.FramesSkippedNoChange} (pointer-only presents), {status.FramesRescanDeferred} rescan(s) deferred");
        Console.WriteLine($"skipped (excluded)  : {status.FramesSkippedExcluded}");
        Console.WriteLine($"skipped (protected) : {status.FramesSkippedProtected}");
        Console.WriteLine($"tiles hashed        : {status.TilesHashed} ({tilesPerSecond:0}/s)");
        Console.WriteLine($"tiles stored        : {status.TilesStored}");
        Console.WriteLine($"tiles deduped       : {status.TilesDeduped} ({dedupeRate:0.0}% of hashed tiles)");
        Console.WriteLine($"log entries         : {status.LogEntries} ({entriesPerSecond:0.00}/s)");
        Console.WriteLine($"asset bytes written : {status.AssetBytesWritten / 1024.0:0.0} KB");
        Console.WriteLine($"store size          : {status.AssetBytesOnDisk / 1024.0:0.0} KB in {status.AssetCountOnDisk} assets");
        Console.WriteLine($"session bytes today : {status.SessionBytes / 1024.0:0.0} KB");
        Console.WriteLine($"extrapolated        : {bytesPerHour / 1024 / 1024:0.00} MB/hour of new tiles, {assetsBytesPerHour / 1024 / 1024:0.00} MB/hour on disk");
        Console.WriteLine($"avg frame cost      : {status.AverageFrameMs:0.00} ms (throttle level {status.ThrottleLevel})");
        Console.WriteLine($"avg process/acquire : {status.AverageProcessMs:0.00} ms / {status.AverageAcquireMs:0.00} ms");
        Console.WriteLine($"last frame phases   : readback {status.LastReadbackMs:0.0} ms, hash {status.LastHashMs:0.0} ms, "
                          + $"encode {status.LastEncodeMs:0.0} ms, store {status.LastStoreMs:0.0} ms, log {status.LastLogMs:0.0} ms");
        Console.WriteLine($"cpu (sampled)       : {status.CpuPercent:0.00}%");
        Console.WriteLine($"working set         : {status.WorkingSetMb:0} MB");
        Console.WriteLine($"canvas tiles        : {status.CanvasTiles}");
        Console.WriteLine($"log integrity       : {status.LogZeroRecordFaults} zero record(s), external writer: {status.LogExternalWriterDetected}");
        Console.WriteLine($"retention           : {config.RetentionDays} day(s)");
        if (status.LastError is not null)
        {
            Console.WriteLine($"last error          : {status.LastError}");
        }

        if (engine?.Stats.FatalException is { } fatal)
        {
            Console.WriteLine();
            Console.WriteLine("=== fatal ===");
            Console.WriteLine(fatal.ToString());
        }
    }
}
