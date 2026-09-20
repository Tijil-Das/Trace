using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    /// <summary>Rolls the session over when the local day changes (spec 5.5: one log file per day).</summary>
    private void EnsureSessionFor(DateOnly day)
    {
        if (day == _day || _rolling)
        {
            return;
        }

        _rolling = true;
        try
        {
            FlushSessionState();
            _assetWriter.Dispose();
            _log.Dispose();
            _manifest.Dispose();
            _index?.Dispose();

            _day = day;
            (_session, _log, _manifest, _index) = SessionOpener.Open(_config, day, _source.Monitors);
            _assetWriter = new AssetWriteQueue(_session.Assets);
            _session.UpdateMonitors(_source.Monitors, DateTimeOffset.Now);
            _stats.SessionsRolled++;
            _lastCheckpointUs = 0;

            // Carry the canvas into the new day and immediately checkpoint it: from here on the new
            // day's log stands on its own without replaying yesterday.
            WriteCheckpoint(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, force: true);
        }
        finally
        {
            _rolling = false;
        }
    }

    /// <summary>Writes a full-state checkpoint (spec 5.6) plus its index row and session summary.</summary>
    private void WriteCheckpoint(long timestampUs, bool force)
    {
        lock (_sync)
        {
            try
            {
                List<CheckpointMonitorState> states = new();
                foreach (MonitorInfo monitor in _source.Monitors.Count > 0 ? _source.Monitors : _session.Meta.AllMonitors())
                {
                    if (!_canvas.TryGetValue(monitor.Id, out ulong[]? canvas) || canvas.Length != monitor.TileCount)
                    {
                        continue;
                    }

                    states.Add(new CheckpointMonitorState(monitor, (ulong[])canvas.Clone()));
                }

                if (states.Count == 0 && !force)
                {
                    return;
                }

                string path = _session.CheckpointPathFor(timestampUs);
                CheckpointFormat.Write(path, timestampUs, states);
                _lastCheckpointUs = timestampUs;
                _manifest.Flush();
                _log.Flush();
                _index?.InsertCheckpoint(SessionLayout.DayName(_day), timestampUs / 1000, path);

                long first = _log.IsNewFile ? timestampUs / 1000 : 0;
                _index?.UpsertSession(
                    SessionLayout.DayName(_day),
                    first == 0 ? timestampUs / 1000 : first,
                    timestampUs / 1000,
                    _log.FilePath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _stats.LastError = $"checkpoint failed: {ex.Message}";
            }
        }
    }

    /// <summary>
    /// Seeds the canvas from the newest checkpoint available so a restart (or a day rollover) does not
    /// re-log the whole screen: tiles that are unchanged produce no log entry at all.
    /// </summary>
    private void SeedCanvasFromLatestCheckpoint()
    {
        try
        {
            foreach (MonitorInfo monitor in _session.Meta.AllMonitors())
            {
                if (!_canvas.ContainsKey(monitor.Id))
                {
                    _canvas[monitor.Id] = new ulong[Math.Max(monitor.TileCount, 1)];
                }
            }

            (long TimestampUs, string Path)? newest = null;
            IReadOnlyList<(long TimestampUs, string Path)> checkpoints = CheckpointFormat.List(_session.SessionDir);
            if (checkpoints.Count > 0)
            {
                newest = checkpoints[^1];
            }
            else
            {
                DateOnly previous = _session.Day.AddDays(-1);
                IReadOnlyList<(long TimestampUs, string Path)> yesterday =
                    CheckpointFormat.List(SessionLayout.SessionDir(_session.Root, previous));
                if (yesterday.Count > 0)
                {
                    newest = yesterday[^1];
                }
            }

            if (newest is null)
            {
                return;
            }

            CheckpointData data = CheckpointFormat.Read(newest.Value.Path);
            foreach (CheckpointMonitorState state in data.Monitors)
            {
                MonitorInfo monitor = state.Monitor;
                if (monitor.TileCount != state.Tiles.Length)
                {
                    continue;
                }

                _canvas[monitor.Id] = (ulong[])state.Tiles.Clone();
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            _stats.LastError = $"could not seed canvas from checkpoint: {ex.Message}";
        }
    }

    private bool _rolling;
}
