namespace ScreenRecall.Dashboard.Services;

/// <summary>Live capture status as reported by the service (mirrors its StatusDto).</summary>
public sealed class CaptureStatus
{
    public string State { get; set; } = "unknown";

    public bool Paused { get; set; }

    public double UptimeSeconds { get; set; }

    public double CpuPercent { get; set; }

    public double WorkingSetMb { get; set; }

    public long FramesAcquired { get; set; }

    public long FramesWithChanges { get; set; }

    public long FramesSkippedExcluded { get; set; }

    public long FullFrameRescans { get; set; }

    public long TilesHashed { get; set; }

    public long TilesDeduped { get; set; }

    public long TilesStored { get; set; }

    public long LogEntries { get; set; }

    public long AssetBytesWritten { get; set; }

    public double AverageFrameMs { get; set; }

    public int ThrottleLevel { get; set; }

    public string Source { get; set; } = string.Empty;

    public int Monitors { get; set; }

    public string? ForegroundApp { get; set; }

    public bool Excluded { get; set; }

    public string? LastError { get; set; }

    public string? LastMaintenance { get; set; }

    public string StorageRoot { get; set; } = string.Empty;

    public string Day { get; set; } = string.Empty;

    public double FreeDiskGb { get; set; }

    public int CanvasTiles { get; set; }

    public long SessionBytes { get; set; }

    public long AssetCountOnDisk { get; set; }

    public long AssetBytesOnDisk { get; set; }

    public long LogZeroRecordFaults { get; set; }

    public bool LogExternalWriterDetected { get; set; }

    public int AssetQueueDepth { get; set; }

    public double AverageProcessMs { get; set; }

    /// <summary>Pause the service is inserting between frames, in milliseconds (its CPU governor at work).</summary>
    public double PaceIntervalMs { get; set; }

    /// <summary>Frames the recorder deliberately waited after, to stay inside its CPU budget.</summary>
    public long FramesPaced { get; set; }

    /// <summary>Why capture is idle, when it is: the service cannot see a desktop to record (spec 13).</summary>
    public string? UnavailableReason { get; set; }

    /// <summary>Readable form of <see cref="UnavailableReason"/>, including the raw DXGI detail.</summary>
    public string? UnavailableDetail { get; set; }

    /// <summary>Seconds until the service tries to capture again.</summary>
    public double RetryInSeconds { get; set; }

    public long DedupeCacheHits { get; set; }

    public long DedupeCacheMisses { get; set; }

    public int DedupeCacheEntries { get; set; }

    /// <summary>True when the recorder is deliberately recording nothing because no display can be duplicated.</summary>
    public bool IsWaitingForOutput => UnavailableReason is not null;

    /// <summary>Share of dedupe lookups answered from memory instead of probing the store on the capture thread.</summary>
    public double DedupeCacheHitPercent
    {
        get
        {
            long lookups = DedupeCacheHits + DedupeCacheMisses;
            return lookups == 0 ? 0 : 100.0 * DedupeCacheHits / lookups;
        }
    }

    public int LastDirtyRects { get; set; }

    public int LastMoveRects { get; set; }

    /// <summary>Dedupe effectiveness, as a percentage of hashed tiles.</summary>
    public double DedupePercent => TilesHashed == 0 ? 0 : 100.0 * TilesDeduped / TilesHashed;

    /// <summary>True when the writer reported anything suspicious (spec: silent corruption guard).</summary>
    public bool HasIntegrityWarning => LogZeroRecordFaults > 0 || LogExternalWriterDetected;
}

/// <summary>One recorded day as the service reports it.</summary>
public sealed class DayInfo
{
    public string Day { get; set; } = string.Empty;

    public long StartTs { get; set; }

    public long EndTs { get; set; }

    public long Bytes { get; set; }

    public long SpanMs { get; set; }

    public int Entries { get; set; }

    public int Checkpoints { get; set; }

    public string Display
        => $"{Day}    {(SpanMs > 0 ? TimeSpan.FromMilliseconds(SpanMs).ToString(@"hh\:mm") : "--:--")}    "
           + $"{Bytes / 1024.0 / 1024.0:0.0} MB    {Entries} entries";
}

/// <summary>Outcome of a prune/purge.</summary>
public sealed class PruneInfo
{
    public int DaysDeleted { get; set; }

    public long SessionBytesDeleted { get; set; }

    public int AssetsDeleted { get; set; }

    public long AssetBytesDeleted { get; set; }

    public long BytesReclaimed { get; set; }

    public string[] Days { get; set; } = Array.Empty<string>();

    public string Describe()
        => $"{DaysDeleted} day(s), {AssetsDeleted} asset(s) removed, "
           + $"{BytesReclaimed / 1024.0 / 1024.0:0.0} MB reclaimed";
}

/// <summary>Monitor as reported by the probe.</summary>
public sealed class MonitorDto
{
    public ushort Id { get; set; }

    public string Device { get; set; } = string.Empty;

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public int Columns { get; set; }

    public int Rows { get; set; }

    public string Display => $"#{Id}   {Width}x{Height} at ({X},{Y})   grid {Columns}x{Rows}";
}

/// <summary>Focus span shown in the jump-to-focus list (navigation metadata only).</summary>
public sealed class FocusItem
{
    public string App { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public long StartTs { get; set; }

    public string Display => $"{TimeOnly.FromDateTime(DateTimeOffset.FromUnixTimeMilliseconds(StartTs).LocalDateTime):HH:mm:ss}  {App} — {Title}";
}
