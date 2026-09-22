namespace ScreenRecall.Storage;

public sealed partial class AssetStore
{
    /// <summary>Every asset the store can serve (packed and legacy), used by retention GC and integrity checks.</summary>
    public IEnumerable<(ulong Hash, string Path, long Bytes)> EnumerateFiles()
    {
        foreach ((ulong hash, string path, long bytes) in EnumerateFilesSorted())
        {
            yield return (hash, path, bytes);
        }
    }

    /// <summary>Packed index entries, for tooling that needs the physical location.</summary>
    public IEnumerable<(ulong Hash, AssetPackStore.PackEntry Entry)> PackedEntries() => _packs.Entries();

    /// <summary>
    /// Asset count and byte footprint. Packed tiles come from the in-memory index — no directory walk at all, which
    /// is what makes this callable from a status poll again: a 30 s profile found the old walk of a 370k-file store
    /// burning 80% of the capture thread (<c>docs/PERFORMANCE.md</c> §5b). Legacy per-file assets still need the
    /// listing, and they shrink with retention until there are none.
    /// </summary>
    public (long Count, long Bytes) ComputeStats()
    {
        long count = _packs.Count;
        long bytes = _packs.Bytes;

        if (!Directory.Exists(Root))
        {
            return (count, bytes);
        }

        try
        {
            foreach (FileInfo file in new DirectoryInfo(Root).EnumerateFiles("*.tile", SearchOption.AllDirectories))
            {
                try
                {
                    bytes += file.Length;
                    count++;
                }
                catch (IOException)
                {
                    // Deleted between listing and measuring (retention GC): skip it.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A shard can disappear underneath the walk (retention GC). Report what was counted: this
            // feeds a status display, and the next pass picks up the true totals.
        }

        return (count, bytes);
    }

    /// <summary>
    /// Every asset the store can serve, packed and legacy, in ascending hash order. Shard directories are
    /// enumerated in sorted order and so are files inside them, which makes the legacy stream globally sorted by
    /// hash (fixed-width lowercase hex sorts identically to the numeric value) — the property retention GC
    /// merge-walks against. Packed tiles are merged in by hash so the combined stream stays sorted.
    /// </summary>
    public IEnumerable<(ulong Hash, string Path, long Bytes)> EnumerateFilesSorted()
    {
        List<(ulong Hash, long Bytes, int PackId)> packed = new();
        foreach ((ulong hash, AssetPackStore.PackEntry entry) in _packs.Entries())
        {
            packed.Add((hash, entry.RecordLength, entry.PackId));
        }

        if (packed.Count == 0)
        {
            foreach ((ulong hash, string path, long bytes) in EnumerateLegacySorted())
            {
                yield return (hash, path, bytes);
            }

            yield break;
        }

        packed.Sort((left, right) => left.Hash.CompareTo(right.Hash));
        using IEnumerator<(ulong Hash, string Path, long Bytes)> legacy = EnumerateLegacySorted().GetEnumerator();
        bool hasLegacy = legacy.MoveNext();
        int index = 0;

        while (hasLegacy || index < packed.Count)
        {
            if (!hasLegacy || (index < packed.Count && packed[index].Hash < legacy.Current.Hash))
            {
                (ulong hash, long bytes, int packId) = packed[index++];
                yield return (hash, Packs.PathOf(packId), bytes);
                continue;
            }

            if (index < packed.Count && packed[index].Hash == legacy.Current.Hash)
            {
                index++; // the same tile exists twice; the packed copy is authoritative
            }

            yield return legacy.Current;
            hasLegacy = legacy.MoveNext();
        }
    }

    private IEnumerable<(ulong Hash, string Path, long Bytes)> EnumerateLegacySorted()
    {
        if (!Directory.Exists(Root))
        {
            yield break;
        }

        string[] shards = Directory.GetDirectories(Root);
        Array.Sort(shards, StringComparer.OrdinalIgnoreCase);
        foreach (string shard in shards)
        {
            if (string.Equals(Path.GetFileName(shard), TempFolderName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(shard), AssetPackStore.PackFolderName, StringComparison.OrdinalIgnoreCase))
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
    public int CleanupTempFiles(TimeSpan olderThan) => CleanupTempFilesDetailed(olderThan).Deleted;

    /// <summary>
    /// Deletes packs whose tiles have all expired, and reports the space. <b>No compaction:</b> a pack with even
    /// one live tile is kept whole until its last tile expires, so reclaiming is always a file delete and never a
    /// rewrite — the simpler of the two options the spec allows, and the one that cannot race a reader.
    /// </summary>
    public (int PacksDeleted, long BytesReclaimed) ReclaimEmptyPacks() => _packs.ReclaimEmptyPacks();

    /// <summary>Removes temp files left behind by an interrupted write, reporting the bytes reclaimed.</summary>
    public (int Deleted, long Bytes) CleanupTempFilesDetailed(TimeSpan olderThan)
    {
        int removed = 0;
        long bytes = 0;
        if (!Directory.Exists(_tempDir))
        {
            return (0, 0);
        }

        DateTime cutoff = DateTime.UtcNow - olderThan;
        foreach (FileInfo file in new DirectoryInfo(_tempDir).EnumerateFiles("*.part"))
        {
            try
            {
                if (file.LastWriteTimeUtc < cutoff)
                {
                    long size = file.Length;
                    file.Delete();
                    removed++;
                    bytes += size;
                }
            }
            catch (IOException)
            {
                // Still in use by a live writer: leave it alone.
            }
        }

        return (removed, bytes);
    }
}
