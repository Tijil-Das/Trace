using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Ipc;

/// <summary>Name of the pipe the service listens on (spec 9).</summary>
internal static class IpcConstants
{
    internal const string PipeName = "ScreenRecall.Capture";

    internal static class Commands
    {
        internal const string Ping = "ping";
        internal const string Status = "status";
        internal const string Pause = "pause";
        internal const string Resume = "resume";
        internal const string TogglePause = "togglePause";
        internal const string GetConfig = "config.get";
        internal const string SetConfig = "config.set";
        internal const string PurgeRecent = "purgeRecent";
        internal const string Prune = "prune";
        internal const string Days = "days";
        internal const string Windows = "windows";
        internal const string Probe = "probe";
        internal const string Flush = "flush";
        internal const string Shutdown = "shutdown";
    }
}

/// <summary>One request from the dashboard (or the CLI) over the named pipe.</summary>
internal sealed class IpcRequest
{
    public int Id { get; set; }

    public string Command { get; set; } = string.Empty;

    /// <summary>New configuration for <c>config.set</c>.</summary>
    public RecallConfig? Config { get; set; }

    /// <summary>Minutes for <c>purgeRecent</c> (default 15).</summary>
    public int? Minutes { get; set; }

    /// <summary>Day ("yyyy-MM-dd") for <c>windows</c>.</summary>
    public string? Day { get; set; }

    /// <summary>Verbosity flag for <c>probe</c>.</summary>
    public bool? Deep { get; set; }
}

/// <summary>One response; exactly one is written per request.</summary>
internal sealed class IpcResponse
{
    public int Id { get; set; }

    public bool Ok { get; set; }

    public string? Error { get; set; }

    public StatusDto? Status { get; set; }

    public RecallConfig? Config { get; set; }

    public PruneReportDto? Report { get; set; }

    public ProbeDto? Probe { get; set; }

    public DayDto[]? Days { get; set; }

    public WindowDto[]? Windows { get; set; }

    internal static IpcResponse Failure(int id, string error) => new() { Id = id, Ok = false, Error = error };
}

internal sealed record StatusDto(
    string State,
    bool Paused,
    string StartedUtc,
    double UptimeSeconds,
    double CpuPercent,
    double WorkingSetMb,
    long FramesAcquired,
    long FramesWithChanges,
    long FramesSkippedExcluded,
    long FramesSkippedProtected,
    long FullFrameRescans,
    long TilesHashed,
    long TilesDeduped,
    long TilesStored,
    long LogEntries,
    long AssetBytesWritten,
    double AverageFrameMs,
    int ThrottleLevel,
    string Source,
    int Monitors,
    string? ForegroundApp,
    bool Excluded,
    string? LastError,
    string? LastMaintenance,
    string StorageRoot,
    string Day,
    double FreeDiskGb,
    int CanvasTiles,
    long SessionBytes,
    long AssetCountOnDisk,
    long AssetBytesOnDisk,
    long FramesSkippedNoChange,
    long FramesRescanDeferred,
    int LastDirtyRects,
    int LastMoveRects,
    double AverageProcessMs,
    double AverageAcquireMs,
    double LastReadbackMs,
    double LastHashMs,
    double LastEncodeMs,
    double LastStoreMs,
    double LastLogMs,
    long LogZeroRecordFaults,
    bool LogExternalWriterDetected,
    int AssetQueueDepth);

internal sealed record PruneReportDto(
    int DaysDeleted,
    long SessionBytesDeleted,
    int AssetsDeleted,
    long AssetBytesDeleted,
    long BytesReclaimed,
    string[] Days);

internal sealed record MonitorDto(
    ushort Id,
    string Device,
    int X,
    int Y,
    int Width,
    int Height,
    int TileSize,
    int Columns,
    int Rows);

internal sealed record ProbeDto(
    string Source,
    MonitorDto[] Monitors,
    bool DuplicationOk,
    string? Note,
    string[] Notes);

internal sealed record DayDto(
    string Day,
    long StartTs,
    long EndTs,
    long Bytes,
    long SpanMs,
    int Entries,
    int Checkpoints);

internal sealed record WindowDto(
    string App,
    string Title,
    long StartTs,
    long EndTs,
    int Count);

/// <summary>Everything the IPC layer is allowed to ask the capture side to do.</summary>
internal interface ICaptureControl
{
    RecallConfig GetConfig();

    void SetConfig(RecallConfig config);

    StatusDto GetStatus();

    void Pause();

    void Resume();

    void TogglePause();

    PruneReportDto PurgeRecent(int minutes);

    PruneReportDto Prune();

    void Flush();

    ProbeDto Probe(bool deep);

    DayDto[] GetDays();

    WindowDto[] GetWindows(string day);

    void RequestShutdown();
}
