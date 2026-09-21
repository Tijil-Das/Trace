using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// Long-run housekeeping. A recorder meant to run for days has to actively reclaim what it creates;
/// otherwise the things that grow — dumps, temp files, WAL pages, an over-budget day — become the reason
/// the machine slows down or the disk fills.
/// </summary>
internal sealed partial class CaptureEngine
{
    /// <summary>Housekeeping cadence: often enough to stay ahead, rare enough to be invisible.</summary>
    private static readonly TimeSpan TempCleanupInterval = TimeSpan.FromMinutes(20);

    private static readonly TimeSpan WalCheckpointInterval = TimeSpan.FromMinutes(3);

    /// <summary>How often stale dump folders are swept when test mode is off.</summary>
    private static readonly TimeSpan DumpSweepInterval = TimeSpan.FromMinutes(30);

    private long _lastTempCleanupMs;
    private long _lastWalCheckpointMs;
    private long _lastDumpSweepMs;

    /// <summary>Runs the periodic maintenance jobs from the capture loop.</summary>
    private void RunHousekeeping(long nowMs)
    {
        if (nowMs - _lastTempCleanupMs >= TempCleanupInterval.TotalMilliseconds)
        {
            _lastTempCleanupMs = nowMs;
            CleanupTemporaryFiles();
        }

        if (nowMs - _lastWalCheckpointMs >= WalCheckpointInterval.TotalMilliseconds)
        {
            _lastWalCheckpointMs = nowMs;
            CheckpointIndexWal();
        }

        if (nowMs - _lastDumpSweepMs >= DumpSweepInterval.TotalMilliseconds)
        {
            _lastDumpSweepMs = nowMs;
            SweepGroundTruthDumps();
        }

        EnforceDailyBudget();
    }

    /// <summary>
    /// Deletes abandoned `.part` files. They only appear when a write was interrupted (crash, forced
    /// shutdown, AV interference), but leaving them around is exactly the slow leak that shows up after a
    /// day of running.
    /// </summary>
    private void CleanupTemporaryFiles()
    {
        try
        {
            (int temp, long tempBytes) = _session.Assets.CleanupTempFilesDetailed(TimeSpan.FromHours(1));
            (int checkpoints, long checkpointBytes) = CleanupStaleFiles(_session.CheckpointDir, "*.part", TimeSpan.FromHours(1));
            (int dumps, long dumpBytes) = CleanupStaleFiles(_session.GroundTruthDir, "*.part", TimeSpan.FromHours(1));
            int removed = temp + checkpoints + dumps;
            long freed = tempBytes + checkpointBytes + dumpBytes;

            if (removed > 0)
            {
                _stats.BytesReclaimedByHousekeeping += freed;
                _stats.LastMaintenance =
                    $"housekeeping: removed {removed} abandoned temp file(s), freed {freed / 1024} KB";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _stats.LastError = $"temp cleanup failed: {ex.Message}";
        }
    }

    private static (int Deleted, long Bytes) CleanupStaleFiles(string directory, string pattern, TimeSpan olderThan)
    {
        if (!Directory.Exists(directory))
        {
            return (0, 0);
        }

        int removed = 0;
        long bytes = 0;
        DateTime cutoff = DateTime.UtcNow - olderThan;
        foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles(pattern))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    long size = file.Length;
                    file.Delete();
                    removed++;
                    bytes += size;
                }
            }
            catch (IOException)
            {
            }
        }

        return (removed, bytes);
    }

    /// <summary>
    /// Truncates the SQLite write-ahead log. WAL files grow with every transaction until a checkpoint runs,
    /// so a day of window-span updates would otherwise leave a fat `index.db-wal` behind.
    /// </summary>
    private void CheckpointIndexWal()
    {
        try
        {
            _index?.CheckpointWal();
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _stats.LastError = $"index checkpoint failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Keeps one day inside its storage budget. Over budget the engine reduces fidelity instead of silently
    /// filling the disk: first by switching to the smaller "balanced" codec, then by pausing and saying why.
    /// </summary>
    private void EnforceDailyBudget()
    {
        long budgetBytes = (long)_config.MaxDailyMegabytes * 1024 * 1024;
        if (budgetBytes <= 0)
        {
            return;
        }

        long used = _log.EntryBytesWritten + _assetWriter.BytesWritten;
        if (used < budgetBytes)
        {
            return;
        }

        if (!_stats.BudgetThrottled && _codec.IsLossless && _codec != TileCodecs.Balanced)
        {
            // Budget pressure goes down one fidelity step, never up: archive → lossless → balanced. The archive
            // codec is already the densest lossless one, so an over-budget archive day falls back to cheap and
            // fast rather than to a different kind of losslessness.
            _codec = _codec == TileCodecs.Archive ? TileCodecs.Lossless : TileCodecs.Balanced;
            _stats.BudgetThrottled = true;
            _stats.LastMaintenance =
                $"daily budget reached ({used / 1024 / 1024} MB): switched to balanced compression";
            return;
        }

        if (used >= budgetBytes * 2 && !_paused)
        {
            Pause();
            _stats.LastError =
                $"daily storage budget exceeded ({used / 1024 / 1024} MB of {_config.MaxDailyMegabytes} MB) — "
                + "recording paused. Purge recent minutes, raise the budget, or resume manually.";
        }
    }

    /// <summary>
    /// Clears leftovers at session start: ground-truth dumps when test mode is off (they are only useful
    /// immediately after a run), plus abandoned temp files.
    /// </summary>
    private void PrepareSessionStorage()
    {
        try
        {
            if (_config.CaptureGroundTruth)
            {
                GroundTruthStore.Trim(_session.GroundTruthDir);
            }
            else
            {
                (int deleted, long freed) = GroundTruthStore.ClearAll(_session.Root);
                if (deleted > 0)
                {
                    _stats.BytesReclaimedByHousekeeping += freed;
                    _stats.LastMaintenance = $"cleared {deleted} stale ground-truth dump(s), freed {freed / 1024 / 1024} MB";
                }
            }

            _session.Assets.CleanupTempFiles(TimeSpan.FromHours(1));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _stats.LastError = $"session storage preparation failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Re-sweeps dump folders while running. Only reaches the file system when test mode is off (dumps
    /// are live data while it is on) and only every half hour, but that is what stops a crash or a mode
    /// change earlier in the day from leaving multi-gigabyte leftovers on the disk for weeks.
    /// </summary>
    private void SweepGroundTruthDumps()
    {
        if (_config.CaptureGroundTruth)
        {
            return;
        }

        try
        {
            (int deleted, long freed) = GroundTruthStore.ClearAll(_session.Root);
            if (deleted > 0)
            {
                _stats.BytesReclaimedByHousekeeping += freed;
                _stats.LastMaintenance = $"swept {deleted} stale ground-truth dump(s), freed {freed / 1024 / 1024} MB";
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _stats.LastError = $"ground-truth sweep failed: {ex.Message}";
        }
    }
}
