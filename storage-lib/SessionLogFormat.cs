using System.Buffers.Binary;

namespace ScreenRecall.Storage;

/// <summary>Parsed reference-log file header.</summary>
public sealed record SessionLogHeader(byte Version, long DayStartUnixMs, uint Flags);

/// <summary>
/// Reference-log container format (spec 5.5): a 32-byte header followed by a stream of fixed
/// 27-byte <see cref="LogEntry"/> records. Sequential, append-only, cheap to write, cheap to
/// stream-replay.
/// <code>
///   0  char[6] magic "SRLOG1"
///   6  uint8   version
///   7  uint8   reserved
///   8  uint64  day start (unix ms, local day the log belongs to)
///  16  uint32  flags
///  20  uint32  header size (32)
///  24  uint32  entry size (27)
///  28  uint32  reserved
/// </code>
/// </summary>
public static class SessionLogFormat
{
    /// <summary>Name of the first log segment in a session directory.</summary>
    public const string BaseFileName = "log.bin";

    /// <summary>Header size in bytes.</summary>
    public const int HeaderSize = 32;

    /// <summary>Current format version.</summary>
    public const byte Version = 1;

    /// <summary>Builds the header bytes for a day's log.</summary>
    public static byte[] CreateHeaderBytes(DateTimeOffset dayStartLocal)
    {
        byte[] header = new byte[HeaderSize];
        "SRLOG1"u8.CopyTo(header);
        header[6] = Version;
        header[7] = 0;
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(8), (ulong)dayStartLocal.ToUnixTimeMilliseconds());
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(20), HeaderSize);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), LogEntry.Size);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), 0);
        return header;
    }

    /// <summary>Parses and validates a header.</summary>
    public static bool TryReadHeader(ReadOnlySpan<byte> buffer, out SessionLogHeader header)
    {
        header = new SessionLogHeader(0, 0, 0);
        if (buffer.Length < HeaderSize)
        {
            return false;
        }

        if (buffer[0] != 'S' || buffer[1] != 'R' || buffer[2] != 'L' || buffer[3] != 'O'
            || buffer[4] != 'G' || buffer[5] != '1')
        {
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(buffer[24..]) != LogEntry.Size)
        {
            return false;
        }

        header = new SessionLogHeader(
            buffer[6],
            (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]));
        return true;
    }

    /// <summary>Log segments of a session in replay order (a service restart opens a new segment).</summary>
    public static IReadOnlyList<string> FilesFor(string sessionDir)
    {
        if (!Directory.Exists(sessionDir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(sessionDir, "log*.bin")
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>Path of a log segment by index.</summary>
    public static string SegmentPath(string sessionDir, int segmentIndex)
        => Path.Combine(sessionDir, segmentIndex == 0 ? BaseFileName : $"log.{segmentIndex:0000}.bin");
}
