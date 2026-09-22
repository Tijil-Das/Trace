using System.Diagnostics;

using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Cli;
using ScreenRecall.CaptureService.Ipc;
using ScreenRecall.CaptureService.Service;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService;

/// <summary>
/// <c>--cpu-bench [minutes]</c>: the standing check behind the spec's one hard performance requirement
/// (spec 3: steady-state CPU below 2%, spec 5.8 governance).
///
/// Two phases are measured back to back in one process: a static screen, then a driven active desktop. The
/// run ends with per-metric verdicts against the budget and a single machine-readable result line, so "did
/// this change cost CPU?" is answered by a command instead of an argument.
///
/// Meant to be run once per capture-loop change, never continuously: minutes per phase is the spec's own
/// window, and a shorter run only validates the harness. The signature is <c>--cpu-bench 5</c> for the real
/// thing (5 min idle + 5 min active) and <c>--cpu-bench 0.25</c> for a smoke test of the harness itself.
/// </summary>
internal static class CpuBenchCommand
{
    /// <summary>Spec 3 ceiling, as a percentage of total machine CPU (the Task Manager convention).</summary>
    internal const double TargetPercent = 2.0;

    /// <summary>
    /// A static screen is held to a quarter of the ceiling on purpose: the spec's 2% has to cover active use,
    /// and a recorder that spends most of it while nothing is happening has already spent the budget.
    /// </summary>
    internal const double IdleTargetPercent = 0.5;

    /// <summary>
    /// Compression must be skipped for most of what is hashed, or the dedupe layer is not doing its job
    /// (spec 5.3). A desktop is mostly re-drawn content, so a low number means tiles are being re-encoded.
    /// </summary>
    internal const double DedupeTargetPercent = 80.0;

    /// <summary>
    /// Presents tolerated during the "static" phase. Zero is the ideal, but a taskbar clock or a tray icon can
    /// present on its own; a handful of presents over minutes cannot move a five-minute average, while a
    /// continuous stream of them means the machine was not idle and the number must not be certified.
    /// </summary>
    private const int MaxIdlePresents = 10;

    /// <summary>
    /// Excluded from both phases on purpose: the first seconds of a run include a full-surface rescan and the
    /// store walk that seeds the size statistics - real costs, but not steady-state ones.
    /// </summary>
    private static readonly TimeSpan Warmup = TimeSpan.FromSeconds(10);

    /// <summary>Exit code meaning "this measurement is not valid input to the budget".</summary>
    internal const int InvalidMeasurement = 2;

    /// <summary>Scripted workload the fidelity harness uses to drive a visible desktop (dev data, gitignored).</summary>
    private const string ActivityScript = "screen-activity.ps1";

    internal static int Run(RecallConfig config, CommandLine commandLine)
    {
        RecallConfig effective = config.Clone().Normalize();
        if (commandLine.RootOverride is null && !commandLine.Synthetic && !commandLine.ConfigExplicit)
        {
            // A bench is not a recording: keep it out of the user's store unless one was named.
            effective.StoragePath = Path.Combine(
                Path.GetTempPath(),
                "screen-recall-cpu-bench",
                DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        }

        // Minutes per phase, with a floor low enough to make a harness smoke test quick and a ceiling that
        // stops a typo from starting a multi-hour run.
        TimeSpan phase = TimeSpan.FromMinutes(Math.Clamp(commandLine.CpuBenchMinutes, 5.0 / 60.0, 60));
        TimeSpan interval = TimeSpan.FromSeconds(Math.Clamp(phase.TotalSeconds / 12, 1, 5));
        string? activity = ResolveActivity(commandLine.CpuBenchActivity, out string? activityNote);

        // The per-tile phase split is off by default because it costs real CPU (23% of capture-thread samples in
        // the §5a trace). The bench therefore measures what production actually runs; `--detailed-timing` turns the
        // split on when you want to know where a frame went, and the report says which mode produced it.
        effective.DetailedTiming = commandLine.DetailedTiming;

        IFrameSource source = CaptureWorker.CreateSource(effective, commandLine, out string? sourceNote);
        bool synthetic = source.Name.Contains("synthetic", StringComparison.OrdinalIgnoreCase);

        Console.WriteLine($"cpu-bench    : {Duration(phase)} idle + {Duration(phase)} active "
                          + $"(spec 3 ceiling {TargetPercent:0.00}% of total machine CPU, sampled every {Duration(interval)})");
        Console.WriteLine($"build        : {BuildConfiguration.Description}");
        Console.WriteLine($"timing       : {(effective.DetailedTiming
            ? "per-tile phase split ON (--detailed-timing: this costs CPU, so the number is not a production number)"
            : "coarse (production mode: no per-tile stopwatches)")}");
        Console.WriteLine($"cores        : {Environment.ProcessorCount} logical (CPU% is normalised across all of them)");
        Console.WriteLine($"source       : {source.Name}{(sourceNote is null ? string.Empty : $" ({sourceNote})")}");
        Console.WriteLine($"root         : {effective.StoragePath}");
        Console.WriteLine(activity is null
            ? "active phase : no scripted workload - use the machine normally (type, scroll, switch windows)"
            : $"active phase : {activity}");
        if (activityNote is not null)
        {
            Console.WriteLine($"[ !! ] activity       : {activityNote}");
        }

        if (source.Block is { } block)
        {
            // Say it before measuring anything: with no capturable output both phases measure an idle loop, and a
            // number from that is not a budget measurement (spec 13).
            Console.WriteLine(
                $"[ !! ] source block   : {block.Describe()} - nothing is being recorded; "
                + $"the service retries every {Math.Max(1, block.RetryInMs / 1000)}s. Unlock the session or use "
                + "--synthetic-test to exercise the pipeline anyway.");
        }

        Console.WriteLine();
        Console.WriteLine("IDLE PHASE: leave the mouse and keyboard alone, and close anything that animates.");
        Console.WriteLine();

        CaptureController controller = new(commandLine.ConfigPath, effective);
        controller.Start(source, out CaptureIpcServer server);
        using Process process = Process.GetCurrentProcess();

        try
        {
            Console.WriteLine($"warming up   : {Duration(Warmup)} excluded from both phases "
                              + "(JIT, first full rescan, stat seeding)");
            Thread.Sleep(Warmup);
            Console.WriteLine();

            PhaseResult idle = Measure(controller, process, "phase 1: static screen", phase, interval, null);
            PhaseResult active = Measure(controller, process, "phase 2: active use", phase, interval, activity);

            // A source that cannot see the desktop measures an idle loop, not a recorder: the numbers are real but
            // they describe nothing, so the run cannot be certified (spec 13).
            bool sourceWasBlocked = source.Block is not null;
            bool idleStatic = idle.Presents == 0;
            bool idleQualified = idle.Presents <= MaxIdlePresents;
            bool activeObserved = active.TilesHashed > 0;
            bool optimized = BuildConfiguration.IsOptimized;

            bool failed = !optimized
                          || !idleQualified
                          || !activeObserved
                          || idle.AveragePercent >= IdleTargetPercent
                          || active.AveragePercent >= TargetPercent
                          || active.CompressionSkippedPercent < DedupeTargetPercent
                          || idle.Error is not null
                          || active.Error is not null;

            // A run that recorded nothing is not a cheap recorder - it is a run where the desktop was never
            // visible (a locked session, a secure desktop, an exclusion that caught everything). Certifying
            // that as a pass is exactly the failure mode a budget check exists to prevent.
            bool invalid = !optimized || !idleQualified || !activeObserved || sourceWasBlocked
                           || (synthetic && idle.Presents > 0);

            Verdict(idle, active, optimized, idleStatic, idleQualified, activeObserved, synthetic, sourceWasBlocked);
            Console.WriteLine($"cpu-bench result: idle={idle.AveragePercent:0.000}% active={active.AveragePercent:0.000}% "
                              + $"target={TargetPercent:0.000}% idle-presents={idle.Presents} "
                              + $"dedupe={active.CompressionSkippedPercent:0.0}% "
                              + $"verdict={(invalid ? "INVALID" : failed ? "FAIL" : "PASS")}");
            Console.WriteLine("(record this line: it is the whole point of the command)");

            return invalid ? InvalidMeasurement : failed ? 1 : 0;
        }
        finally
        {
            server.Dispose();
            controller.Stop();
        }
    }

    /// <summary>
    /// Measures one phase and returns its deltas. CPU is the phase integral - process CPU time over wall time,
    /// normalised by the core count - rather than an average of samples, so the figure cannot be skewed by when
    /// a sample happened to land. Counters are reported as deltas so both phases read on their own scale.
    /// </summary>
    private static PhaseResult Measure(
        CaptureController controller,
        Process process,
        string label,
        TimeSpan duration,
        TimeSpan interval,
        string? activity)
    {
        StatusDto before = controller.GetStatus();
        TimeSpan cpuStart = ProcessCpu(process);
        TimeSpan cpuPrevious = cpuStart;
        long startTicks = Stopwatch.GetTimestamp();
        long previousTicks = startTicks;
        double peak = 0;
        int samples = 0;
        int peakQueue = 0;

        Console.WriteLine($"=== {label} ({Duration(duration)}) ===");
        Console.WriteLine("  elapsed   cpu%   peak  presents  changed   tiles  stored  deduped    log  acquire  process  queue");

        if (activity is not null)
        {
            Console.WriteLine($"  driving the desktop with {Path.GetFileName(activity)}");
        }

        using Process? driver = StartActivity(activity, duration);
        try
        {
            while (Stopwatch.GetElapsedTime(startTicks) < duration)
            {
                Thread.Sleep(interval);

                TimeSpan cpuNow = ProcessCpu(process);
                long nowTicks = Stopwatch.GetTimestamp();
                double intervalMs = Stopwatch.GetElapsedTime(previousTicks).TotalMilliseconds;
                double sample = intervalMs <= 0
                    ? 0
                    : 100.0 * (cpuNow - cpuPrevious).TotalMilliseconds / (intervalMs * Environment.ProcessorCount);
                peak = Math.Max(peak, sample);
                cpuPrevious = cpuNow;
                previousTicks = nowTicks;
                samples++;

                StatusDto live = controller.GetStatus();
                peakQueue = Math.Max(peakQueue, live.AssetQueueDepth);
                Console.WriteLine(
                    $"  {Stopwatch.GetElapsedTime(startTicks).TotalSeconds,6:0}s {sample,6:0.00} {peak,6:0.00} "
                    + $"{live.FramesAcquired - before.FramesAcquired,9} {live.FramesWithChanges - before.FramesWithChanges,8} "
                    + $"{live.TilesHashed - before.TilesHashed,7} {live.TilesStored - before.TilesStored,7} "
                    + $"{live.TilesDeduped - before.TilesDeduped,8} {live.LogEntries - before.LogEntries,6} "
                    + $"{live.AverageAcquireMs,8:0.0} {live.AverageProcessMs,8:0.0} {live.AssetQueueDepth,6}");
            }
        }
        finally
        {
            StopActivity(driver);
        }

        TimeSpan cpuEnd = ProcessCpu(process);
        double wallMs = Stopwatch.GetElapsedTime(startTicks).TotalMilliseconds;
        double average = wallMs <= 0
            ? 0
            : 100.0 * (cpuEnd - cpuStart).TotalMilliseconds / (wallMs * Environment.ProcessorCount);
        StatusDto after = controller.GetStatus();

        PhaseResult result = new(
            Label: label,
            Seconds: wallMs / 1000.0,
            AveragePercent: average,
            PeakPercent: peak,
            Samples: samples,
            Presents: after.FramesAcquired - before.FramesAcquired,
            Changed: after.FramesWithChanges - before.FramesWithChanges,
            TilesHashed: after.TilesHashed - before.TilesHashed,
            TilesStored: after.TilesStored - before.TilesStored,
            TilesDeduped: after.TilesDeduped - before.TilesDeduped,
            LogEntries: after.LogEntries - before.LogEntries,
            Rescans: after.FullFrameRescans - before.FullFrameRescans,
            SkippedNoChange: after.FramesSkippedNoChange - before.FramesSkippedNoChange,
            RescansDeferred: after.FramesRescanDeferred - before.FramesRescanDeferred,
            AverageAcquireMs: after.AverageAcquireMs,
            AverageProcessMs: after.AverageProcessMs,
            AverageFrameMs: after.AverageFrameMs,
            ThrottleLevel: after.ThrottleLevel,
            ReadbackMs: after.LastReadbackMs,
            HashMs: after.LastHashMs,
            EncodeMs: after.LastEncodeMs,
            StoreMs: after.LastStoreMs,
            LogMs: after.LastLogMs,
            PeakQueueDepth: peakQueue,
            State: after.State,
            Error: after.LastError,
            PaceIntervalMs: after.PaceIntervalMs,
            FramesPaced: after.FramesPaced - before.FramesPaced,
            DedupeCacheHits: after.DedupeCacheHits - before.DedupeCacheHits,
            DedupeCacheMisses: after.DedupeCacheMisses - before.DedupeCacheMisses,
            DedupeCacheEntries: after.DedupeCacheEntries,
            LastEncodedTiles: after.LastEncodedTiles,
            DetailedTiming: after.DetailedTiming,
            TileLoopMs: after.LastTileLoopMs,
            DedupeCacheSeeded: after.DedupeCacheSeeded);

        PrintPhase(result);
        return result;
    }

    /// <summary>
    /// Prints one phase's result block. The dedupe split is printed as three numbers because they answer
    /// different questions: an unchanged tile is skipped by identity, a known hash skips compression but still
    /// has to be referenced by the log, and only the remainder is genuinely new content.
    /// </summary>
    private static void PrintPhase(PhaseResult phase)
    {
        Console.WriteLine();
        Console.WriteLine($"--- {phase.Label} result ---");
        Console.WriteLine($"duration            : {phase.Seconds:0.0}s");
        Console.WriteLine($"average cpu         : {phase.AveragePercent:0.000}% (over {phase.Samples} sample(s))");
        Console.WriteLine($"peak sampled cpu    : {phase.PeakPercent:0.000}%");
        Console.WriteLine($"presents            : {phase.Presents} ({phase.Changed} with changes, "
                          + $"{phase.Rescans} full rescan(s), {phase.RescansDeferred} deferred)");
        Console.WriteLine($"skipped (no change) : {phase.SkippedNoChange} pointer-only present(s)");
        Console.WriteLine($"tiles hashed        : {phase.TilesHashed}");
        if (phase.TilesHashed > 0)
        {
            Console.WriteLine($"  unchanged tile    : {phase.CanvasHits} ({100.0 * phase.CanvasHits / phase.TilesHashed:0.0}%) "
                              + "- identical to what the canvas already holds, nothing to do");
            Console.WriteLine($"  known hash        : {phase.TilesDeduped} ({100.0 * phase.TilesDeduped / phase.TilesHashed:0.0}%) "
                              + "- deduped, compression skipped");
            Console.WriteLine($"  compressed+stored : {phase.TilesStored} ({100.0 * phase.TilesStored / phase.TilesHashed:0.0}%) "
                              + "- genuinely new content");
            Console.WriteLine($"compression skipped : {phase.CompressionSkippedPercent:0.0}% of hashed tiles");
        }

        Console.WriteLine($"log entries         : {phase.LogEntries}");
        Console.WriteLine($"acquire wait        : {phase.AverageAcquireMs:0.0} ms average, process {phase.AverageProcessMs:0.0} ms average");
        Console.WriteLine($"cadence frame cost  : {phase.AverageFrameMs:0.0} ms average (what the self-throttle watches), "
                          + $"throttle level {phase.ThrottleLevel}");
        Console.WriteLine($"loop pace           : {phase.PaceIntervalMs:0.0} ms between frames "
                          + $"({phase.FramesPaced} frame(s) deliberately waited after)");
        Console.WriteLine($"dedupe cache        : {phase.DedupeCacheHitPercent:0.0}% of "
                          + $"{phase.DedupeCacheLookups} lookup(s) answered from memory "
                          + $"({phase.DedupeCacheEntries} hash(es) held, {phase.DedupeCacheSeeded} seeded from the "
                          + $"manifest at startup), {phase.DedupeCacheMisses} filesystem probe(s)");
        Console.WriteLine($"last frame phases   : {(phase.DetailedTiming
            ? $"readback {phase.ReadbackMs:0.0} ms, hash {phase.HashMs:0.0} ms, "
              + $"encode {phase.EncodeMs:0.0} ms over {phase.LastEncodedTiles} tile(s), store {phase.StoreMs:0.0} ms, log {phase.LogMs:0.0} ms"
            : $"readback {phase.ReadbackMs:0.0} ms, tile loop {phase.TileLoopMs:0.0} ms "
              + "(per-tile split off - pass --detailed-timing to break the loop down)")}");
        Console.WriteLine($"asset queue         : peak {phase.PeakQueueDepth} payload(s) pending");
        Console.WriteLine($"state               : {phase.State}");
        Console.WriteLine($"errors              : {phase.Error ?? "none"}");
        Console.WriteLine();
    }

    /// <summary>Prints the per-metric verdicts against the spec's budget.</summary>
    private static void Verdict(
        PhaseResult idle,
        PhaseResult active,
        bool optimized,
        bool idleStatic,
        bool idleQualified,
        bool activeObserved,
        bool synthetic,
        bool sourceWasBlocked)
    {
        Console.WriteLine("=== verdict (spec 3: steady-state CPU below 2%) ===");
        Check(
            "build configuration",
            BuildConfiguration.Description,
            optimized,
            "a Debug number is not evidence - rebuild Release and re-measure");
        Check(
            "static-screen CPU",
            $"{idle.AveragePercent:0.000}% average (target < {IdleTargetPercent:0.00}%)",
            idle.AveragePercent < IdleTargetPercent,
            "a static screen should cost almost nothing");
        Check(
            "screen was static",
            idleStatic ? "0 presents during the idle phase" : $"{idle.Presents} present(s) during the idle phase",
            idleQualified,
            idleStatic
                ? null
                : $"past {MaxIdlePresents} presents the idle figure describes a busy machine, not an idle one");
        Check(
            "active-use CPU",
            $"{active.AveragePercent:0.000}% average, {active.PeakPercent:0.000}% peak (target < {TargetPercent:0.00}%)",
            active.AveragePercent < TargetPercent,
            "peak is the widest single sample, not a percentile");
        Check(
            "compression skipped",
            $"{active.CompressionSkippedPercent:0.0}% of {active.TilesHashed} hashed tile(s) (target > {DedupeTargetPercent:0.0}%)",
            active.TilesHashed == 0 || active.CompressionSkippedPercent >= DedupeTargetPercent,
            active.TilesHashed == 0 ? "no tiles hashed - nothing to judge" : "dedupe is the point of the tile cache");
        Check(
            "active use observed",
            $"{active.TilesHashed} tile(s) hashed over {active.Presents} present(s)",
            activeObserved,
            "nothing was recorded: a locked session or secure desktop measures the wrong thing entirely");
        Check(
            "capturable output",
            sourceWasBlocked
                ? "no display could be duplicated when the run started"
                : "the desktop was visible to the recorder",
            !sourceWasBlocked,
            "with no output both phases measure an idle loop, not a recorder");
        Check(
            "errors",
            idle.Error ?? active.Error ?? "none",
            idle.Error is null && active.Error is null);
        if (synthetic && idle.Presents > 0)
        {
            Check(
                "real screen",
                "the synthetic source generates frames on a timer, so a static screen was never possible",
                false,
                "run without --synthetic-test on an unlocked desktop to measure the idle budget");
        }

        Console.WriteLine();
    }

    private static void Check(string name, string detail, bool ok, string? note = null)
        => Console.WriteLine($"[{(ok ? " ok " : " !! ")}] {name,-20}: {detail}{(note is null ? string.Empty : $" - {note}")}");

    /// <summary>Process CPU time; refreshed because a cached value would lag the phase it is measuring.</summary>
    private static TimeSpan ProcessCpu(Process process)
    {
        process.Refresh();
        return process.TotalProcessorTime;
    }

    /// <summary>Phase duration as "90s" or "5.00 min", so a smoke run does not read as five minutes.</summary>
    private static string Duration(TimeSpan value)
        => value.TotalMinutes >= 1 ? $"{value.TotalMinutes:0.00} min" : $"{value.TotalSeconds:0}s";

    /// <summary>
    /// Resolves the scripted workload: an explicit path, the repo's activity script found by walking up from
    /// the binary, or nothing at all (the operator drives the machine). The script is gitignored dev data, so
    /// its absence is normal and only turns the active phase into a manual one.
    /// </summary>
    private static string? ResolveActivity(string? requested, out string? note)
    {
        note = null;
        if (requested is not null)
        {
            if (requested.Equals("none", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            if (File.Exists(requested))
            {
                return Path.GetFullPath(requested);
            }

            note = $"'{requested}' was not found; the active phase will measure whatever the machine is doing";
            return null;
        }

        for (DirectoryInfo? directory = new(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            string candidate = Path.Combine(directory.FullName, "dev-data", ActivityScript);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Starts the scripted workload window for the length of the active phase.</summary>
    private static Process? StartActivity(string? activity, TimeSpan duration)
    {
        if (activity is null)
        {
            return null;
        }

        ProcessStartInfo info = new("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(activity);
        info.ArgumentList.Add("-Seconds");
        info.ArgumentList.Add($"{Math.Ceiling(duration.TotalSeconds):0}");

        try
        {
            return Process.Start(info);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.WriteLine($"  could not start the scripted workload ({ex.Message}); use the machine by hand");
            return null;
        }
    }

    /// <summary>Closes the workload window when the phase ends, whether or not its own timer fired.</summary>
    private static void StopActivity(Process? driver)
    {
        if (driver is null)
        {
            return;
        }

        try
        {
            if (!driver.WaitForExit(3000))
            {
                driver.Kill(entireProcessTree: true);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            // Already gone, or could not be signalled: the window closes on its own timer either way.
        }
        finally
        {
            driver.Dispose();
        }
    }

    /// <summary>Everything one phase measured, as deltas over that phase.</summary>
    private readonly record struct PhaseResult(
        string Label,
        double Seconds,
        double AveragePercent,
        double PeakPercent,
        int Samples,
        long Presents,
        long Changed,
        long TilesHashed,
        long TilesStored,
        long TilesDeduped,
        long LogEntries,
        long Rescans,
        long SkippedNoChange,
        long RescansDeferred,
        double AverageAcquireMs,
        double AverageProcessMs,
        double AverageFrameMs,
        int ThrottleLevel,
        double ReadbackMs,
        double HashMs,
        double EncodeMs,
        double StoreMs,
        double LogMs,
        int PeakQueueDepth,
        string State,
        string? Error,
        double PaceIntervalMs,
        long FramesPaced,
        long DedupeCacheHits,
        long DedupeCacheMisses,
        int DedupeCacheEntries,
        int LastEncodedTiles,
        bool DetailedTiming,
        double TileLoopMs,
        int DedupeCacheSeeded)
    {
        /// <summary>Hashed tiles whose content already matched the canvas: skipped without any store lookup.</summary>
        internal long CanvasHits => Math.Max(0, TilesHashed - TilesStored - TilesDeduped);

        /// <summary>Dedupe cache lookups this phase, hit or miss.</summary>
        internal long DedupeCacheLookups => DedupeCacheHits + DedupeCacheMisses;

        /// <summary>
        /// Share of dedupe lookups that were answered from memory. Every miss escapes to a filesystem existence
        /// probe on the capture thread, so this is the number the segmented cache exists to keep near 100%.
        /// </summary>
        internal double DedupeCacheHitPercent
            => DedupeCacheLookups == 0 ? 100 : 100.0 * DedupeCacheHits / DedupeCacheLookups;

        /// <summary>
        /// Share of hashed tiles that never reached the encoder - the number the dedupe layer exists to keep
        /// high. It is deliberately not <c>TilesDeduped / TilesHashed</c>: most unchanged tiles are skipped by
        /// canvas identity before the asset store is ever consulted, and counting only the store hits would
        /// report a well-deduping desktop as a cache miss.
        /// </summary>
        internal double CompressionSkippedPercent
            => TilesHashed == 0 ? 100 : 100.0 * (TilesHashed - TilesStored) / TilesHashed;
    }
}
