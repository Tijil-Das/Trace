namespace ScreenRecall.Storage;

/// <summary>Outcome of a retention or purge run, surfaced in the dashboard.</summary>
public sealed record PruneReport(
    int DaysDeleted,
    long SessionBytesDeleted,
    int AssetsDeleted,
    long AssetBytesDeleted,
    IReadOnlyList<DateOnly> DeletedDays)
{
    /// <summary>Total bytes reclaimed.</summary>
    public long BytesReclaimed => SessionBytesDeleted + AssetBytesDeleted;
}

/// <summary>
/// Retention and pruning (spec 6 / 8): delete session folders older than the configured window,
/// then garbage-collect assets that no retained day references.
/// </summary>
public sealed partial class RetentionPruner
{
    /// <summary>Grace period protecting assets written very recently from GC.</summary>
    public static readonly TimeSpan DefaultAssetGrace = TimeSpan.FromMinutes(30);

    private readonly string _root;

    public RetentionPruner(string root)
    {
        _root = root;
    }

    /// <summary>Store root being pruned.</summary>
    public string Root => _root;

    /// <summary>Deletes days older than the retention window and runs asset GC.</summary>
    public PruneReport Prune(int retentionDays, DateTimeOffset now, bool collectGarbage = true)
    {
        DateOnly cutoff = DateOnly.FromDateTime(now.LocalDateTime).AddDays(-Math.Max(1, retentionDays));
        List<DateOnly> deleted = new();
        long sessionBytes = 0;

        using RecallIndex? index = TryOpenIndex();
        foreach (DateOnly day in SessionLayout.ListDays(_root))
        {
            if (day >= cutoff)
            {
                continue;
            }

            string dir = SessionLayout.SessionDir(_root, day);
            long bytes = SessionLayout.SessionBytes(_root, day);
            try
            {
                Directory.Delete(dir, recursive: true);
                sessionBytes += bytes;
                deleted.Add(day);
                index?.DeleteDay(SessionLayout.DayName(day));
            }
            catch (IOException)
            {
                // A locked file (dashboard reading it) postpones the delete to the next run.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        int assetsDeleted = 0;
        long assetBytes = 0;
        if (collectGarbage)
        {
            (assetsDeleted, assetBytes) = CollectGarbage(now);
        }

        return new PruneReport(deleted.Count, sessionBytes, assetsDeleted, assetBytes, deleted);
    }

    /// <summary>Days currently inside the retention window, oldest first.</summary>
    public IReadOnlyList<DateOnly> RetainedDays(int retentionDays, DateTimeOffset now)
    {
        DateOnly cutoff = DateOnly.FromDateTime(now.LocalDateTime).AddDays(-Math.Max(1, retentionDays));
        return SessionLayout.ListDays(_root).Where(day => day >= cutoff).ToArray();
    }

    /// <summary>Local day folder size in bytes.</summary>
    public long DayBytes(DateOnly day) => SessionLayout.SessionBytes(_root, day);

    private RecallIndex? TryOpenIndex()
    {
        try
        {
            return new RecallIndex(SessionLayout.IndexPath(_root));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // Without the index the files still delete; the index just keeps stale day rows.
            return null;
        }
    }
}
