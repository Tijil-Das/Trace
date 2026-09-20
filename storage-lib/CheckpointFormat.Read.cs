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

        using MemoryStream stream = new(buffer.ToArray(), writable: false);
        using BinaryReader reader = new(stream);
        stream.Position = 6;
        byte version = reader.ReadByte();
        if (version != Version)
        {
            throw new InvalidDataException($"Unsupported checkpoint version {version}.");
        }

        _ = reader.ReadByte(); // flags
        long timestampUs = reader.ReadInt64();
        int monitorCount = (int)reader.ReadUInt32();
        _ = reader.ReadUInt32(); // reserved

        List<CheckpointMonitorState> states = new(monitorCount);
        for (int m = 0; m < monitorCount; m++)
        {
            if (stream.Position + MonitorHeaderSize > stream.Length)
            {
                throw new InvalidDataException("Truncated checkpoint (monitor header).");
            }

            ushort id = reader.ReadUInt16();
            int tileSize = reader.ReadUInt16();
            int x = reader.ReadInt32();
            int y = reader.ReadInt32();
            int width = (int)reader.ReadUInt32();
            int height = (int)reader.ReadUInt32();
            int columns = (int)reader.ReadUInt32();
            int rows = (int)reader.ReadUInt32();
            int entryCount = (int)reader.ReadUInt32();

            MonitorInfo monitor = new(id, string.Empty, x, y, width, height, tileSize);
            ulong[] tiles = new ulong[Math.Max(columns * rows, 1)];

            for (int e = 0; e < entryCount; e++)
            {
                if (stream.Position + TileEntrySize > stream.Length)
                {
                    throw new InvalidDataException("Truncated checkpoint (tile entries).");
                }

                int index = (int)reader.ReadUInt32();
                ulong hash = reader.ReadUInt64();
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
