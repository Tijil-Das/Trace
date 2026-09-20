using System.Buffers.Binary;

namespace ScreenRecall.Storage;

/// <summary>
/// Per-day manifest of every asset hash a session references. This is what makes retention GC and
/// integrity verification cheap: instead of re-reading (and re-hashing) every log entry in the
/// retention window, the pruner streams these 8-byte-per-hash files into a scratch table and deletes
/// assets that no retained day mentions.
/// </summary>
public static class AssetManifest
{
    /// <summary>Header size of a manifest file.</summary>
    public const int HeaderSize = 8;

    private const string Magic = "SRMAN1";

    /// <summary>File name of the first manifest of a day.</summary>
    public const string BaseFileName = "assets.idx";

    /// <summary>Sorts manifest file names so restart-generated parts replay in order.</summary>
    public static IReadOnlyList<string> FilesFor(string sessionDir)
    {
        if (!Directory.Exists(sessionDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(sessionDir, "assets*.idx")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Creates a manifest file part (used when a session/appender restarts).</summary>
    public static string NextPartPath(string sessionDir, int part)
        => Path.Combine(sessionDir, part == 0 ? BaseFileName : $"assets.{part}.idx");

    /// <summary>Writes the fixed manifest header.</summary>
    public static byte[] CreateHeaderBytes()
    {
        byte[] header = new byte[HeaderSize];
        MagicBytes().CopyTo(header.AsSpan(0));
        header[6] = 1; // version
        header[7] = 0; // flags
        return header;
    }

    /// <summary>Validates a manifest header.</summary>
    public static bool ValidateHeader(ReadOnlySpan<byte> header)
    {
        if (header.Length < HeaderSize)
        {
            return false;
        }

        for (int i = 0; i < 6; i++)
        {
            if (header[i] != MagicBytes()[i])
            {
                return false;
            }
        }

        return header[6] == 1;
    }

    /// <summary>Streams every hash in a manifest file (in file order, may contain duplicates).</summary>
    public static IEnumerable<ulong> ReadHashes(string path)
    {
        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        byte[] header = new byte[HeaderSize];
        if (stream.Length < HeaderSize)
        {
            yield break;
        }

        stream.ReadExactly(header);
        if (!ValidateHeader(header))
        {
            yield break;
        }

        byte[] buffer = new byte[8];
        while (stream.Position + 8 <= stream.Length)
        {
            if (stream.Read(buffer, 0, 8) < 8)
            {
                yield break;
            }

            ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(buffer);
            if (hash != TileHash.None)
            {
                yield return hash;
            }
        }
    }

    /// <summary>Distinct hashes across all manifest parts of a session.</summary>
    public static HashSet<ulong> ReadDistinctHashes(string sessionDir)
    {
        HashSet<ulong> hashes = new();
        foreach (string file in FilesFor(sessionDir))
        {
            foreach (ulong hash in ReadHashes(file))
            {
                hashes.Add(hash);
            }
        }

        return hashes;
    }

    private static ReadOnlySpan<byte> MagicBytes() => "SRMAN1"u8;
}

/// <summary>
/// Append-only writer for a day's asset manifest. Keeps the day's distinct hashes in memory so only
/// genuinely new hashes hit the disk, which keeps this comfortably inside the CPU/I-O budget.
/// </summary>
public sealed class AssetManifestWriter : IDisposable
{
    private readonly HashSet<ulong> _seen = new();
    private readonly FileStream _stream;
    private bool _disposed;

    public AssetManifestWriter(string path, bool append)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _stream = new FileStream(path, append && File.Exists(path) ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16);
        if (_stream.Length == 0)
        {
            _stream.Write(AssetManifest.CreateHeaderBytes());
        }
    }

    /// <summary>Hashes already recorded for this session.</summary>
    public int DistinctHashes => _seen.Count;

    /// <summary>Bytes appended to the manifest file.</summary>
    public long BytesWritten { get; private set; }

    /// <summary>Records a hash; duplicate hashes are dropped in memory before touching the file.</summary>
    public bool Add(ulong hash)
    {
        if (hash == TileHash.None || !_seen.Add(hash))
        {
            return false;
        }

        Span<byte> buffer = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, hash);
        _stream.Write(buffer);
        BytesWritten += 8;
        return true;
    }

    /// <summary>Flushes pending writes.</summary>
    public void Flush() => _stream.Flush();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }
}
