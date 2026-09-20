using System.Diagnostics;
using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.CaptureService.Interop;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    /// <summary>Runs the capture loop until the token is cancelled.</summary>
    internal void Run(CancellationToken token)
    {
        NativeMethods.EnterThreadBackgroundMode();
        _stats.MonitorsActive = _source.Monitors.Count;
        WriteCheckpoint(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, force: true);

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
                }

                Maintenance();
            }
            catch (DuplicationLostException ex)
            {
                _stats.LastError = ex.Message;
                RebuildSource();
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

        FlushSessionState();
    }

    /// <summary>Records a fatal error from outside the loop (used by the host when the task faults).</summary>
    internal void ReportFatal(Exception exception)
    {
        _stats.LastError = $"capture loop stopped: {exception}";
        _stats.FatalException = exception;
        FlushSessionState();
    }

    /// <summary>Rebuilds the backend after a lost duplication session or a display topology change.</summary>
    private void RebuildSource()
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

            _stats.LastError = error;
        }
        catch (Exception ex) when (ex is SharpGen.Runtime.SharpGenException or IOException)
        {
            _stats.LastError = ex.Message;
        }

        Thread.Sleep(TimeSpan.FromSeconds(2));
    }
}
