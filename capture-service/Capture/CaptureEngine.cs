using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// The steady-state capture loop (spec 5): acquire a frame, turn the dirty/move rects into grid-aligned
/// tiles, hash each tile, dedupe it against the content-addressable store, append a reference-log entry
/// and update the in-memory canvas that later becomes a checkpoint. This class is hot-path code: no
/// per-frame allocations beyond small lists, no full-frame copies, no video codec anywhere.
/// </summary>
internal sealed partial class CaptureEngine : IDisposable
{
    /// <summary>
    /// Minimum spacing between full-surface rescans. Dirty-rect frames are never delayed by this; it
    /// only bounds how often the "present with no rects" case (cursor, layered overlays) can force a
    /// whole-screen hash pass.
    /// </summary>
    private const int FullRescanMinIntervalMs = 1000;

    private readonly object _sync = new();
    private readonly CaptureStats _stats = new();
    private readonly DedupeCache _dedupe = new();
    private readonly TileCellSet _cells = new();
    private readonly Dictionary<ushort, ulong[]> _canvas = new();
    private readonly byte[] _tileScratch;
    private readonly IFrameSource _source;
    private readonly StoreLock? _storeLock;

    private ExclusionMatcher _exclusions;
    private ForegroundWindowTracker _foreground;
    private AdaptiveCadence _cadence;
    private ITileCodec _codec;
    private SessionStore _session;
    private SessionLogWriter _log;
    private AssetManifestWriter _manifest;
    private RecallIndex? _index;
    private AssetWriteQueue _assetWriter;

    private RecallConfig _config;
    private DateOnly _day;
    private long _lastCheckpointUs;
    private long _lastFlushUs;
    private long _lastSpanFlushMs;
    private long _lastPruneMs;
    private long _lastDiskCheckMs;
    private long _lastFullRescanMs;
    private long _lastGroundTruthMs;
    private bool _owedRescan;
    private long _lastAssetStatsMs;
    private int _assetStatsRefreshing;
    private long _lastSessionBytesMs;
    private long _cachedSessionBytes;
    private int _sessionBytesRefreshing;
    private long _cachedAssetCount;
    private long _cachedAssetBytes;
    private bool _forceFullRescan = true;
    private bool _pruneRunning;
    private double _freeDiskGb;
    private ForegroundWindowInfo _spanWindow = ForegroundWindowInfo.Empty;
    private bool _disposed;

    private volatile bool _paused;

    internal CaptureEngine(RecallConfig config, IFrameSource source)
    {
        _config = config.Clone().Normalize();
        _source = source;
        _exclusions = new ExclusionMatcher(_config.ExcludedProcesses, _config.ExcludedTitlePatterns);
        _foreground = new ForegroundWindowTracker(_exclusions);
        _codec = TileCodecs.FromFidelityMode(_config.FidelityMode);
        _cadence = new AdaptiveCadence(_config.IdlePollMs, _config.BurstPollMs);
        _tileScratch = new byte[Math.Max(_config.TileSize * _config.TileSize * 4, 64 * 64 * 4)];
        _day = DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime);

        if (!StoreLock.TryAcquire(config.StoragePath, out StoreLock? storeLock, out string? holder))
        {
            // Two writers in one store would interleave log appends: refuse to record rather than
            // corrupt what is already there.
            _stats.LastError = $"another Screen Recall instance is recording into '{config.StoragePath}' ({holder})";
            storeLock?.Dispose();
            _paused = true;
            _stats.Paused = true;
        }
        else
        {
            _storeLock = storeLock;
        }

        (_session, _log, _manifest, _index) = SessionOpener.Open(_config, _day, _source.Monitors);
        _assetWriter = new AssetWriteQueue(_session.Assets);
        PrepareSessionStorage();
        SeedCanvasFromLatestCheckpoint();
    }

    /// <summary>Live counters for the dashboard and the CLI.</summary>
    internal CaptureStats Stats => _stats;

    /// <summary>Active capture backend.</summary>
    internal IFrameSource Source => _source;

    /// <summary>Root of the store currently being written.</summary>
    internal string StorageRoot => _session.Root;

    /// <summary>Session currently being written (log, checkpoints, assets).</summary>
    internal SessionStore Session => _session;

    /// <summary>Day currently being recorded.</summary>
    internal DateOnly CurrentDay => _day;

    /// <summary>Number of non-empty tile slots in the in-memory canvas.</summary>
    internal int CanvasTileCount
    {
        get
        {
            int total = 0;
            foreach (ulong[] tiles in _canvas.Values)
            {
                foreach (ulong hash in tiles)
                {
                    if (hash != TileHash.None)
                    {
                        total++;
                    }
                }
            }

            return total;
        }
    }

    /// <summary>Free disk space on the storage volume, in GB (0 when unknown).</summary>
    internal double FreeDiskGb => _freeDiskGb;

    /// <summary>
    /// Asset count and byte footprint of the store, refreshed at most every 30 seconds. Walking a
    /// store with hundreds of thousands of files is far too expensive to do per status poll, and far
    /// too expensive to do on the thread answering the poll: after the first call the walk is handed to
    /// the thread pool and this returns the last known figures immediately.
    /// </summary>
    internal (long Count, long Bytes) AssetStats(int refreshIntervalMs = 30_000)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_lastAssetStatsMs == 0)
        {
            // First call has nothing cached to return, and at startup the store is small: answer
            // synchronously so the CLI and the first dashboard poll get real numbers.
            RefreshAssetStats(nowMs);
        }
        else if (nowMs - _lastAssetStatsMs >= refreshIntervalMs)
        {
            QueueAssetStatsRefresh(nowMs);
        }

        return (_cachedAssetCount, _cachedAssetBytes);
    }

    /// <summary>Recomputes the store footprint into the cached fields.</summary>
    private void RefreshAssetStats(long nowMs)
    {
        try
        {
            (_cachedAssetCount, _cachedAssetBytes) = _session.Assets.ComputeStats();
            _lastAssetStatsMs = nowMs;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _stats.LastError = $"asset stats failed: {ex.Message}";
            _lastAssetStatsMs = nowMs; // Back off; a broken walk must not be retried on every poll.
        }
    }

    /// <summary>
    /// Starts a background recount if one is not already running. Single-flight: the dashboard polls
    /// every couple of seconds, and without the guard a slow walk would be started again on every tick.
    /// </summary>
    private void QueueAssetStatsRefresh(long nowMs)
    {
        if (Interlocked.CompareExchange(ref _assetStatsRefreshing, 1, 0) != 0)
        {
            return;
        }

        _lastAssetStatsMs = nowMs;
        Task.Run(() =>
        {
            try
            {
                (_cachedAssetCount, _cachedAssetBytes) = _session.Assets.ComputeStats();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _stats.LastError = $"asset stats failed: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _assetStatsRefreshing, 0);
            }
        });
    }

    /// <summary>
    /// Bytes occupied by today's session folder. The folder holds the log, the checkpoints and any
    /// ground-truth dumps, so walking it on every status poll — twice a second, from the tray and the
    /// window — would cost thousands of file-system calls a second to track a number that moves by a few
    /// kilobytes. Cached for 30 seconds, refreshed off-thread after the first call.
    /// </summary>
    internal long SessionBytes(int refreshIntervalMs = 30_000)
    {
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (_lastSessionBytesMs == 0)
        {
            _cachedSessionBytes = SessionLayout.SessionBytes(_session.Root, _day);
            _lastSessionBytesMs = nowMs;
        }
        else if (nowMs - _lastSessionBytesMs >= refreshIntervalMs)
        {
            QueueSessionBytesRefresh(nowMs);
        }

        return _cachedSessionBytes;
    }

    /// <summary>Starts a background size recount if one is not already running.</summary>
    private void QueueSessionBytesRefresh(long nowMs)
    {
        if (Interlocked.CompareExchange(ref _sessionBytesRefreshing, 1, 0) != 0)
        {
            return;
        }

        _lastSessionBytesMs = nowMs;
        Task.Run(() =>
        {
            try
            {
                _cachedSessionBytes = SessionLayout.SessionBytes(_session.Root, _day);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _stats.LastError = $"session size failed: {ex.Message}";
            }
            finally
            {
                Interlocked.Exchange(ref _sessionBytesRefreshing, 0);
            }
        });
    }

    /// <summary>True when capture is paused by the user.</summary>
    internal bool Paused => _paused;

    /// <summary>Pauses capture; the loop keeps draining frames so timestamps stay meaningful.</summary>
    internal void Pause()
    {
        _paused = true;
        _stats.Paused = true;
        FlushSessionState();
    }

    /// <summary>Resumes capture, forcing a full rescan so anything that changed while paused is caught.</summary>
    internal void Resume()
    {
        _paused = false;
        _stats.Paused = false;
        _forceFullRescan = true;
        _cadence.Reset();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _assetWriter.TryDrain(TimeSpan.FromSeconds(20));
                _assetWriter.Dispose();
                _log?.Dispose();
                _manifest?.Dispose();
                _index?.Dispose();
                _storeLock?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _stats.LastError = ex.Message;
            }

            _source.Dispose();
        }
    }
}
