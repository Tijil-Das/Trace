using System.Buffers.Binary;

namespace ScreenRecall.Storage;

/// <summary>
/// Packed tile storage (spec §5.3): many tiles appended into shared pack files, with a per-pack index
/// mapping <c>asset_hash → (pack, offset, length)</c>.
///
/// Why this exists: the one-file-per-tile store measured **9.30 ms per new tile** on a scanned volume, and the
/// cost is not the bytes — it is create/write/close/rename per file. At the rates a real desktop produces new
/// content that is ~10% of a four-core machine from the writer alone, plus the capture thread blocked behind it
/// (<c>docs/PERFORMANCE.md</c> §5b). Appending to a pack turns N file creations into one buffered sequential
/// write plus N cheap index appends.
///
/// The layout:
/// <code>
/// assets/packs/000000.pack   [SRAS1 header | payload][SRAS1 header | payload]...
/// assets/packs/000000.idx    [32-byte index record]...
/// </code>
///
/// Crash-consistency follows the same rule as the reference log: <b>the index record is the commit point.</b>
/// Tile bytes are flushed first, then the index record. A crash between the two leaves unreferenced bytes at the
/// end of a pack — garbage no reader can reach, because nothing points at it — and startup truncates the pack
/// back to its last committed offset so the garbage is reclaimed. A crash therefore cannot make a committed tile
/// unreadable, and cannot make a reader follow a half-written tile. A tile whose index record never landed simply
/// is not stored, and the capture loop stores it again.
///
/// Dedupe is unchanged: hashes and the content-addressing contract are exactly as before, and
/// <see cref="Contains"/> becomes an in-memory dictionary lookup instead of a filesystem probe.
/// </summary>
public sealed partial class AssetPackStore : IDisposable
{
    /// <summary>Index record size.</summary>
    public const int IndexRecordSize = 32;

    /// <summary>
    /// Bytes per pack after which the next append rolls to a new file. Not tuned: 64 MB keeps a pack comfortably
    /// inside the OS file cache while staying far below anything that would make one sequential read slow, and a
    /// 2 GB day is only ~32 packs.
    /// </summary>
    public const long DefaultPackBytes = 64L * 1024 * 1024;

    /// <summary>Folder holding pack and index files, inside the store root.</summary>
    public const string PackFolderName = "packs";

    /// <summary>One tile's location inside a pack.</summary>
    public readonly record struct PackEntry(int PackId, long Offset, int RecordLength, byte CodecId, int Width, int Height);

    private readonly string _packsDir;
    private readonly long _packBytes;
    // Authoritative, shared across every instance in this process (keyed by packs dir): the process runs one
    // capture writer and many short-lived readers (retention, GC, tests) concurrently, and each must see the
    // other's tiles. A per-instance index silently served stale answers to any instance that did not perform
    // the write or delete itself — including the retention merge walk's own store. Sharing the dictionaries
    // keeps every instance coherent; the write gate below serializes file access.
    private static readonly object SharedStatesLock = new();
    private static readonly Dictionary<string, SharedPackState> SharedStates = new(StringComparer.OrdinalIgnoreCase);
    private readonly SharedPackState _shared;
    private sealed class SharedPackState
    {
        public readonly object Gate = new();
        public readonly Dictionary<ulong, PackEntry> Index = new();
        public readonly Dictionary<int, int> LivePerPack = new();
        public FileStream? Pack;
        public FileStream? Idx;
        public int CurrentPackId = -1;
        public long CurrentPackLength;

        /// <summary>
        /// Record bytes committed across all packs. Maintained on every append and delete under <see cref="Gate"/>,
        /// so readers pay a field load instead of a full index sum.
        /// </summary>
        public long PackedBytes;
    }

    public AssetPackStore(string assetsRoot, long packBytes = DefaultPackBytes)
    {
        _packsDir = Path.Combine(assetsRoot, PackFolderName);
        _packBytes = packBytes;
        Directory.CreateDirectory(_packsDir);

        lock (SharedStatesLock)
        {
            if (!SharedStates.TryGetValue(_packsDir, out _shared!))
            {
                _shared = new SharedPackState();
                SharedStates[_packsDir] = _shared;
            }
        }

        lock (_shared.Gate)
        {
            if (_shared.Index.Count == 0 && _shared.CurrentPackId < 0)
            {
                LoadIndex();
                long committedPackBytes = 0;
                foreach (PackEntry entry in _shared.Index.Values)
                {
                    committedPackBytes += entry.RecordLength;
                }

                _shared.PackedBytes = committedPackBytes;
            }
        }
    }

    /// <summary>Tiles reachable through packs.</summary>
    public int Count
    {
        get
        {
            lock (_shared.Gate)
            {
                return _shared.Index.Count;
            }
        }
    }

    /// <summary>
    /// Indexed record bytes across all packs, from a counter maintained under the write gate — a field load, not
    /// an index sum. Summing per read is what made every days-list refresh walk every packed tile (~10s for 80k).
    /// </summary>
    public long Bytes
    {
        get
        {
            lock (_shared.Gate)
            {
                return _shared.PackedBytes;
            }
        }
    }

    /// <summary>Packs currently on disk.</summary>
    public int PackCount => Directory.EnumerateFiles(_packsDir, "*.pack").Count();

    /// <summary>True when the hash is packed. Pure dictionary lookup — no filesystem call.</summary>
    public bool Contains(ulong hash) => _shared.Index.ContainsKey(hash);

    /// <summary>
    /// Durable membership probe for short-lived instances (retention, integrity checks): answers whether any pack
    /// index on disk still names the hash, without pulling that pack's whole index into this instance.
    /// </summary>
    public bool ContainsOnDisk(ulong hash)
    {
        foreach (string idxPath in Directory.EnumerateFiles(_packsDir, "*.idx"))
        {
            try
            {
                using FileStream idx = new(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 12);
                Span<byte> record = stackalloc byte[IndexRecordSize];
                while (idx.Position + IndexRecordSize <= idx.Length)
                {
                    idx.ReadExactly(record);
                    if (record[0] != 'S' || record[1] != 'R' || record[2] != 'K' || record[3] != '1')
                    {
                        break;
                    }

                    if (BinaryPrimitives.ReadUInt64LittleEndian(record[4..]) == hash)
                    {
                        return true;
                    }
                }
            }
            catch (IOException)
            {
            }
        }

        return false;
    }

    /// <summary>Packs written through this instance.</summary>
    public long WritesThisInstance { get; private set; }

    /// <summary>Payload+header bytes appended through this instance.</summary>
    public long BytesWrittenThisInstance { get; private set; }

    /// <summary>Appends a tile and commits it with an index record. False when the hash was already packed.</summary>
    public bool Store(ulong hash, byte codecId, int width, int height, ReadOnlySpan<byte> payload, bool assumeMissing)
    {
        lock (_shared.Gate)
        {
            if (_shared.Index.ContainsKey(hash))
            {
                return false;
            }

            EnsureOpen();

            long offset = _shared.CurrentPackLength;
            Span<byte> header = stackalloc byte[AssetStore.HeaderSize];
            AssetStore.WriteHeader(header, hash, codecId, width, height, payload.Length);
            _shared.Pack!.Write(header);
            _shared.Pack.Write(payload);
            _shared.CurrentPackLength += header.Length + payload.Length;

            // The commit: bytes are in the pack, then the index record names where. Flushed before the caller can
            // flush the log, preserving the rule that no log entry becomes durable before its tile.
            _shared.Pack.Flush(flushToDisk: false);

            Span<byte> record = stackalloc byte[IndexRecordSize];
            WriteIndexRecord(record, hash, offset, header.Length + payload.Length, codecId, width, height);
            _shared.Idx!.Write(record);
            _shared.Idx.Flush(flushToDisk: false);

            _shared.Index[hash] = new PackEntry(_shared.CurrentPackId, offset, header.Length + payload.Length, codecId, width, height);
            _shared.LivePerPack[_shared.CurrentPackId] = _shared.LivePerPack.GetValueOrDefault(_shared.CurrentPackId) + 1;
            _shared.PackedBytes += header.Length + payload.Length;
            WritesThisInstance++;
            BytesWrittenThisInstance += header.Length + payload.Length;
            return true;
        }
    }

    /// <summary>Reads a packed tile's payload bytes.</summary>
    public bool TryReadPayload(ulong hash, out byte codecId, out int width, out int height, out byte[] payload)
    {
        codecId = 0;
        width = 0;
        height = 0;
        payload = Array.Empty<byte>();

        if (!_shared.Index.TryGetValue(hash, out PackEntry entry))
        {
            return false;
        }

        using FileStream stream = new(
            PackPath(entry.PackId), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024);
        stream.Seek(entry.Offset, SeekOrigin.Begin);

        Span<byte> header = stackalloc byte[AssetStore.HeaderSize];
        stream.ReadExactly(header);
        ulong stored = BinaryPrimitives.ReadUInt64LittleEndian(header[12..]);
        if (stored != hash)
        {
            throw new InvalidDataException(
                $"Pack {entry.PackId} index says {TileHash.ToHex(hash)} is at offset {entry.Offset}, but the record "
                + $"there is {TileHash.ToHex(stored)}: the index and its pack disagree.");
        }

        byte[] buffer = new byte[entry.RecordLength - AssetStore.HeaderSize];
        stream.ReadExactly(buffer);
        codecId = entry.CodecId;
        width = entry.Width;
        height = entry.Height;
        payload = buffer;
        return true;
    }

    /// <summary>Reads just a packed tile's descriptor.</summary>
    public bool TryReadInfo(ulong hash, out AssetInfo info)
    {
        info = new AssetInfo(hash, 0, 0, 0, 0);
        if (!_shared.Index.TryGetValue(hash, out PackEntry entry))
        {
            return false;
        }

        info = new AssetInfo(hash, entry.CodecId, entry.Width, entry.Height, entry.RecordLength - AssetStore.HeaderSize);
        return true;
    }

    /// <summary>
    /// Drops a tile from the shared in-process index and makes the delete durable: the owning pack's .idx is
    /// atomically rewritten without that record, so a restart cannot resurrect the tile. Returns true when the
    /// tile was present; the pack files go once their last live tile does (<see cref="ReclaimEmptyPacks"/>).
    /// </summary>
    public bool Delete(ulong hash)
    {
        lock (_shared.Gate)
        {
            if (!_shared.Index.Remove(hash, out PackEntry entry))
            {
                return false;
            }

            _shared.PackedBytes -= entry.RecordLength;

            int live = _shared.LivePerPack.GetValueOrDefault(entry.PackId) - 1;
            if (live <= 0)
            {
                _shared.LivePerPack.Remove(entry.PackId);
            }
            else
            {
                _shared.LivePerPack[entry.PackId] = live;
            }

            try
            {
                CommitDeletion(entry.PackId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The rewrite is the durability half of a delete. If it cannot land — a pack index held open by a
                // live writer, a transient sharing violation — the tile is still gone from the shared index, so no
                // reader or writer resurrects it in this process; the on-disk .idx keeps its record and the next
                // retention walk (which reads the index from disk at startup, i.e. after a restart) deletes it again.
                // Swallowing here keeps the caller's count honest: Delete reports the tile as deleted, because it is.
            }

            return true;
        }
    }

    /// <summary>
    /// Deletes pack files that hold no live tiles.
    ///
    /// <b>No compaction, deliberately:</b> a pack with even one live tile is kept whole until its last tile
    /// expires, so reclaiming is always a file delete and never a rewrite. That trades disk space for simplicity
    /// and for never touching a pack a reader might be using. The alternative — compacting survivors into a fresh
    /// pack — needs a rewrite protocol, a second index generation and a way to retire the old pack safely, and is
    /// not worth having until space actually matters.
    /// </summary>
    public (int PacksDeleted, long BytesReclaimed) ReclaimEmptyPacks()
    {
        int deleted = 0;
        long bytes = 0;
        lock (_shared.Gate)
        {
            foreach (string path in Directory.EnumerateFiles(_packsDir, "*.pack").ToList())
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int packId))
                {
                    continue;
                }

                // Never touch the pack being appended to, or one that still has live tiles. CurrentPackId is the
                // only pack any instance in this process can be appending to (the handles are shared), so exempting
                // it is exact — including for the merge walk, whose Delete calls update the same shared state.
                if (packId == _shared.CurrentPackId || _shared.LivePerPack.ContainsKey(packId))
                {
                    continue;
                }

                try
                {
                    long size = new FileInfo(path).Length;
                    File.Delete(path);
                    string idxPath = IndexPath(packId);
                    if (File.Exists(idxPath))
                    {
                        File.Delete(idxPath);
                    }

                    deleted++;
                    bytes += size;
                }
                catch (IOException)
                {
                    // In use: the next pass picks it up.
                }
            }
        }

        return (deleted, bytes);
    }

    /// <summary>
    /// Deletes every temp commit file in the packs dir. The pack handles are process-shared (see
    /// <see cref="SharedPackState"/>), so Dispose intentionally does NOT close them: a short-lived instance
    /// (retention walk, test, integrity check) going out of scope must not yank the writer's handles out from
    /// under it. The OS reclaims them at process exit, and every append reuses them through the shared state.
    /// </summary>
    public void Dispose()
    {
        TryDeleteFiles(_packsDir, "*.del-*.tmp");
    }

    /// <summary>Cleans leftover commit files matching a pattern, best effort.</summary>
    private static void TryDeleteFiles(string dir, string pattern)
    {
        try
        {
            foreach (string path in Directory.EnumerateFiles(dir, pattern))
            {
                TryDeleteFile(path);
            }
        }
        catch (IOException)
        {
        }
    }

    private string PackPath(int packId) => Path.Combine(_packsDir, $"{packId:D6}.pack");

    /// <summary>
    /// Makes one deletion durable by rewriting that pack's .idx from the shared index without the deleted record.
    /// Callers must hold <see cref="SharedPackState.Gate"/>.
    ///
    /// Two paths, because a pack's index may or may not be held open by this process:
    /// the pack a writer is appending to has a live handle, and that handle <i>is</i> ours (the state is shared), so
    /// the rewrite goes through it — truncate, write the survivors, seek back to the end for the next append. An
    /// atomic temp-and-move replace is not available there: replacing a file under a live writer fails with a
    /// sharing violation, which is why the older per-instance design could not do this at all. A sealed pack's index
    /// has no handle, so it is reopened for writing instead.
    ///
    /// A crash mid-rewrite tears the tail of the index, which startup already handles: <see cref="LoadIndex"/> stops
    /// at the first bad record, so every record before the tear still loads. The worst case is one tile resurrecting
    /// after a crash — the same outcome as before the rewrite — never a torn middle that hides good tiles.
    /// </summary>
    private void CommitDeletion(int packId)
    {
        if (packId == _shared.CurrentPackId && _shared.Idx is not null)
        {
            _shared.Idx.SetLength(0);
            _shared.Idx.Seek(0, SeekOrigin.Begin);
            WriteIndexForPack(_shared.Idx, packId);
            _shared.Idx.Flush(flushToDisk: false);
            return;
        }

        using FileStream idx = new(
            IndexPath(packId), FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 12);
        idx.SetLength(0);
        idx.Seek(0, SeekOrigin.Begin);
        WriteIndexForPack(idx, packId);
        idx.Flush(flushToDisk: true);
    }

    /// <summary>Writes the live index records of one pack to <paramref name="idx"/>, in index order.</summary>
    private void WriteIndexForPack(FileStream idx, int packId)
    {
        Span<byte> record = stackalloc byte[IndexRecordSize];
        foreach (KeyValuePair<ulong, PackEntry> pair in _shared.Index)
        {
            if (pair.Value.PackId != packId)
            {
                continue;
            }

            WriteIndexRecord(
                record, pair.Key, pair.Value.Offset, pair.Value.RecordLength,
                pair.Value.CodecId, pair.Value.Width, pair.Value.Height);
            idx.Write(record);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Path of a pack file, for callers that only need to name it (retention timestamps, diagnostics).</summary>
    public string PathOf(int packId) => PackPath(packId);

    /// <summary>Every indexed tile and where it lives.</summary>
    public IEnumerable<(ulong Hash, PackEntry Entry)> Entries()
    {
        foreach (KeyValuePair<ulong, PackEntry> pair in _shared.Index)
        {
            yield return (pair.Key, pair.Value);
        }
    }

    private string IndexPath(int packId) => Path.Combine(_packsDir, $"{packId:D6}.idx");

    /// <summary>Opens the live pack, rolling to a new file once the current one has reached the pack size.</summary>
    private void EnsureOpen()
    {
        if (_shared.Pack is not null && _shared.CurrentPackLength < _packBytes)
        {
            return;
        }

        _shared.Pack?.Dispose();
        _shared.Idx?.Dispose();

        _shared.CurrentPackId = NextPackId();
        _shared.Pack = new FileStream(
            PackPath(_shared.CurrentPackId), FileMode.CreateNew, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan);
        _shared.Idx = new FileStream(
            IndexPath(_shared.CurrentPackId), FileMode.CreateNew, FileAccess.Write,
            FileShare.ReadWrite | FileShare.Delete, 1 << 12, FileOptions.SequentialScan);
        _shared.CurrentPackLength = 0;
    }

    private int NextPackId()
    {
        int highest = -1;
        foreach (string path in Directory.EnumerateFiles(_packsDir, "*.pack"))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int id) && id > highest)
            {
                highest = id;
            }
        }

        return highest + 1;
    }

    private static void WriteIndexRecord(
        Span<byte> destination, ulong hash, long offset, int recordLength, byte codecId, int width, int height)
    {
        "SRK1"u8.CopyTo(destination);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[4..], hash);
        BinaryPrimitives.WriteInt64LittleEndian(destination[12..], offset);
        BinaryPrimitives.WriteInt32LittleEndian(destination[20..], recordLength);
        destination[24] = codecId;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[25..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[27..], (ushort)height);
        destination[29] = 0;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[30..], 0);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Reads every pack index into memory, stopping each at its first bad record.
    ///
    /// Recovering from a crash is the whole point of this method. The commit order is data-then-index, so the only
    /// damaged state possible is a pack tail with no usable index record — and that is exactly what the two checks
    /// below catch: a record whose magic is wrong (a torn index write), and a record whose extent runs past the end
    /// of the pack (index written, data not). Neither is reachable by a reader, so both are ignored, and the pack is
    /// trimmed back to its last committed offset so the next append starts from a clean boundary instead of after
    /// garbage. A pack with no valid record at all is a leftover from an interrupted first append: both files go.
    /// </summary>
    private void LoadIndex()
    {
        List<string> packFiles = Directory.EnumerateFiles(_packsDir, "*.pack").ToList();
        packFiles.Sort(StringComparer.OrdinalIgnoreCase);

        foreach (string packPath in packFiles)
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(packPath), out int packId))
            {
                continue;
            }

            string idxPath = IndexPath(packId);
            if (!File.Exists(idxPath))
            {
                TryDelete(packPath); // unreferenced bytes: nothing can reach them
                continue;
            }

            long packLength = new FileInfo(packPath).Length;
            long committedLength = 0;
            int goodRecords = 0;

            using (FileStream idx = new(idxPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 12))
            {
                byte[] buffer = new byte[IndexRecordSize];
                while (idx.Position + IndexRecordSize <= idx.Length)
                {
                    idx.ReadExactly(buffer);
                    Span<byte> record = buffer;
                    if (record[0] != 'S' || record[1] != 'R' || record[2] != 'K' || record[3] != '1')
                    {
                        break; // torn index write
                    }

                    ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(record[4..]);
                    long offset = BinaryPrimitives.ReadInt64LittleEndian(record[12..]);
                    int recordLength = BinaryPrimitives.ReadInt32LittleEndian(record[20..]);
                    if (recordLength < AssetStore.HeaderSize || offset < 0 || offset + recordLength > packLength)
                    {
                        break; // the index record outran the data: the data never landed
                    }

                    _shared.Index[hash] = new PackEntry(
                        packId,
                        offset,
                        recordLength,
                        record[24],
                        BinaryPrimitives.ReadUInt16LittleEndian(record[25..]),
                        BinaryPrimitives.ReadUInt16LittleEndian(record[27..]));
                    committedLength = Math.Max(committedLength, offset + recordLength);
                    goodRecords++;
                }
            }

            if (goodRecords == 0)
            {
                TryDelete(packPath);
                TryDelete(idxPath);
                continue;
            }

            _shared.LivePerPack[packId] = goodRecords;

            // Trim an interrupted tail so the next append starts on a record boundary. Skipped for the newest
            // pack, which is the one a live writer may still be appending to.
            bool isNewest = string.Equals(packPath, packFiles[^1], StringComparison.OrdinalIgnoreCase);
            if (!isNewest)
            {
                if (packLength > committedLength)
                {
                    using FileStream pack = new(packPath, FileMode.Open, FileAccess.Write, FileShare.None);
                    pack.SetLength(committedLength);
                }

                long idxLength = new FileInfo(idxPath).Length;
                long committedIndexLength = (long)goodRecords * IndexRecordSize;
                if (idxLength > committedIndexLength)
                {
                    using FileStream idx = new(idxPath, FileMode.Open, FileAccess.Write, FileShare.None);
                    idx.SetLength(committedIndexLength);
                }
            }
        }
    }
}
