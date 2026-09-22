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

    /// <summary>Maximum distinct monitor geometries kept per session (older ones are dropped).</summary>
    public const int MaxMonitorEntries = 64;

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

        // A monitor that flickers between modes would otherwise grow this file forever.
        if (Monitors.Count > MaxMonitorEntries)
        {
            Monitors = Monitors
                .OrderByDescending(monitor => monitor.LastSeenUnixMs)
                .Take(MaxMonitorEntries)
                .OrderBy(monitor => monitor.FirstSeenUnixMs)
                .ToList();
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

    /// <summary>Every monitor whose geometry is in force at a timestamp, one entry per id.</summary>
    public IReadOnlyList<MonitorInfo> MonitorsAt(long timestampMs)
    {
        List<MonitorInfo> monitors = new();
        HashSet<ushort> seen = new();
        foreach (SessionMetaMonitor entry in Monitors)
        {
            if (seen.Add(entry.Id) && GeometryAt(entry.Id, timestampMs) is { } active)
            {
                monitors.Add(active);
            }
        }

        return monitors.OrderBy(monitor => monitor.Id).ToList();
    }

    /// <summary>
    /// The geometry in force for one monitor at a timestamp, or null when it was not known yet: the meta keeps
    /// (id, geometry) rows rather than one row per id — a mid-session plug, a mode change or a foreign source can
    /// and did reuse id 0 with another grid — and two grids disagree about where tile (x, y) lands, so applying an
    /// entry under the wrong one renders at the wrong place. A timestamp inside a row's first/last-seen window
    /// selects that row; after the newest row's window the newest row still owns the id, because capture keeps
    /// recording under it until a newer geometry is seen.
    /// </summary>
    public MonitorInfo? GeometryAt(ushort monitorId, long timestampMs)
        => MonitorAt(monitorId, timestampMs)?.ToMonitorInfo();

    /// <summary>The meta row in force for a monitor id at a timestamp, or null before it was first seen.</summary>
    public SessionMetaMonitor? MonitorAt(ushort monitorId, long timestampMs)
    {
        SessionMetaMonitor? containing = null;
        SessionMetaMonitor? latest = null;

        foreach (SessionMetaMonitor entry in Monitors)
        {
            // Distinct ids report independently: a stale window for a different id never suppresses one.
            if (entry.Id != monitorId || timestampMs < entry.FirstSeenUnixMs)
            {
                continue;
            }

            // Two rows can both claim a moment: the real display is re-seen after a foreign source used the same
            // id, which widens its last-seen across the foreign window. The geometry actually in force is the most
            // recently *changed* one, i.e. the containing window with the latest first-seen.
            if (timestampMs <= entry.LastSeenUnixMs
                && (containing is null || entry.FirstSeenUnixMs > containing.FirstSeenUnixMs))
            {
                containing = entry;
            }

            // Outside every window (a gap, or after the last sighting) the geometry still in force is the one seen
            // most recently, because capture keeps using it until it is told otherwise.
            if (latest is null || entry.LastSeenUnixMs >= latest.LastSeenUnixMs)
            {
                latest = entry;
            }
        }

        return containing ?? latest;
    }

    /// <summary>
    /// All known monitor geometries, newest geometry per id.
    ///
    /// "Newest" is by last-seen, not by position in the file, and that distinction is the whole bug this method
    /// caused: a session's meta accumulates one row per (id, geometry), and a fallback or test source recording
    /// under the same id with a different grid won purely by being appended later. That seeded the canvas with a
    /// 16x12 grid for content captured on a 22x12 one, so 689 MB of a real desktop replayed as stripes.
    ///
    /// Use this for "which geometries exist" questions (counts, day summaries, seeding a canvas before replay
    /// starts). Anything that applies a log entry at a timestamp must use <see cref="MonitorsAt"/> or
    /// <see cref="GeometryAt"/>, because two rows for one id mean two tile grids.
    /// </summary>
    public IReadOnlyList<MonitorInfo> AllMonitors()
    {
        Dictionary<ushort, SessionMetaMonitor> map = new();
        foreach (SessionMetaMonitor monitor in Monitors)
        {
            if (!map.TryGetValue(monitor.Id, out SessionMetaMonitor? current)
                || monitor.LastSeenUnixMs >= current.LastSeenUnixMs)
            {
                map[monitor.Id] = monitor;
            }
        }

        return map.Values
            .Select(monitor => monitor.ToMonitorInfo())
            .OrderBy(monitor => monitor.Id)
            .ToList();
    }
}
