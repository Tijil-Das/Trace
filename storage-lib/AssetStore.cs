using System.Buffers.Binary;

namespace ScreenRecall.Storage;

/// <summary>Asset file header as persisted in the store (24 bytes, versioned by magic "SRAS1").</summary>
public sealed record AssetInfo(ulong Hash, byte CodecId, int Width, int Height, int PayloadLength);

/// <summary>
/// Content-addressable asset store (spec 5.3 / 6): one immutable file per distinct tile, sharded by
/// hash prefix like git's object store so no single directory ends up holding millions of files:
/// <c>&lt;root&gt;/assets/ab/cd/abcd...eff.tile</c>. Writes go to a temp file and are then renamed
/// atomically, so a crash mid-write can never leave a partial asset that a log entry points at.
/// </summary>
public sealed partial class AssetStore
{
    /// <summary>Size of the fixed asset header.</summary>
    public const int HeaderSize = 24;

    private const string TempFolderName = "tmp";

    private readonly string _tempDir;
    private readonly HashSet<string> _knownDirectories = new(StringComparer.OrdinalIgnoreCase);

    public AssetStore(string assetsRoot)
    {
        Root = assetsRoot ?? throw new ArgumentNullException(nameof(assetsRoot));
        _tempDir = Path.Combine(Root, TempFolderName);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(_tempDir);
    }

    /// <summary>Root directory of the store.</summary>
    public string Root { get; }

    /// <summary>Number of new assets written through this instance.</summary>
    public long StoresThisInstance { get; private set; }

    /// <summary>Payload+header bytes written through this instance.</summary>
    public long BytesWrittenThisInstance { get; private set; }

    /// <summary>Dedupe hits observed through this instance.</summary>
    public long DedupeHitsThisInstance { get; private set; }

    /// <summary>Full path an asset with the given hash lives at.</summary>
    public string PathFor(ulong hash)
    {
        string hex = TileHash.ToHex(hash);
        return Path.Combine(Root, hex[..2], hex.Substring(2, 2), hex + ".tile");
    }

    /// <summary>Cheap existence probe used by the dedupe path.</summary>
    public bool Contains(ulong hash) => File.Exists(PathFor(hash));

    /// <summary>Records a dedupe hit in the live counters.</summary>
    public void NoteDedupeHit() => DedupeHitsThisInstance++;

    /// <summary>Stores a compressed tile payload. Returns false when the asset already existed.</summary>
    /// <param name="hash">Content hash of the tile.</param>
    /// <param name="codecId">Codec that produced the payload.</param>
    /// <param name="width">Tile width in pixels.</param>
    /// <param name="height">Tile height in pixels.</param>
    /// <param name="payload">Encoded payload.</param>
    /// <param name="assumeMissing">
    /// Set by callers that already probed the store, skipping a redundant existence check on the hot path.
    /// </param>
    public bool Store(
        ulong hash,
        byte codecId,
        int width,
        int height,
        ReadOnlySpan<byte> payload,
        bool assumeMissing = false)
    {
        string path = PathFor(hash);
        if (!assumeMissing && File.Exists(path))
        {
            DedupeHitsThisInstance++;
            return false;
        }

        string? directory = Path.GetDirectoryName(path);
        if (directory is not null && _knownDirectories.Add(directory))
        {
            // Shard directories repeat constantly; creating them once turns a syscall per tile into
            // almost none, which matters on the steady-state path.
            Directory.CreateDirectory(directory);
        }

        string temp = Path.Combine(_tempDir, Guid.NewGuid().ToString("n") + ".part");

        try
        {
            using (FileStream stream = new(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024))
            {
                Span<byte> header = stackalloc byte[HeaderSize];
                WriteHeader(header, hash, codecId, width, height, payload.Length);
                stream.Write(header);
                stream.Write(payload);
                stream.Flush(flushToDisk: false);
            }

            try
            {
                File.Move(temp, path, overwrite: false);
            }
            catch (IOException)
            {
                // Another writer won the race, or the asset appeared meanwhile. The store is
                // content-addressable, so the file already there is equivalent by definition.
                File.Delete(temp);
                DedupeHitsThisInstance++;
                return false;
            }

            StoresThisInstance++;
            BytesWrittenThisInstance += payload.Length + HeaderSize;
            return true;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>Deletes an asset. Returns true when a file was removed.</summary>
    public bool Delete(ulong hash)
    {
        string path = PathFor(hash);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    internal static void WriteHeader(Span<byte> destination, ulong hash, byte codecId, int width, int height, int payloadLength)
    {
        "SRAS1"u8.CopyTo(destination);
        destination[5] = codecId;
        BinaryPrimitives.WriteUInt16LittleEndian(destination[6..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[8..], (ushort)height);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[10..], 0);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[12..], hash);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[20..], (uint)payloadLength);
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
}
