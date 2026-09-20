using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    /// <summary>Applies a new configuration without restarting the service.</summary>
    internal void ApplyConfig(RecallConfig config)
    {
        lock (_sync)
        {
            RecallConfig normalized = config.Clone().Normalize();
            bool monitorFilterChanged = normalized.CaptureAllMonitors != _config.CaptureAllMonitors
                                        || !normalized.MonitorIds.SequenceEqual(_config.MonitorIds);
            bool fidelityChanged = normalized.FidelityMode != _config.FidelityMode;
            bool tileSizeChanged = normalized.TileSize != _config.TileSize;
            bool rootChanged = !string.Equals(
                Path.GetFullPath(normalized.StoragePath).TrimEnd('\\'),
                Path.GetFullPath(_config.StoragePath).TrimEnd('\\'),
                StringComparison.OrdinalIgnoreCase);

            _config = normalized;
            _exclusions = new ExclusionMatcher(_config.ExcludedProcesses, _config.ExcludedTitlePatterns);
            _foreground = new ForegroundWindowTracker(_exclusions);
            _codec = TileCodecs.FromFidelityMode(_config.FidelityMode);
            _cadence = new AdaptiveCadence(_config.IdlePollMs, _config.BurstPollMs);

            if (fidelityChanged || tileSizeChanged || rootChanged || monitorFilterChanged)
            {
                _forceFullRescan = true;
            }

            if (rootChanged)
            {
                // Point the recorder at the new root. Data migration is a deliberate, verified copy
                // driven by the dashboard (spec 6); the service never moves files under itself.
                SwitchRoot(_config.StoragePath);
            }

            _stats.Paused = _paused;
        }
    }

    /// <summary>
    /// Switches the active store root, opening a fresh session there. The canvas is reseeded from the
    /// newest checkpoint of the new root so playback keeps working across the switch.
    /// </summary>
    internal void SwitchRoot(string newRoot)
    {
        lock (_sync)
        {
            FlushSessionState();
            _assetWriter.Dispose();
            _log?.Dispose();
            _manifest?.Dispose();
            _index?.Dispose();
            _canvas.Clear();
            _dedupe.Clear();
            _config.StoragePath = newRoot;
            (_session, _log, _manifest, _index) = SessionOpener.Open(_config, _day);
            _assetWriter = new AssetWriteQueue(_session.Assets);
            SeedCanvasFromLatestCheckpoint();
            _forceFullRescan = true;
            WriteCheckpoint(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, force: true);
        }
    }

    /// <summary>Runs retention pruning and asset GC now (dashboard "prune" action, daily job).</summary>
    internal PruneReport PruneNow()
    {
        FlushSessionState();
        PruneReport report = new RetentionPruner(_session.Root).Prune(_config.RetentionDays, DateTimeOffset.Now);
        _dedupe.Clear();
        return report;
    }

    /// <summary>Drops everything recorded in the last <paramref name="window"/>, then reclaims its assets.</summary>
    internal PruneReport PurgeRecent(TimeSpan window)
    {
        bool wasPaused = _paused;
        Pause();
        try
        {
            FlushSessionState();
            PruneReport report = new RetentionPruner(_session.Root).PurgeRecent(window, DateTimeOffset.Now);
            _dedupe.Clear();
            _forceFullRescan = true;
            return report;
        }
        finally
        {
            if (!wasPaused)
            {
                Resume();
            }
        }
    }

    /// <summary>
    /// Flushes the log and manifest so readers see everything written so far. The asset writer is
    /// drained first: a checkpoint or a purge must never run while tile payloads are still queued.
    /// </summary>
    internal void FlushSessionState()
    {
        try
        {
            _assetWriter?.Drain(TimeSpan.FromSeconds(5));
            _log?.Flush();
            _manifest?.Flush();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _stats.LastError = ex.Message;
        }
    }
}
