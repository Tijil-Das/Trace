using System.Diagnostics;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// Live counters published to the dashboard (spec 8: "what is this actually costing me"). Reset at
/// process start; CPU is sampled from the process itself rather than guessed.
/// </summary>
internal sealed class CaptureStats
{
    private readonly Process _process = Process.GetCurrentProcess();
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleUtc = DateTime.UtcNow;

    internal DateTimeOffset StartedUtc { get; } = DateTimeOffset.UtcNow;

    internal long FramesAcquired { get; set; }

    internal long FramesWithChanges { get; set; }

    internal long FramesSkippedExcluded { get; set; }

    internal long FramesSkippedProtected { get; set; }

    /// <summary>Frames DXGI handed over with no regions to process (pointer-only presents).</summary>
    internal long FramesSkippedNoChange { get; set; }

    /// <summary>Frames whose full-surface rescan was deferred by the rescan rate limit.</summary>
    internal long FramesRescanDeferred { get; set; }

    /// <summary>Dirty rects DXGI reported for the most recent frame.</summary>
    internal int LastDirtyRects { get; set; }

    /// <summary>Move rects DXGI reported for the most recent frame.</summary>
    internal int LastMoveRects { get; set; }

    /// <summary>Smoothed time spent inside ProcessFrame, in milliseconds.</summary>
    internal double AverageProcessMs { get; set; }

    /// <summary>Smoothed time spent waiting for the next frame, in milliseconds.</summary>
    internal double AverageAcquireMs { get; set; }

    /// <summary>GPU copy + CPU readback time of the most recent frame, in milliseconds.</summary>
    internal double LastReadbackMs { get; set; }

    /// <summary>Tile hashing time of the most recent frame, in milliseconds.</summary>
    internal double LastHashMs { get; set; }

    /// <summary>QOI encoding time of the most recent frame, in milliseconds.</summary>
    internal double LastEncodeMs { get; set; }

    /// <summary>Asset store writes (compress + fsync-free write + rename) of the most recent frame, in ms.</summary>
    internal double LastStoreMs { get; set; }

    /// <summary>Log append time of the most recent frame, in milliseconds.</summary>
    internal double LastLogMs { get; set; }

    /// <summary>Tile payloads queued for the background writer and not yet on disk.</summary>
    internal int AssetQueueDepth { get; set; }

    /// <summary>Tiles the codec was asked to encode in the most recent frame.</summary>
    internal int LastEncodedTiles { get; set; }

    /// <summary>
    /// Wall time of the tile loop for the last frame, when the per-tile phase split is off. Zero when
    /// <see cref="DetailedTiming"/> is on, because then the split carries the same information at finer grain.
    /// </summary>
    internal double LastTileLoopMs { get; set; }

    /// <summary>True when the per-tile phase split (hash/encode/store/log) was measured for the last frame.</summary>
    internal bool DetailedTiming { get; set; }

    /// <summary>Hashes the dedupe cache was seeded with when the session opened.</summary>
    internal int DedupeCacheSeeded { get; set; }

    /// <summary>Log records dropped because they were zero-filled (writer integrity faults).</summary>
    internal long LogZeroRecordFaults { get; set; }

    /// <summary>True when something outside this writer was appending to the log file.</summary>
    internal bool LogExternalWriterDetected { get; set; }

    /// <summary>Ground-truth frames written since the engine started (test mode).</summary>
    internal long GroundTruthFrames { get; set; }

    /// <summary>Ground-truth frames deleted to stay inside the rolling-window caps.</summary>
    internal long GroundTruthTrimmed { get; set; }

    /// <summary>Bytes reclaimed by housekeeping (temp files, ground-truth trims, pruned days).</summary>
    internal long BytesReclaimedByHousekeeping { get; set; }

    /// <summary>True when the daily storage budget forced a fidelity reduction.</summary>
    internal bool BudgetThrottled { get; set; }

    internal long FullFrameRescans { get; set; }

    internal long TilesHashed { get; set; }

    internal long TilesDeduped { get; set; }

    internal long TilesStored { get; set; }

    internal long LogEntriesWritten { get; set; }

    internal long AssetsBytesWritten { get; set; }

    internal long SessionsRolled { get; set; }

    internal long DuplicationRebuilds { get; set; }

    internal string? LastError { get; set; }

    /// <summary>Result of the last maintenance pass (pruning/GC), for the dashboard.</summary>
    internal string? LastMaintenance { get; set; }

    /// <summary>Set when the capture loop faulted; the CLI prints its full stack for diagnosis.</summary>
    internal Exception? FatalException { get; set; }

    internal string? ForegroundApp { get; set; }

    internal bool Paused { get; set; }

    internal bool Excluded { get; set; }

    internal DateTimeOffset? LastFrameUtc { get; set; }

    internal int MonitorsActive { get; set; }

    internal double AverageFrameMs { get; set; }

    internal int ThrottleLevel { get; set; }

    /// <summary>Pause the cadence is currently inserting between frames, in milliseconds (0 when unpaced).</summary>
    internal double PaceIntervalMs { get; set; }

    /// <summary>Frames the loop deliberately waited after, to stay inside the CPU budget.</summary>
    internal long FramesPaced { get; set; }

    /// <summary>Dedupe cache lookups that found the hash without touching the store.</summary>
    internal long DedupeCacheHits { get; set; }

    /// <summary>Dedupe cache lookups that escaped to a filesystem existence probe on the capture thread.</summary>
    internal long DedupeCacheMisses { get; set; }

    /// <summary>Hashes the dedupe cache currently remembers.</summary>
    internal int DedupeCacheEntries { get; set; }

    /// <summary>Process CPU percentage since the previous sample (0 when called too soon).</summary>
    internal double SampleCpuPercent()
    {
        _process.Refresh();
        TimeSpan cpu = _process.TotalProcessorTime;
        DateTime now = DateTime.UtcNow;
        double elapsedMs = (now - _lastCpuSampleUtc).TotalMilliseconds;
        if (elapsedMs <= 100)
        {
            return 0;
        }

        double cpuMs = (cpu - _lastCpuTime).TotalMilliseconds;
        _lastCpuTime = cpu;
        _lastCpuSampleUtc = now;
        return Math.Round(100.0 * cpuMs / (elapsedMs * Environment.ProcessorCount), 3);
    }

    /// <summary>Resident memory in megabytes.</summary>
    internal double WorkingSetMb
    {
        get
        {
            _process.Refresh();
            return Math.Round(_process.WorkingSet64 / 1024.0 / 1024.0, 1);
        }
    }

    /// <summary>Uptime in seconds.</summary>
    internal double UptimeSeconds => Math.Round((DateTimeOffset.UtcNow - StartedUtc).TotalSeconds, 1);
}
