namespace ScreenRecall.Storage;

public sealed partial class AssetStore
{
    /// <summary>Every asset file currently on disk (used by retention GC and integrity checks).</summary>
    public IEnumerable<(ulong Hash, string Path, long Bytes)> EnumerateFiles()
    {
        if (!Directory.Exists(Root))
        {
            yield break;
        }

        foreach (string path in Directory.EnumerateFiles(Root, "*.tile", SearchOption.AllDirectories))
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 16
                || !ulong.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
            {
                continue;
            }

            yield return (hash, path, new FileInfo(path).Length);
        }
    }

    /// <summary>Asset count and byte footprint on disk.</summary>
    public (long Count, long Bytes) ComputeStats()
    {
        long count = 0;
        long bytes = 0;
        foreach ((_, _, long size) in EnumerateFiles())
        {
            count++;
            bytes += size;
        }

        return (count, bytes);
    }

    /// <summary>
    /// Asset files in ascending hash order. Shard directories are enumerated in sorted order and so
    /// are files inside them, which makes the stream globally sorted by hash (fixed-width lowercase
    /// hex sorts identically to the numeric value) — the property retention GC merge-walks against.
    /// </summary>
    public IEnumerable<(ulong Hash, string Path, long Bytes)> EnumerateFilesSorted()
    {
        if (!Directory.Exists(Root))
        {
            yield break;
        }

        string[] shards = Directory.GetDirectories(Root);
        Array.Sort(shards, StringComparer.OrdinalIgnoreCase);
        foreach (string shard in shards)
        {
            if (string.Equals(Path.GetFileName(shard), TempFolderName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string[] subShards = Directory.GetDirectories(shard);
            Array.Sort(subShards, StringComparer.OrdinalIgnoreCase);
            foreach (string sub in subShards)
            {
                string[] files = Directory.GetFiles(sub, "*.tile");
                Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                foreach (string path in files)
                {
                    string name = Path.GetFileNameWithoutExtension(path);
                    if (name.Length != 16
                        || !ulong.TryParse(name, System.Globalization.NumberStyles.HexNumber, null, out ulong hash))
                    {
                        continue;
                    }

                    yield return (hash, path, new FileInfo(path).Length);
                }
            }
        }
    }

    /// <summary>Removes temp files left behind by an interrupted write.</summary>
    public int CleanupTempFiles(TimeSpan olderThan)
    {
        int removed = 0;
        if (!Directory.Exists(_tempDir))
        {
            return 0;
        }

        DateTime cutoff = DateTime.UtcNow - olderThan;
        foreach (string file in Directory.EnumerateFiles(_tempDir, "*.part"))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (IOException)
            {
                // Still in use by a live writer: leave it alone.
            }
        }

        return removed;
    }
}
