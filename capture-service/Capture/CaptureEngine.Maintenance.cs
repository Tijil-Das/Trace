using ScreenRecall.CaptureService.Interop;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    /// <summary>Refuses to record when the storage volume is nearly full (spec 5.8 governance).</summary>
    private bool CheckDiskSpace()
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _lastDiskCheckMs < 30_000 && _lastDiskCheckMs != 0)
        {
            return _freeDiskGb == 0 || _freeDiskGb * 1024 >= _config.MinFreeDiskMegabytes;
        }

        _lastDiskCheckMs = nowMs;
        ulong free = NativeMethods.GetFreeDiskBytes(_session.Root);
        if (free == 0)
        {
            _freeDiskGb = 0;
            return true;
        }

        _freeDiskGb = free / 1024.0 / 1024.0 / 1024.0;
        if (_freeDiskGb * 1024 >= _config.MinFreeDiskMegabytes)
        {
            return true;
        }

        _stats.LastError =
            $"free disk space is {_freeDiskGb:0.0} GB, below the {_config.MinFreeDiskMegabytes} MB floor — capture paused";
        return false;
    }

    /// <summary>Cheap per-iteration chores: log flush cadence, retention job, stats sampling.</summary>
    private void Maintenance()
    {
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;
        if (nowUs - _lastFlushUs >= 2_000_000)
        {
            _lastFlushUs = nowUs;
            FlushSessionState(deferIfBusy: true);
        }

        long nowMs = nowUs / 1000;

        // Keep the cached size numbers warm from the loop rather than from whoever happens to poll: a
        // consumer that only asks once a minute would otherwise always read the answer measured a minute
        // ago, which reads as a frozen counter. Both calls are self-throttling, and their walks run
        // off-thread, so this costs two timestamp comparisons per iteration.
        _ = AssetStats();
        _ = SessionBytes();

        try
        {
            RunHousekeeping(nowMs);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or Microsoft.Data.Sqlite.SqliteException)
        {
            _stats.LastError = $"housekeeping failed: {ex.Message}";
        }

        if (_lastPruneMs == 0)
        {
            _lastPruneMs = nowMs;
        }

        if (_pruneRunning || nowMs - _lastPruneMs < TimeSpan.FromHours(6).TotalMilliseconds)
        {
            return;
        }

        _lastPruneMs = nowMs;
        _pruneRunning = true;
        string root = _session.Root;
        int retentionDays = _config.RetentionDays;

        // Pruning touches thousands of files: keep it off the capture thread, guarded so only one runs.
        Task.Run(() =>
        {
            try
            {
                PruneReport report = new RetentionPruner(root).Prune(retentionDays, DateTimeOffset.Now);
                if (report.DaysDeleted > 0 || report.AssetsDeleted > 0)
                {
                    _dedupe.Clear();
                }

                _stats.LastMaintenance = $"pruned {report.DaysDeleted} day(s), {report.AssetsDeleted} asset(s), "
                                         + $"reclaimed {report.BytesReclaimed / 1024 / 1024} MB";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or Microsoft.Data.Sqlite.SqliteException)
            {
                _stats.LastError = $"pruning failed: {ex.Message}";
            }
            finally
            {
                _pruneRunning = false;
            }
        });
    }
}
