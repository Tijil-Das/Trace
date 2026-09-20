namespace ScreenRecall.Storage;

/// <summary>
/// Facade over one recorded day: the asset store shared by the whole installation, plus this day's
/// log, checkpoints, manifest and monitor meta. Everything the capture service and the player need
/// to agree on the same layout lives here.
/// </summary>
public sealed class SessionStore
{
    public SessionStore(string root, DateOnly day, int tileSize = TileGrid.DefaultTileSize)
    {
        Root = root;
        Day = day;
        SessionDir = SessionLayout.SessionDir(root, day);
        LogPath = SessionLayout.LogPath(root, day);
        CheckpointDir = SessionLayout.CheckpointDir(root, day);
        GroundTruthDir = SessionLayout.GroundTruthDir(root, day);
        MetaPath = SessionLayout.MetaPath(root, day);
        Assets = new AssetStore(SessionLayout.AssetsRoot(root));
        TileSize = tileSize;
        Meta = SessionMeta.Load(MetaPath);
        SessionLayout.EnsureSession(root, day);
    }

    /// <summary>Store root chosen by the user.</summary>
    public string Root { get; }

    /// <summary>Local day this session belongs to.</summary>
    public DateOnly Day { get; }

    /// <summary>Folder holding the day's files.</summary>
    public string SessionDir { get; }

    /// <summary>First log segment of the day.</summary>
    public string LogPath { get; }

    /// <summary>Checkpoint folder of the day.</summary>
    public string CheckpointDir { get; }

    /// <summary>Ground-truth folder of the day (fidelity harness).</summary>
    public string GroundTruthDir { get; }

    /// <summary>Monitor meta file of the day.</summary>
    public string MetaPath { get; }

    /// <summary>Shared content-addressable asset store.</summary>
    public AssetStore Assets { get; }

    /// <summary>Tile edge length this session was recorded with.</summary>
    public int TileSize { get; }

    /// <summary>Monitor geometry recorded for the day.</summary>
    public SessionMeta Meta { get; private set; }

    /// <summary>Opens (or creates) a log segment for appending.</summary>
    public SessionLogWriter OpenLog(DateTimeOffset dayStartLocal, int segment = 0)
        => new(SessionLayout.LogPath(Root, Day, segment), dayStartLocal);

    /// <summary>Index of the first free log segment for the day.</summary>
    public int NextSegmentIndex()
    {
        int index = 0;
        while (File.Exists(SessionLayout.LogPath(Root, Day, index)))
        {
            index++;
        }

        return Math.Max(0, index - 1);
    }

    /// <summary>Path a checkpoint taken at the given timestamp should be written to.</summary>
    public string CheckpointPathFor(long timestampUs) => Path.Combine(CheckpointDir, CheckpointFormat.FileNameFor(timestampUs));

    /// <summary>Opens a manifest part for appending.</summary>
    public AssetManifestWriter OpenManifest(int part = 0)
        => new(AssetManifest.NextPartPath(SessionDir, part), append: true);

    /// <summary>Records currently visible monitor geometry.</summary>
    public void UpdateMonitors(IReadOnlyList<MonitorInfo> monitors, DateTimeOffset now)
    {
        Meta.Update(monitors, now.ToUnixTimeMilliseconds());
        Meta.Save(MetaPath);
    }

    /// <summary>Reloads meta from disk (used by readers).</summary>
    public void ReloadMeta()
    {
        Meta = SessionMeta.Load(MetaPath);
    }

    /// <summary>Opens a store for a day.</summary>
    public static SessionStore Open(string root, DateOnly day, int tileSize = TileGrid.DefaultTileSize)
        => new(root, day, tileSize);

    /// <summary>Log segments of a day in replay order.</summary>
    public IReadOnlyList<string> LogSegments() => SessionLogFormat.FilesFor(SessionDir);

    /// <summary>Every log entry of the day across segments.</summary>
    public IEnumerable<LogEntry> ReadAllEntries()
    {
        foreach (string segment in LogSegments())
        {
            using SessionLogReader reader = new(segment);
            foreach (LogEntry entry in reader.ReadAll())
            {
                yield return entry;
            }
        }
    }
}
