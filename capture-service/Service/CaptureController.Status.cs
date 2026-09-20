using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.CaptureService.Ipc;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Service;

internal sealed partial class CaptureController
{
    public DayDto[] GetDays()
    {
        string root = _engine?.StorageRoot ?? CurrentConfig.StoragePath;
        List<DayDto> days = new();
        using RecallIndex? index = SessionOpener.TryOpenIndex(root);

        foreach (DateOnly day in SessionLayout.ListDays(root))
        {
            string name = SessionLayout.DayName(day);
            SessionRow? session = index?.GetSession(name);
            long start = session?.StartTs ?? 0;
            long end = session?.EndTs ?? 0;
            long entries = 0;

            foreach (string segment in SessionLogFormat.FilesFor(SessionLayout.SessionDir(root, day)))
            {
                SessionLogStats stats = SessionLogReader.Scan(segment);
                entries += stats.EntryCount;
                if (stats.FirstTimestampUs > 0 && (start == 0 || stats.FirstTimestampUs / 1000 < start))
                {
                    start = stats.FirstTimestampUs / 1000;
                }

                end = Math.Max(end, stats.LastTimestampUs / 1000);
            }

            days.Add(new DayDto(
                name,
                start,
                end,
                SessionLayout.SessionBytes(root, day),
                index?.TotalSpanMs(name) ?? 0,
                (int)entries,
                CheckpointFormat.List(SessionLayout.SessionDir(root, day)).Count));
        }

        return days.ToArray();
    }

    public WindowDto[] GetWindows(string day)
    {
        string root = _engine?.StorageRoot ?? CurrentConfig.StoragePath;
        using RecallIndex? index = SessionOpener.TryOpenIndex(root);
        if (index is null)
        {
            return Array.Empty<WindowDto>();
        }

        return index.GetWindowSpans(day)
            .GroupBy(span => (span.AppName, span.WindowTitle))
            .Select(group => new WindowDto(
                group.Key.AppName,
                group.Key.WindowTitle,
                group.Min(span => span.StartTs),
                group.Max(span => span.EndTs),
                group.Count()))
            .OrderBy(window => window.StartTs)
            .ToArray();
    }

    public ProbeDto Probe(bool deep)
    {
        List<MonitorDto> monitors = new();
        List<string> notes = new();
        bool ok = false;
        string source = _engine?.Source.Name ?? "none";

        try
        {
            if (_engine is not null)
            {
                foreach (MonitorInfo monitor in _engine.Source.Monitors)
                {
                    monitors.Add(ToDto(monitor));
                }

                ok = monitors.Count > 0;
            }
            else
            {
                foreach (MonitorInfo monitor in DxgiOutputEnumerator.EnumerateMonitors())
                {
                    monitors.Add(ToDto(monitor));
                }

                using DxgiFrameSource candidate = DxgiFrameSource.Create(CurrentConfig.TileSize, null);
                ok = candidate.Monitors.Count > 0;
                notes.AddRange(candidate.Notes);
                source = candidate.Name;
            }
        }
        catch (Exception ex)
        {
            notes.Add(ex.Message);
        }

        return new ProbeDto(source, monitors.ToArray(), ok, notes.Count > 0 ? notes[0] : null, notes.ToArray());
    }

    public StatusDto GetStatus()
    {
        CaptureEngine? engine = _engine;
        RecallConfig config = CurrentConfig;

        if (engine is null)
        {
            // Before the engine exists there is nothing to report: zeros for every counter, and the
            // configured storage path/day so the dashboard can still render its shell.
            return new StatusDto(
                State: "starting",
                Paused: config.Paused,
                StartedUtc: DateTimeOffset.UtcNow.ToString("o"),
                UptimeSeconds: 0,
                CpuPercent: 0,
                WorkingSetMb: 0,
                FramesAcquired: 0,
                FramesWithChanges: 0,
                FramesSkippedExcluded: 0,
                FramesSkippedProtected: 0,
                FullFrameRescans: 0,
                TilesHashed: 0,
                TilesDeduped: 0,
                TilesStored: 0,
                LogEntries: 0,
                AssetBytesWritten: 0,
                AverageFrameMs: 0,
                ThrottleLevel: 0,
                Source: "none",
                Monitors: 0,
                ForegroundApp: null,
                Excluded: false,
                LastError: null,
                LastMaintenance: null,
                StorageRoot: config.StoragePath,
                Day: SessionLayout.DayName(DateOnly.FromDateTime(DateTime.Now)),
                FreeDiskGb: 0,
                CanvasTiles: 0,
                SessionBytes: 0,
                AssetCountOnDisk: 0,
                AssetBytesOnDisk: 0,
                FramesSkippedNoChange: 0,
                FramesRescanDeferred: 0,
                LastDirtyRects: 0,
                LastMoveRects: 0,
                AverageProcessMs: 0,
                AverageAcquireMs: 0,
                LastReadbackMs: 0,
                LastHashMs: 0,
                LastEncodeMs: 0,
                LastStoreMs: 0,
                LastLogMs: 0);
        }

        CaptureStats stats = engine.Stats;
        (long assetCount, long assetBytes) = engine.AssetStats();

        return new StatusDto(
            State: stats.Paused ? "paused" : "recording",
            Paused: stats.Paused,
            StartedUtc: stats.StartedUtc.ToString("o"),
            UptimeSeconds: stats.UptimeSeconds,
            CpuPercent: stats.SampleCpuPercent(),
            WorkingSetMb: stats.WorkingSetMb,
            FramesAcquired: stats.FramesAcquired,
            FramesWithChanges: stats.FramesWithChanges,
            FramesSkippedExcluded: stats.FramesSkippedExcluded,
            FramesSkippedProtected: stats.FramesSkippedProtected,
            FullFrameRescans: stats.FullFrameRescans,
            TilesHashed: stats.TilesHashed,
            TilesDeduped: stats.TilesDeduped,
            TilesStored: stats.TilesStored,
            LogEntries: stats.LogEntriesWritten,
            AssetBytesWritten: stats.AssetsBytesWritten,
            AverageFrameMs: stats.AverageFrameMs,
            ThrottleLevel: stats.ThrottleLevel,
            Source: engine.Source.Name,
            Monitors: stats.MonitorsActive,
            ForegroundApp: stats.ForegroundApp,
            Excluded: stats.Excluded,
            LastError: stats.LastError,
            LastMaintenance: stats.LastMaintenance,
            StorageRoot: engine.StorageRoot,
            Day: SessionLayout.DayName(engine.CurrentDay),
            FreeDiskGb: engine.FreeDiskGb,
            CanvasTiles: engine.CanvasTileCount,
            SessionBytes: SessionLayout.SessionBytes(engine.StorageRoot, engine.CurrentDay),
            AssetCountOnDisk: assetCount,
            AssetBytesOnDisk: assetBytes,
            FramesSkippedNoChange: stats.FramesSkippedNoChange,
            FramesRescanDeferred: stats.FramesRescanDeferred,
            LastDirtyRects: stats.LastDirtyRects,
            LastMoveRects: stats.LastMoveRects,
            AverageProcessMs: Math.Round(stats.AverageProcessMs, 2),
            AverageAcquireMs: Math.Round(stats.AverageAcquireMs, 2),
            LastReadbackMs: stats.LastReadbackMs,
            LastHashMs: stats.LastHashMs,
            LastEncodeMs: stats.LastEncodeMs,
            LastStoreMs: stats.LastStoreMs,
            LastLogMs: stats.LastLogMs);

    }

    internal static MonitorDto ToDto(MonitorInfo monitor)
        => new(
            monitor.Id,
            monitor.DeviceName,
            monitor.X,
            monitor.Y,
            monitor.Width,
            monitor.Height,
            monitor.TileSize,
            monitor.Columns,
            monitor.Rows);
}
