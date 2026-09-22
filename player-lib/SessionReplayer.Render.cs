using ScreenRecall.Storage;

namespace ScreenRecall.Player;

public sealed partial class SessionReplayer
{
    /// <summary>Renders one monitor at the current position.</summary>
    public RenderedFrame Render(ushort monitorId)
    {
        MonitorInfo? monitor = Canvas.Monitor(monitorId);
        return monitor is null
            ? new RenderedFrame(monitorId, 0, 0, 1, 1, new byte[4])
            : Renderer.Render(Canvas, monitor);
    }

    /// <summary>Renders every monitor at the current position.</summary>
    public IReadOnlyList<RenderedFrame> RenderAll()
        => Canvas.Monitors.Select(monitor => Renderer.Render(Canvas, monitor)).ToList();

    /// <summary>Renders all monitors composited into one virtual-desktop frame.</summary>
    public RenderedFrame RenderVirtualDesktop() => Renderer.RenderVirtualDesktop(Canvas);

    /// <summary>Focus spans of the day (navigation metadata only, spec 6).</summary>
    public IReadOnlyList<WindowSpanRow> WindowSpans()
    {
        try
        {
            using RecallIndex index = new(SessionLayout.IndexPath(Store.Root));
            return index.GetWindowSpans(SessionLayout.DayName(Store.Day));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Array.Empty<WindowSpanRow>();
        }
    }

    /// <summary>Timestamp of the next moment a window gains focus at or after a position.</summary>
    public long? NextFocusOf(string appName, string windowTitle, long afterMs)
    {
        foreach (WindowSpanRow span in WindowSpans().OrderBy(span => span.StartTs))
        {
            if (span.StartTs > afterMs
                && string.Equals(span.AppName, appName, StringComparison.OrdinalIgnoreCase)
                && string.Equals(span.WindowTitle, windowTitle, StringComparison.Ordinal))
            {
                return span.StartTs;
            }
        }

        return null;
    }

    private void ApplyGeometry(long timestampMs = 0)
    {
        IReadOnlyList<MonitorInfo> monitors = timestampMs > 0
            ? Store.Meta.MonitorsAt(timestampMs)
            : Array.Empty<MonitorInfo>();

        foreach (MonitorInfo monitor in monitors.Count > 0 ? monitors : Store.Meta.AllMonitors())
        {
            Canvas.SetMonitor(monitor);
        }

        if (Canvas.Monitors.Count > 0)
        {
            return;
        }

        // Sessions recorded before the meta was populated (or a session whose meta was lost) still
        // carry full monitor geometry inside every checkpoint, so fall back to the newest one.
        IReadOnlyList<(long TimestampUs, string Path)> checkpoints = CheckpointFormat.List(Store.SessionDir);
        if (checkpoints.Count == 0)
        {
            return;
        }

        try
        {
            CheckpointData newest = CheckpointFormat.Read(checkpoints[^1].Path);
            foreach (CheckpointMonitorState state in newest.Monitors)
            {
                Canvas.SetMonitor(state.Monitor);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            // Geometry stays empty: rendering will report a 1x1 frame rather than throwing.
        }
    }

    private long? FindCheckpointAtOrBefore(long timestampUs)
    {
        long? best = null;
        foreach (long candidate in _checkpoints)
        {
            if (candidate <= timestampUs)
            {
                best = candidate;
            }
            else
            {
                break;
            }
        }

        return best;
    }

    private CheckpointData LoadCheckpoint(long timestampUs)
    {
        foreach (CheckpointData cached in _checkpointCache)
        {
            if (cached.TimestampUs == timestampUs)
            {
                return cached;
            }
        }

        CheckpointData data = CheckpointFormat.Read(Store.CheckpointPathFor(timestampUs));
        if (_checkpointCache.Count >= 4)
        {
            _checkpointCache.RemoveAt(0);
        }

        _checkpointCache.Add(data);
        return data;
    }
}
