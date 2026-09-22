using System.Diagnostics;

using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Cli;
using ScreenRecall.CaptureService.Interop;
using ScreenRecall.CaptureService.Ipc;
using ScreenRecall.CaptureService.Service;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService;

/// <summary>
/// <c>--soak [minutes]</c>: the check behind the "safe to leave running for a day" claim. It samples the
/// things that grow unless something clears them — ground-truth dumps, abandoned temp files, WAL pages,
/// memory, handles, the store itself — and ends with a verdict, so a long run is a measurement rather
/// than a hope. Uses the same engine and IPC path as the service; only the reporting is different.
/// </summary>
internal static class SoakCommand
{
    internal static int Run(RecallConfig config, CommandLine commandLine)
    {
        BuildConfiguration.WarnIfNotOptimized("the long-run CPU and memory figures");
        RecallConfig effective = config.Clone().Normalize();
        if (commandLine.RootOverride is null && !commandLine.Synthetic && !commandLine.ConfigExplicit)
        {
            // A soak writes real data: keep it out of the user's store unless one was named.
            effective.StoragePath = Path.Combine(
                Path.GetTempPath(),
                "screen-recall-soak",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        IFrameSource source = CaptureWorker.CreateSource(effective, commandLine, out string? note);
        TimeSpan duration = TimeSpan.FromMinutes(commandLine.SoakMinutes);
        TimeSpan interval = TimeSpan.FromSeconds(commandLine.SoakIntervalSeconds);

        Console.WriteLine($"soak         : {duration.TotalMinutes:0} minute(s), sampling every {interval.TotalSeconds:0}s");
        Console.WriteLine($"source       : {source.Name}{(note is null ? string.Empty : $" ({note})")}");
        Console.WriteLine($"root         : {effective.StoragePath}");
        Console.WriteLine($"ground truth : {effective.CaptureGroundTruth} "
                          + $"(caps {GroundTruthStore.DefaultMaxFrames} frames / {GroundTruthStore.DefaultMaxBytes / 1024 / 1024} MB per folder)");
        Console.WriteLine($"retention    : {effective.RetentionDays} day(s), daily budget {effective.MaxDailyMegabytes} MB, "
                          + $"disk floor {effective.MinFreeDiskMegabytes} MB");
        Console.WriteLine();
        Console.WriteLine("  elapsed     cpu%   workSet  managed  handles  threads  frames    tiles   log   assets  session   dumps  tmp    wal   queue");

        CaptureController controller = new(commandLine.ConfigPath, effective);
        controller.Start(source, out CaptureIpcServer server);
        Stopwatch clock = Stopwatch.StartNew();
        List<Sample> samples = new();

        try
        {
            while (true)
            {
                Sample sample = Take(controller, effective, clock);
                samples.Add(sample);
                Print(sample);
                if (clock.Elapsed >= duration)
                {
                    break;
                }

                Thread.Sleep(interval);
            }

            controller.Flush();
            Verdict(samples, effective, source.Name);
            return 0;
        }
        finally
        {
            server.Dispose();
            controller.Stop();
        }
    }

    /// <summary>One reading of everything that should stay flat or stay bounded over a long run.</summary>
    private readonly record struct Sample(
        double Seconds,
        double CpuPercent,
        double WorkingSetMb,
        double ManagedMb,
        int Handles,
        int Threads,
        long Frames,
        long TilesStored,
        long LogEntries,
        long AssetCount,
        long AssetBytes,
        long SessionBytes,
        long DumpBytes,
        int DumpCount,
        int TempFiles,
        double TempOldestSeconds,
        long WalBytes,
        int QueueDepth,
        string? Error);

    private static Sample Take(CaptureController controller, RecallConfig config, Stopwatch clock)
    {
        StatusDto status = controller.GetStatus();
        using Process process = Process.GetCurrentProcess();
        process.Refresh();

        string root = config.StoragePath;
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        (int dumpCount, long dumpBytes) = CountFiles(SessionLayout.GroundTruthDir(root, day), "*.raw");
        (int tempParts, double tempOldestSeconds) = CountTempParts(
            Path.Combine(SessionLayout.AssetsRoot(root), "tmp"),
            SessionLayout.CheckpointDir(root, day));

        return new Sample(
            Seconds: clock.Elapsed.TotalSeconds,
            CpuPercent: status.CpuPercent,
            WorkingSetMb: status.WorkingSetMb,
            ManagedMb: GC.GetTotalMemory(forceFullCollection: false) / 1024.0 / 1024.0,
            Handles: QuietHandleCount(),
            Threads: process.Threads.Count,
            Frames: status.FramesAcquired,
            TilesStored: status.TilesStored,
            LogEntries: status.LogEntries,
            AssetCount: status.AssetCountOnDisk,
            AssetBytes: status.AssetBytesOnDisk,
            SessionBytes: status.SessionBytes,
            DumpBytes: dumpBytes,
            DumpCount: dumpCount,
            TempFiles: tempParts,
            TempOldestSeconds: tempOldestSeconds,
            WalBytes: FileLength(SessionLayout.IndexPath(root) + "-wal"),
            QueueDepth: status.AssetQueueDepth,
            Error: status.LastError);
    }

    private static void Print(Sample sample)
    {
        Console.WriteLine(
            $"  {sample.Seconds,7:0}s {sample.CpuPercent,7:0.00} {sample.WorkingSetMb,8:0}MB {sample.ManagedMb,7:0}MB "
            + $"{sample.Handles,8} {sample.Threads,8} {sample.Frames,7} {sample.TilesStored,8} {sample.LogEntries,5} "
            + $"{sample.AssetCount,7} {sample.SessionBytes / 1024,8}KB {sample.DumpCount,4}/{sample.DumpBytes / 1024,5}KB "
            + $"{sample.TempFiles,4} {sample.WalBytes / 1024,5}KB {sample.QueueDepth,6}");
    }

    /// <summary>Prints the per-metric verdict once the run finishes.</summary>
    private static void Verdict(List<Sample> samples, RecallConfig config, string sourceName)
    {
        if (samples.Count < 2)
        {
            Console.WriteLine();
            Console.WriteLine("soak too short to judge: at least two samples are needed (see --soak-interval).");
            return;
        }

        // Judge growth from the second sample on: the first is taken at t≈0, before the thread pool, the
        // JIT and the writer's buffers have settled, and counting that ramp as a leak would make every
        // run look like one. What matters over a day is the steady-state slope.
        Sample first = samples[0];
        Sample last = samples[^1];
        int baselineIndex = samples.Count >= 4 ? 1 : 0;
        Sample baseline = samples[baselineIndex];
        double hours = Math.Max(0.01, (last.Seconds - baseline.Seconds) / 3600.0);
        double averageCpu = samples.Skip(1).DefaultIfEmpty(first).Average(sample => sample.CpuPercent);
        double managedRate = GrowthRate(baseline.ManagedMb, last.ManagedMb, hours);
        double workingSetRate = GrowthRate(baseline.WorkingSetMb, last.WorkingSetMb, hours);

        // Handles are judged by the floor of each half of the window, not by the last sample: the process
        // briefly holds several hundred extra handles now and then, and a leaking process is one whose *floor*
        // rises. Reading the last sample as the answer reported 24,000 handles/hour for a process that starts at
        // 340 and ends at 354.
        int midpoint = Math.Max(baselineIndex + 1, samples.Count / 2);
        (int earlyFloor, _) = HandleRange(samples, baselineIndex, midpoint);
        (int lateFloor, int latePeak) = HandleRange(samples, midpoint, samples.Count);
        double tailHours = Math.Max(0.01, (last.Seconds - samples[Math.Min(midpoint, samples.Count - 1)].Seconds) / 3600.0);
        double handleRate = GrowthRate(earlyFloor, lateFloor, tailHours);

        Console.WriteLine();
        Console.WriteLine("=== soak verdict ===");
        Console.WriteLine($"window            : {last.Seconds - first.Seconds:0}s ({samples.Count} samples), measured from {baseline.Seconds:0}s ({hours:0.00} h)");
        Check("cpu average", $"{averageCpu:0.00}% (budget 2%)", averageCpu < 2.0);
        if (config.CaptureGroundTruth)
        {
            Check(
                "ground-truth dumps",
                $"{last.DumpCount} frame(s), {last.DumpBytes / 1024 / 1024} MB of {GroundTruthStore.DefaultMaxBytes / 1024 / 1024} MB cap",
                last.DumpBytes <= GroundTruthStore.DefaultMaxBytes);
        }
        else
        {
            Check(
                "ground-truth dumps",
                $"{last.DumpCount} frame(s), {last.DumpBytes / 1024 / 1024} MB (test mode off, must be empty)",
                last.DumpCount == 0,
                "cleared at session start and swept every 30 minutes");
        }

        Check(
            "abandoned temp files",
            $"{last.TempFiles} partial file(s), oldest {last.TempOldestSeconds:0}s",
            last.TempOldestSeconds < 1500,
            "in-flight writes are expected; anything past the 20-minute sweep is not");
        Check("index WAL", $"{last.WalBytes / 1024} KB after {hours:0.0} h", last.WalBytes <= 16L * 1024 * 1024, "checkpointed every 3 minutes");
        Check(
            "handles",
            $"{earlyFloor} -> {lateFloor} floor in the window (window peak {latePeak}, {handleRate:0}/h)",
            handleRate < 500,
            "floor of four GetProcessHandleCount reads per sample; waves are not growth");
        Check("managed memory", $"{baseline.ManagedMb:0} MB -> {last.ManagedMb:0} MB ({managedRate:0.0} MB/h)", managedRate < 32, "GC pressure, not a cap");
        Check("working set", $"{baseline.WorkingSetMb:0} MB -> {last.WorkingSetMb:0} MB ({workingSetRate:0.0} MB/h)", workingSetRate < 128);
        Check("asset queue", $"{last.QueueDepth} payload(s) pending", last.QueueDepth <= 2048, "bounded queue gives backpressure, not growth");
        Check("last error", last.Error ?? "none", last.Error is null);
        Console.WriteLine();
        Console.WriteLine($"capture           : {last.Frames} frame(s), {last.TilesStored} tile(s) stored, {last.LogEntries} log entries");
        Console.WriteLine($"store on disk     : {last.SessionBytes / 1024} KB session, {last.AssetBytes / 1024 / 1024} MB assets in {last.AssetCount} file(s)");
        Console.WriteLine($"extrapolated      : {last.SessionBytes / 1024.0 / 1024.0 / hours:0.00} MB/hour session, "
                          + $"{last.AssetBytes / 1024.0 / 1024.0 / hours:0.00} MB/hour assets");
        if (sourceName.Contains("synthetic", StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine("note              : the synthetic source drives frames as fast as it can, so its CPU figure is a ceiling, not a forecast.");
        }
    }

    /// <summary>Lowest and highest handle count across a slice of the samples.</summary>
    private static (int Floor, int Peak) HandleRange(List<Sample> samples, int start, int end)
    {
        int floor = int.MaxValue;
        int peak = 0;
        for (int i = Math.Max(0, start); i < Math.Min(end, samples.Count); i++)
        {
            floor = Math.Min(floor, samples[i].Handles);
            peak = Math.Max(peak, samples[i].Handles);
        }

        return (floor == int.MaxValue ? 0 : floor, peak);
    }

    /// <summary>Growth per hour for a counter; shrinking is not treated as a leak.</summary>
    private static double GrowthRate(double first, double last, double hours)
        => Math.Max(0, last - first) / Math.Max(0.01, hours);

    private static void Check(string name, string detail, bool ok, string? note = null)
        => Console.WriteLine($"[{(ok ? " ok " : " !! ")}] {name,-20} : {detail}{(note is null ? string.Empty : $" — {note}")}");

    private static (int Count, long Bytes) CountFiles(string directory, string pattern)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        int count = 0;
        long bytes = 0;
        try
        {
            foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles(pattern))
            {
                count++;
                bytes += file.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A file can be swept while this counts: the next sample has the true figure.
        }

        return (count, bytes);
    }

    /// <summary>
    /// Lowest handle count across a few reads, a few tens of milliseconds apart, straight from
    /// <c>GetProcessHandleCount</c>. A single read says more about what the process was doing at that instant
    /// than about the process: an in-flight store walk or a burst of tile writes adds a wave of handles that is
    /// gone by the next sample, and reading it as growth would make every run look like it leaks. The floor is
    /// the number that would still be there tomorrow.
    /// </summary>
    private static int QuietHandleCount()
    {
        IntPtr self = NativeMethods.GetCurrentProcess();
        int lowest = int.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            if (NativeMethods.GetProcessHandleCount(self, out int count))
            {
                lowest = Math.Min(lowest, count);
            }

            Thread.Sleep(40);
        }

        return lowest == int.MaxValue ? 0 : lowest;
    }

    /// <summary>
    /// Counts <c>*.part</c> files in the temp folders and reports the age of the oldest one. Age is the
    /// number that matters: a partial file that exists right now is a tile being written, while one that
    /// survived past the 20-minute sweep is abandoned â€” and a count alone cannot tell the two apart.
    /// </summary>
    private static (int Count, double OldestSeconds) CountTempParts(params string[] directories)
    {
        int count = 0;
        double oldest = 0;
        DateTime now = DateTime.UtcNow;

        foreach (string directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            try
            {
                foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles("*.part"))
                {
                    count++;
                    oldest = Math.Max(oldest, (now - file.LastWriteTimeUtc).TotalSeconds);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file can be swept while this counts: the next sample has the true figure.
            }
        }

        return (count, oldest);
    }

    private static long FileLength(string path)
    {
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
