using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>Outcome of checking that every referenced tile is present and intact.</summary>
public sealed record IntegrityReport(
    DateOnly Day,
    long ReferencedHashes,
    long PresentAssets,
    long MissingAssets,
    long CorruptAssets,
    long LogEntries,
    int Checkpoints,
    IReadOnlyList<string> Examples)
{
    /// <summary>True when nothing referenced is missing or damaged.</summary>
    public bool IsHealthy => MissingAssets == 0 && CorruptAssets == 0;

    /// <summary>One-line summary for the CLI and the dashboard.</summary>
    public string Describe()
        => $"{LogEntries} log entries, {ReferencedHashes} referenced tiles, {PresentAssets} present, "
           + $"{MissingAssets} missing, {CorruptAssets} corrupt, {Checkpoints} checkpoint(s)";
}

/// <summary>
/// Integrity check: every hash referenced by a day's log or checkpoints must exist in the asset store,
/// and (optionally) must still decode to the content it claims to hold. This is the check to run after
/// a crash, a pruning pass, or a storage migration.
/// </summary>
public static class IntegrityVerifier
{
    /// <summary>Verifies one recorded day.</summary>
    public static IntegrityReport Verify(
        string root,
        DateOnly day,
        bool verifyContent = false,
        int maxExamples = 10,
        IProgress<string>? progress = null)
    {
        SessionStore store = SessionStore.Open(root, day);
        HashSet<ulong> referenced = new();
        long entries = 0;

        foreach (string segment in store.LogSegments())
        {
            using SessionLogReader reader = new(segment);
            while (reader.TryReadNext(out LogEntry entry))
            {
                entries++;
                if (entry.AssetHash != TileHash.None)
                {
                    referenced.Add(entry.AssetHash);
                }
            }
        }

        IReadOnlyList<(long TimestampUs, string Path)> checkpoints = CheckpointFormat.List(store.SessionDir);
        foreach ((_, string path) in checkpoints)
        {
            try
            {
                CheckpointData checkpoint = CheckpointFormat.Read(path);
                foreach (CheckpointMonitorState state in checkpoint.Monitors)
                {
                    foreach (ulong hash in state.Tiles)
                    {
                        if (hash != TileHash.None)
                        {
                            referenced.Add(hash);
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                progress?.Report($"checkpoint {Path.GetFileName(path)} unreadable: {ex.Message}");
            }
        }

        long present = 0;
        long missing = 0;
        long corrupt = 0;
        List<string> examples = new();

        foreach (ulong hash in referenced)
        {
            if (!store.Assets.Contains(hash, checkDisk: true))
            {
                missing++;
                AddExample(examples, maxExamples, $"missing asset {TileHash.ToHex(hash)}");
                continue;
            }

            if (!verifyContent)
            {
                present++;
                continue;
            }

            try
            {
                store.Assets.TryLoadTile(hash, verifyHash: true);
                present++;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                corrupt++;
                AddExample(examples, maxExamples, $"corrupt asset {TileHash.ToHex(hash)}: {ex.Message}");
            }
        }

        progress?.Report($"checked {referenced.Count} referenced tiles ({present} ok)");
        return new IntegrityReport(
            day,
            referenced.Count,
            present,
            missing,
            corrupt,
            entries,
            checkpoints.Count,
            examples);
    }

    private static void AddExample(List<string> examples, int maxExamples, string message)
    {
        if (examples.Count < maxExamples)
        {
            examples.Add(message);
        }
    }
}
