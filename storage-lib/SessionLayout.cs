namespace ScreenRecall.Storage;

/// <summary>
/// On-disk layout of the local store (spec 6):
/// <code>
///   &lt;root&gt;/assets/ab/cd/abcd...eff.tile      content-addressable tile cache
///   &lt;root&gt;/sessions/YYYY-MM-DD/log.bin        timestamped reference log
///   &lt;root&gt;/sessions/YYYY-MM-DD/checkpoints/    periodic full-state snapshots
///   &lt;root&gt;/sessions/YYYY-MM-DD/assets.idx      per-day asset manifest (retention GC)
///   &lt;root&gt;/sessions/YYYY-MM-DD/meta.json       monitor geometry
///   &lt;root&gt;/index.db                            SQLite: day/window boundaries only
/// </code>
/// </summary>
public static class SessionLayout
{
    /// <summary>Directory holding a day per folder.</summary>
    public const string SessionsDirName = "sessions";

    /// <summary>Directory holding the asset store.</summary>
    public const string AssetsDirName = "assets";

    /// <summary>Ground-truth frame dumps (fidelity harness only).</summary>
    public const string GroundTruthDirName = "groundtruth";

    /// <summary>Name of the SQLite index file.</summary>
    public const string IndexFileName = "index.db";

    /// <summary>Day folder name for a local timestamp ("yyyy-MM-dd").</summary>
    public static string DayName(DateOnly day) => day.ToString("yyyy-MM-dd");

    /// <summary>Day folder name for a local timestamp.</summary>
    public static string DayName(DateTimeOffset local) => DayName(DateOnly.FromDateTime(local.LocalDateTime));

    /// <summary>Sessions root.</summary>
    public static string SessionsRoot(string root) => Path.Combine(root, SessionsDirName);

    /// <summary>Assets root.</summary>
    public static string AssetsRoot(string root) => Path.Combine(root, AssetsDirName);

    /// <summary>SQLite index path.</summary>
    public static string IndexPath(string root) => Path.Combine(root, IndexFileName);

    /// <summary>Directory of one recorded day.</summary>
    public static string SessionDir(string root, DateOnly day)
        => Path.Combine(SessionsRoot(root), DayName(day));

    /// <summary>Path of a day's reference log segment.</summary>
    public static string LogPath(string root, DateOnly day, int segment = 0)
        => Path.Combine(SessionDir(root, day), segment == 0 ? SessionLogFormat.BaseFileName : $"log.{segment:0000}.bin");

    /// <summary>Checkpoint directory of a day.</summary>
    public static string CheckpointDir(string root, DateOnly day)
        => Path.Combine(SessionDir(root, day), CheckpointFormat.DirectoryName);

    /// <summary>Ground-truth directory of a day.</summary>
    public static string GroundTruthDir(string root, DateOnly day)
        => Path.Combine(SessionDir(root, day), GroundTruthDirName);

    /// <summary>Meta file of a day.</summary>
    public static string MetaPath(string root, DateOnly day)
        => Path.Combine(SessionDir(root, day), SessionMeta.FileName);

    /// <summary>Creates the folder structure for a day.</summary>
    public static void EnsureSession(string root, DateOnly day)
    {
        Directory.CreateDirectory(SessionDir(root, day));
        Directory.CreateDirectory(CheckpointDir(root, day));
        Directory.CreateDirectory(AssetsRoot(root));
    }

    /// <summary>Days present on disk, oldest first.</summary>
    public static IReadOnlyList<DateOnly> ListDays(string root)
    {
        string sessions = SessionsRoot(root);
        if (!Directory.Exists(sessions))
        {
            return Array.Empty<DateOnly>();
        }

        List<DateOnly> days = new();
        foreach (string dir in Directory.EnumerateDirectories(sessions))
        {
            if (DateOnly.TryParseExact(Path.GetFileName(dir), "yyyy-MM-dd", out DateOnly day))
            {
                days.Add(day);
            }
        }

        days.Sort();
        return days;
    }

    /// <summary>
    /// Bytes occupied by one day's session folder.
    /// </summary>
    /// <remarks>
    /// Sizes are read per file rather than taken from the directory listing. That costs one extra system
    /// call per file, but the listing's size field is only refreshed when a file is closed — so a log
    /// that is being appended to right now reports the size it had when it was created. The session
    /// folder is dominated by exactly such a file, and the whole point of this number is to show a
    /// session growing; a stale size is worse than no size at all. The folder holds tens to hundreds of
    /// files, and callers cache the result, so the cost is bounded.
    /// </remarks>
    public static long SessionBytes(string root, DateOnly day)
    {
        string dir = SessionDir(root, day);
        if (!Directory.Exists(dir))
        {
            return 0;
        }

        long total = 0;
        try
        {
            foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try
                {
                    total += new FileInfo(path).Length;
                }
                catch (IOException)
                {
                    // Deleted between listing and measuring (retention GC): skip it, keep the total.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Enumeration itself can fail part-way (a folder removed mid-walk). What was already
            // counted is still a better answer than zero, and the next pass corrects it.
        }

        return total;
    }
}
