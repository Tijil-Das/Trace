using System.Buffers.Binary;

namespace ScreenRecall.Storage;

/// <summary>
/// Raw ground-truth frame dump used by the fidelity harness (spec 12). Test-only: the capture service
/// writes one of these alongside normal operation, and the verifier pixel-diffs it against the frame
/// the player reconstructs for the same timestamp.
/// <code>
///   0  char[5] magic "SRGT1"
///   5  uint8   version
///   6  uint16  monitor id
///   8  int32   x (virtual desktop), int32 y
///  16  uint32  width, uint32 height
///  24  uint32  pixel data length (width * height * 4)
///  28  ... BGRA rows, tightly packed
/// </code>
/// </summary>
public static class GroundTruthFrame
{
    /// <summary>Header size in bytes.</summary>
    public const int HeaderSize = 28;

    /// <summary>Writes a frame dump atomically.</summary>
    public static void Write(string path, ushort monitorId, int x, int y, int width, int height, ReadOnlySpan<byte> bgra)
    {
        int expected = width * height * 4;
        if (bgra.Length < expected)
        {
            throw new ArgumentException($"Expected {expected} pixel bytes, got {bgra.Length}.", nameof(bgra));
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] buffer = new byte[HeaderSize + expected];
        Span<byte> span = buffer;
        "SRGT1"u8.CopyTo(span);
        span[5] = 1;
        BinaryPrimitives.WriteUInt16LittleEndian(span[6..], monitorId);
        BinaryPrimitives.WriteInt32LittleEndian(span[8..], x);
        BinaryPrimitives.WriteInt32LittleEndian(span[12..], y);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)width);
        BinaryPrimitives.WriteUInt32LittleEndian(span[20..], (uint)height);
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], (uint)expected);
        bgra[..expected].CopyTo(span[HeaderSize..]);

        string temp = path + ".part";
        File.WriteAllBytes(temp, buffer);
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>File name for a ground-truth frame captured at the given timestamp.</summary>
    public static string FileNameFor(long timestampUs, ushort monitorId) => $"{timestampUs}-{monitorId}.raw";

    /// <summary>Reads a frame dump.</summary>
    public static GroundTruthImage Read(string path)
    {
        byte[] buffer = File.ReadAllBytes(path);
        if (buffer.Length < HeaderSize
            || buffer[0] != 'S' || buffer[1] != 'R' || buffer[2] != 'G' || buffer[3] != 'T' || buffer[4] != '1')
        {
            throw new InvalidDataException($"'{path}' is not a ground-truth frame dump.");
        }

        ushort monitorId = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(6));
        int x = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(8));
        int y = BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(12));
        int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(16));
        int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(20));
        int length = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(24));
        if (length != width * height * 4 || buffer.Length < HeaderSize + length)
        {
            throw new InvalidDataException($"Ground-truth frame '{path}' is truncated or inconsistent.");
        }

        byte[] pixels = new byte[length];
        Array.Copy(buffer, HeaderSize, pixels, 0, length);
        return new GroundTruthImage(monitorId, x, y, width, height, pixels);
    }

    /// <summary>Enumerates a session's ground-truth frames in capture order.</summary>
    public static IReadOnlyList<string> List(string sessionDir)
    {
        string dir = Path.Combine(sessionDir, SessionLayout.GroundTruthDirName);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(dir, "*.raw")
            .OrderBy(file => long.TryParse(Path.GetFileName(file).Split('-')[0], out long ts) ? ts : 0)
            .ToArray();
    }

    /// <summary>Timestamp embedded in a ground-truth file name.</summary>
    public static bool TryParseTimestamp(string fileName, out long timestampUs)
    {
        timestampUs = 0;
        string stem = Path.GetFileNameWithoutExtension(fileName);
        int dash = stem.IndexOf('-');
        return dash > 0 && long.TryParse(stem[..dash], out timestampUs);
    }
}

/// <summary>A decoded ground-truth frame.</summary>
public sealed record GroundTruthImage(ushort MonitorId, int X, int Y, int Width, int Height, byte[] Bgra);
