using System.Buffers.Binary;

namespace ScreenRecall.Storage;

public static partial class CheckpointFormat
{
    /// <summary>Parses a serialized checkpoint.</summary>
    public static CheckpointData Deserialize(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < HeaderSize
            || buffer[0] != 'S' || buffer[1] != 'R' || buffer[2] != 'C' || buffer[3] != 'K'
            || buffer[4] != 'P' || buffer[5] != '1')
        {
            throw new InvalidDataException("Not a checkpoint file (bad magic).");
        }

        long timestampUs = (long)BinaryPrimitives.ReadUInt64LittleEndian(buffer[8..]);
        int monitorCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[16..]);
        List<CheckpointMonitorState> states = new(monitorCount);
        int p = HeaderSize;

        for (int m = 0; m < monitorCount; m++)
        {
            if (p + MonitorHeaderSize > buffer.Length)
            {
                throw new InvalidDataException("Truncated checkpoint (monitor header).");
            }

            ushort id = BinaryPrimitives.ReadUInt16LittleEndian(buffer[p..]);
            int tileSize = BinaryPrimitives.ReadUInt16LittleEndian(buffer[(p + 2)..]);
            int x = BinaryPrimitives.ReadInt32LittleEndian(buffer[(p + 4)..]);
            int y = BinaryPrimitives.ReadInt32LittleEndian(buffer[(p + 8)..]);
            int width = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(p + 12)..]);
            int height = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(p + 16)..]);
            int columns = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(p + 20)..]);
            int rows = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(p + 24)..]);
            int entryCount = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[(p + 28)..]);
            p += MonitorHeaderSize;

            MonitorInfo monitor = new(id, string.Empty, x, y, width, height, tileSize);
            ulong[] tiles = new ulong[Math.Max(columns * rows, 1)];
            for (int e = 0; e < entryCount; e++)
            {
                if (p + TileEntrySize > buffer.Length)
                {
                    throw new InvalidDataException("Truncated checkpoint (tile entries).");
                }

                int index = (int)BinaryPrimitives.ReadUInt32LittleEndian(buffer[p..]);
                ulong hash = BinaryPrimitives.ReadUInt64LittleEndian(buffer[(p + 4)..]);
                p += TileEntrySize;
                if (index >= 0 && index < tiles.Length)
                {
                    tiles[index] = hash;
                }
            }

            states.Add(new CheckpointMonitorState(monitor, tiles));
        }

        return new CheckpointData(timestampUs, states);
    }

    /// <summary>Enumerates a session's checkpoints, oldest first, as (timestampUs, path).</summary>
    public static IReadOnlyList<(long TimestampUs, string Path)> List(string sessionDir)
    {
        string dir = System.IO.Path.Combine(sessionDir, DirectoryName);
        if (!Directory.Exists(dir))
        {
            return Array.Empty<(long, string)>();
        }

        List<(long, string)> found = new();
        foreach (string file in Directory.EnumerateFiles(dir, "*.ckpt"))
        {
            if (TryParseFileName(file, out long timestampUs))
            {
                found.Add((timestampUs, file));
            }
        }

        found.Sort((a, b) => a.Item1.CompareTo(b.Item1));
        return found;
    }

    /// <summary>Newest checkpoint at or before the requested timestamp, or the oldest available.</summary>
    public static (long TimestampUs, string Path)? FindAtOrBefore(string sessionDir, long timestampUs)
    {
        IReadOnlyList<(long TimestampUs, string Path)> all = List(sessionDir);
        (long TimestampUs, string Path)? best = null;
        foreach ((long ts, string path) in all)
        {
            if (ts <= timestampUs)
            {
                best = (ts, path);
            }
            else
            {
                break;
            }
        }

        return best ?? (all.Count > 0 ? all[0] : null);
    }
}
