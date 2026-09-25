using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    /// <summary>
    /// Seconds between heartbeats. A heartbeat is 27 bytes, so this is about telling "the recorder was watching and
    /// nothing changed" apart from "nobody was recording" for nothing: 15 s is short enough that an hour of missing
    /// recording is unmistakable, and long enough that a day of quiet time costs about 6 KB.
    /// </summary>
    private const int HeartbeatSeconds = 15;

    private long _lastHeartbeatMs;

    /// <summary>
    /// Writes "the recorder was here and had nothing to write" into the log, at most every 15 seconds.
    /// </summary>
    /// <remarks>
    /// The log only grows when something changes, which makes a quiet hour and a switched-off recorder look identical
    /// to a reader - and the difference matters: one is a still frame, the other is not a recording at all. A
    /// heartbeat is therefore only written while the loop is healthy: never while paused (that is a deliberate gap),
    /// never while the source is blocked (a locked session, a disconnected desktop, a display that cannot be
    /// duplicated - also not a recording), and never after shutdown has started. Its absence is the signal.
    /// </remarks>
    internal void WriteHeartbeat()
    {
        if (_log is null || _paused || _source.Block is not null)
        {
            return;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (nowMs - _lastHeartbeatMs < HeartbeatSeconds * 1000L)
        {
            return;
        }

        _lastHeartbeatMs = nowMs;
        _log.Append(LogEntry.Heartbeat(nowMs * 1000));
        _stats.LogEntriesWritten++;
    }
}
