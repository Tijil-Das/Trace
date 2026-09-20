using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    private ForegroundWindowInfo _spanStartWindow = ForegroundWindowInfo.Empty;
    private long _spanStartMs;

    /// <summary>
    /// Maintains the focus-span boundaries in the navigation index (spec 6). These exist purely so the
    /// player can jump to "the next time Chrome was focused" — this is navigation metadata, not
    /// usage analytics, and spans are coalesced so long focus periods stay one row.
    /// </summary>
    private void UpdateWindowSpans(ForegroundWindowInfo foreground, bool recordSpans)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        if (!recordSpans)
        {
            CloseSpan(nowMs);
            return;
        }

        if (_spanStartWindow.Id == foreground.Id && _spanStartWindow.Id != 0)
        {
            if (nowMs - _lastSpanFlushMs >= 30_000)
            {
                _lastSpanFlushMs = nowMs;
                AddSpan(foreground, _spanStartMs, nowMs);
            }

            return;
        }

        CloseSpan(nowMs);
        _spanStartWindow = foreground;
        _spanStartMs = nowMs;
        _lastSpanFlushMs = nowMs;
        if (foreground.Id != 0)
        {
            AddSpan(foreground, _spanStartMs, nowMs);
        }
    }

    private void CloseSpan(long nowMs)
    {
        if (_spanStartWindow.Id == 0)
        {
            return;
        }

        AddSpan(_spanStartWindow, _spanStartMs, Math.Max(nowMs, _spanStartMs));
        _spanStartWindow = ForegroundWindowInfo.Empty;
    }

    private void AddSpan(ForegroundWindowInfo window, long startMs, long endMs)
    {
        try
        {
            _index?.AddWindowSpan(SessionLayout.DayName(_day), window.Process, window.Title, startMs, endMs);
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            _stats.LastError = $"index update failed: {ex.Message}";
        }
    }
}
