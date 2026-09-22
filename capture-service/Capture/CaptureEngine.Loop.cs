using System.Diagnostics;
using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.CaptureService.Interop;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    /// <summary>
    /// How long a shutdown or a fatal-error path will wait for queued tiles before giving up and leaving the
    /// last interval unwritten. Sized for a writer that is behind by a full queue (2048 payloads at ~5 ms each)
    /// without letting a stalled disk keep the service from stopping.
    /// </summary>
    private static readonly TimeSpan ShutdownFlushBudget = TimeSpan.FromSeconds(15);

    /// <summary>Runs the capture loop until the token is cancelled.</summary>
    internal void Run(CancellationToken token)
    {
        NativeMethods.EnterThreadBackgroundMode();
        _stats.MonitorsActive = _source.Monitors.Count;
        if (_source.Monitors.Count > 0)
        {
            WriteCheckpoint(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, force: true);
        }

        while (!token.IsCancellationRequested)
        {
            try
            {
                if (_paused)
                {
                    _forceFullRescan = true;
                    Thread.Sleep(150);
                    continue;
                }

                if (!CheckDiskSpace())
                {
                    Thread.Sleep(TimeSpan.FromSeconds(15));
                    continue;
                }

                // A backend that cannot see the desktop right now (locked session, disconnected Remote Desktop
                // session, a mode it cannot duplicate, a second instance) is idle, not broken, and nothing may be
                // recorded while it lasts - see RecoveringDxgiSource. This also brings the session's monitor list
                // up to date the moment a display appears.
                SyncSource();

                // Tell the backend whether the pixels outside the dirty rects are about to be needed. A partial
                // readback is only safe while the canvas is trusted: during a full rescan the untouched tiles
                // would otherwise be hashed from pixels belonging to an earlier frame.
                _source.FullFrameRequired = _forceFullRescan || _owedRescan;

                TimeSpan timeout = _cadence.NextTimeout();
                long acquireTicks = Stopwatch.GetTimestamp();
                bool acquired = _source.TryAcquire(timeout, out SourceFrame frame);
                double acquireMs = Stopwatch.GetElapsedTime(acquireTicks).TotalMilliseconds;
                _stats.AverageAcquireMs = _stats.AverageAcquireMs == 0
                    ? acquireMs
                    : (_stats.AverageAcquireMs * 0.9) + (acquireMs * 0.1);

                if (!acquired)
                {
                    _cadence.OnIdle(timeout);
                    Maintenance();
                    continue;
                }

                long startedTicks = Stopwatch.GetTimestamp();
                bool processed;
                try
                {
                    processed = ProcessFrame(frame);
                }
                finally
                {
                    _source.Release(frame);
                }

                double processMs = Stopwatch.GetElapsedTime(startedTicks).TotalMilliseconds;
                _stats.AverageProcessMs = _stats.AverageProcessMs == 0
                    ? processMs
                    : (_stats.AverageProcessMs * 0.8) + (processMs * 0.2);

                _stats.FramesAcquired++;
                if (processed)
                {
                    _stats.FramesWithChanges++;
                    _cadence.OnFrameProcessed(processMs);
                    _stats.AverageFrameMs = Math.Round(_cadence.AverageFrameMs, 2);
                    _stats.ThrottleLevel = _cadence.ThrottleLevel;
                    _stats.PaceIntervalMs = Math.Round(_cadence.PaceIntervalMs, 1);

                    // The loop paces itself here rather than relying on the acquire timeout. A desktop that
                    // presents changes 60 times a second would otherwise be processed 60 times a second, because
                    // the only other lever - the timeout handed to AcquireNextFrame - does nothing at all while a
                    // frame is always ready (spec 5.1 / 5.8).
                    TimeSpan pace = _cadence.NextDelay();
                    if (pace > TimeSpan.Zero)
                    {
                        _stats.FramesPaced++;
                        Thread.Sleep(pace);
                    }
                }

                Maintenance();
            }
            catch (DuplicationLostException ex)
            {
                // Rebuilding is not automatically an error: a desktop switch, a mode change, a locked workstation
                // and a full-screen application all land here. RebuildSource judges whether this one is a fault.
                RebuildSource(ex);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                          or Microsoft.Data.Sqlite.SqliteException)
            {
                _stats.LastError = ex.Message;
                Thread.Sleep(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                // Never let an unexpected fault kill the recorder silently: report it, back off, and
                // keep the loop alive so a single bad frame cannot end the session.
                _stats.LastError = $"{ex.GetType().Name}: {ex.Message}";
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }

        // Shutdown gets a budget: a service that cannot be stopped is worse than a lost interval, and the
        // flush is skipped rather than half-done when it expires (see FlushSessionState).
        FlushSessionState(ShutdownFlushBudget);
    }

    /// <summary>
    /// Records a fatal error from outside the loop (used by the host when the task faults). Bounded like
    /// shutdown: the process is going away either way, and the interval stays atomic if the writer cannot
    /// finish in time.
    /// </summary>
    internal void ReportFatal(Exception exception)
    {
        _stats.LastError = $"capture loop stopped: {exception}";
        _stats.FatalException = exception;
        FlushSessionState(ShutdownFlushBudget);
    }

    /// <summary>
    /// Rebuilds the backend after a lost duplication session or a display topology change, and decides whether the
    /// failure is worth reporting as an error.
    /// </summary>
    private void RebuildSource(DuplicationLostException? cause = null)
    {
        _stats.DuplicationRebuilds++;
        try
        {
            if (_source.TryRecreate(out string? error))
            {
                _stats.MonitorsActive = _source.Monitors.Count;
                _forceFullRescan = true;
                _cadence.Reset();
                _stats.LastError = null;
                _session.UpdateMonitors(_source.Monitors, DateTimeOffset.Now);
                return;
            }

            // Why the rebuild failed decides where it is reported. A locked session, a disconnected Remote Desktop
            // session, a second instance capturing the same display and a desktop mode that cannot be duplicated
            // are all expected, clear on their own, and belong on the state line - reporting them as capture
            // errors would train the user to ignore the ones that matter (spec 13). The source's own backoff paces
            // the next attempt in that case, so the loop just idles.
            SourceBlock? block = _source.Block;
            if (block is { IsExpected: true })
            {
                _stats.LastError = null;
                return;
            }

            _stats.LastError = error ?? cause?.Message ?? "capture backend rebuild failed";
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or IOException)
        {
            _stats.LastError = ex.Message;
        }

        Thread.Sleep(TimeSpan.FromSeconds(2));
    }

    /// <summary>
    /// Brings the session and the canvas up to date when the capture backend becomes usable again, or when the set
    /// of capturable displays changes.
    ///
    /// Both cases need the same thing before anything is recorded: the session's monitor list has to describe the
    /// display that is actually there, and whatever happened while nothing was being captured cannot be assumed
    /// to be in the canvas - so the next frame is a full rescan rather than a dirty-rect delta.
    /// </summary>
    private void SyncSource()
    {
        bool blocked = _source.Block is not null;
        if (blocked)
        {
            if (!_wasBlocked)
            {
                _wasBlocked = true;
                _forceFullRescan = true;
                _cadence.Reset();
            }

            return;
        }

        IReadOnlyList<MonitorInfo> monitors = _source.Monitors;
        if (!_wasBlocked && (monitors.Count == 0 || monitors.Count == _stats.MonitorsActive))
        {
            return;
        }

        _wasBlocked = false;
        _stats.MonitorsActive = monitors.Count;
        if (monitors.Count > 0)
        {
            _session.UpdateMonitors(monitors, DateTimeOffset.Now);
            foreach (MonitorInfo monitor in monitors)
            {
                // Seeds a canvas for a display that has just appeared, and forces the rescan for it.
                GetCanvas(monitor);
            }
        }

        _forceFullRescan = true;
        _cadence.Reset();
        _stats.LastMaintenance = $"capturing {monitors.Count} display(s)";
    }

    /// <summary>True while the backend has been reported as blocked, so the rescan happens once per transition.</summary>
    private bool _wasBlocked;
}
