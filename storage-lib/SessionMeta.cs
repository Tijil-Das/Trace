using System.Text.Json;

namespace ScreenRecall.Storage;

/// <summary>A monitor as recorded in a session's meta file.</summary>
public sealed record SessionMetaMonitor(
    ushort Id,
    string DeviceName,
    int X,
    int Y,
    int Width,
    int Height,
    int TileSize,
    long FirstSeenUnixMs,
    long LastSeenUnixMs)
{
    /// <summary>Geometry view of this entry.</summary>
    public MonitorInfo ToMonitorInfo() => new(Id, DeviceName, X, Y, Width, Height, TileSize);
}

/// <summary>
/// Per-session monitor geometry. Resolution changes and monitor hot-plug are rare but they do happen
/// mid-session, so geometry lives alongside the log instead of being assumed constant: the player
/// needs it to place tiles correctly.
/// </summary>
public sealed class SessionMeta
{
    /// <summary>Current meta version.</summary>
    public const int CurrentVersion = 1;

    /// <summary>Meta file name inside a session directory.</summary>
    public const string FileName = "meta.json";

    public int Version { get; set; } = CurrentVersion;

    public List<SessionMetaMonitor> Monitors { get; set; } = new();

    /// <summary>Loads meta, or returns empty meta when the file does not exist yet.</summary>
    public static SessionMeta Load(string path)
    {
        if (!File.Exists(path))
        {
            return new SessionMeta();
        }

        try
        {
            string json = File.ReadAllText(path);
            SessionMeta? meta = JsonSerializer.Deserialize<SessionMeta>(json, RecallJson.Options);
            if (meta is null)
            {
                return new SessionMeta();
            }

            meta.Monitors ??= new List<SessionMetaMonitor>();
            return meta;
        }
        catch (JsonException)
        {
            return new SessionMeta();
        }
    }

    /// <summary>Writes meta atomically.</summary>
    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        string temp = path + ".part";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, RecallJson.Options));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Merges the currently observed monitors, preserving first-seen times.</summary>
    public void Update(IReadOnlyList<MonitorInfo> monitors, long nowUnixMs)
    {
        foreach (MonitorInfo monitor in monitors)
        {
            int index = Monitors.FindIndex(m => m.Id == monitor.Id && m.Width == monitor.Width
                                                 && m.Height == monitor.Height && m.X == monitor.X && m.Y == monitor.Y);
            if (index >= 0)
            {
                Monitors[index] = Monitors[index] with { LastSeenUnixMs = nowUnixMs };
                continue;
            }

            Monitors.Add(new SessionMetaMonitor(
                monitor.Id,
                monitor.DeviceName,
                monitor.X,
                monitor.Y,
                monitor.Width,
                monitor.Height,
                monitor.TileSize,
                nowUnixMs,
                nowUnixMs));
        }
    }

    /// <summary>Finds the geometry for a monitor id, preferring the most recently seen entry.</summary>
    public MonitorInfo? Find(ushort monitorId)
    {
        SessionMetaMonitor? best = null;
        foreach (SessionMetaMonitor monitor in Monitors)
        {
            if (monitor.Id != monitorId)
            {
                continue;
            }

            if (best is null || monitor.LastSeenUnixMs >= best.LastSeenUnixMs)
            {
                best = monitor;
            }
        }

        return best?.ToMonitorInfo();
    }

    /// <summary>All known monitor geometries, most recent entry per id.</summary>
    public IReadOnlyList<MonitorInfo> AllMonitors()
    {
        Dictionary<ushort, MonitorInfo> map = new();
        foreach (SessionMetaMonitor monitor in Monitors)
        {
            map[monitor.Id] = monitor.ToMonitorInfo();
        }

        return map.Values.ToList();
    }
}
