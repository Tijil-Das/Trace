using System.Buffers.Binary;

namespace ScreenRecall.Storage;

/// <summary>Dense tile map of one monitor inside a checkpoint (index == row-major tile index).</summary>
public sealed record CheckpointMonitorState(MonitorInfo Monitor, ulong[] Tiles);

/// <summary>A full "visible state" snapshot (spec 5.6) — the reconstruction equivalent of a keyframe.</summary>
public sealed record CheckpointData(long TimestampUs, IReadOnlyList<CheckpointMonitorState> Monitors)
{
    /// <summary>Wall-clock instant of the snapshot.</summary>
    public DateTimeOffset Timestamp => DateTimeOffset.UnixEpoch.AddTicks(TimestampUs * 10);

    /// <summary>Finds the state for a monitor id.</summary>
    public CheckpointMonitorState? ForMonitor(ushort monitorId)
    {
        foreach (CheckpointMonitorState state in Monitors)
        {
            if (state.Monitor.Id == monitorId)
            {
                return state;
            }
        }

        return null;
    }
}

/// <summary>
/// Checkpoint file format (spec 5.6). Written every few minutes; seeking loads the nearest checkpoint
/// at or before the target time and replays the log forward from there instead of from session start.
/// <code>
///   0  char[6] magic "SRCKP1"
///   6  uint8   version
///   7  uint8   flags (bit0: sparse tile entries)
///   8  uint64  timestamp_us
///  16  uint32  monitor count
///  20  ... per monitor:
///        uint16 monitor_id, uint16 tile_size, int32 x, int32 y,
///        uint32 width, uint32 height, uint32 columns, uint32 rows, uint32 entry count,
///        then entry count x (uint32 tile index, uint64 asset hash)
/// </code>
/// Only non-empty tiles are stored, so a mostly static screen costs a few hundred bytes.
/// </summary>
public static partial class CheckpointFormat
{
    /// <summary>Directory that holds a session's checkpoints.</summary>
    public const string DirectoryName = "checkpoints";

    /// <summary>Current format version.</summary>
    public const byte Version = 1;

    /// <summary>Header size in bytes.</summary>
    public const int HeaderSize = 20;

    /// <summary>Per-monitor fixed size in bytes.</summary>
    public const int MonitorHeaderSize = 30;

    /// <summary>Bytes per stored tile entry.</summary>
    public const int TileEntrySize = 12;

    /// <summary>File name for a checkpoint taken at the given timestamp.</summary>
    public static string FileNameFor(long timestampUs) => $"{timestampUs}.ckpt";

    /// <summary>Reads the sequence timestamp out of a checkpoint file name.</summary>
    public static bool TryParseFileName(string fileName, out long timestampUs)
    {
        timestampUs = 0;
        string name = Path.GetFileNameWithoutExtension(fileName);
        return long.TryParse(name, out timestampUs);
    }

    /// <summary>Serializes a checkpoint.</summary>
    public static byte[] Serialize(long timestampUs, IReadOnlyList<CheckpointMonitorState> monitors)
    {
        int size = HeaderSize;
        foreach (CheckpointMonitorState state in monitors)
        {
            size += MonitorHeaderSize + (CountNonEmpty(state.Tiles) * TileEntrySize);
        }

        byte[] buffer = new byte[size];
        Span<byte> span = buffer;
        "SRCKP1"u8.CopyTo(span);
        span[6] = Version;
        span[7] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], (ulong)timestampUs);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)monitors.Count);
        int p = HeaderSize;

        foreach (CheckpointMonitorState state in monitors)
        {
            MonitorInfo monitor = state.Monitor;
            BinaryPrimitives.WriteUInt16LittleEndian(span[p..], monitor.Id);
            BinaryPrimitives.WriteUInt16LittleEndian(span[(p + 2)..], (ushort)monitor.TileSize);
            BinaryPrimitives.WriteInt32LittleEndian(span[(p + 4)..], monitor.X);
            BinaryPrimitives.WriteInt32LittleEndian(span[(p + 8)..], monitor.Y);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 12)..], (uint)monitor.Width);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 16)..], (uint)monitor.Height);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 20)..], (uint)monitor.Columns);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 24)..], (uint)monitor.Rows);
            BinaryPrimitives.WriteUInt32LittleEndian(span[(p + 28)..], (uint)CountNonEmpty(state.Tiles));
            p += MonitorHeaderSize;

            for (int index = 0; index < state.Tiles.Length; index++)
            {
                ulong hash = state.Tiles[index];
                if (hash == TileHash.None)
                {
                    continue;
                }

                BinaryPrimitives.WriteUInt32LittleEndian(span[p..], (uint)index);
                BinaryPrimitives.WriteUInt64LittleEndian(span[(p + 4)..], hash);
                p += TileEntrySize;
            }
        }

        return buffer;
    }

    /// <summary>Writes a checkpoint atomically (temp file + rename).</summary>
    public static void Write(string path, long timestampUs, IReadOnlyList<CheckpointMonitorState> monitors)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".part";
        File.WriteAllBytes(temp, Serialize(timestampUs, monitors));
        File.Move(temp, path, overwrite: true);
    }

    /// <summary>Reads a checkpoint from disk.</summary>
    public static CheckpointData Read(string path) => Deserialize(File.ReadAllBytes(path));

    private static int CountNonEmpty(ulong[] tiles)
    {
        int count = 0;
        foreach (ulong hash in tiles)
        {
            if (hash != TileHash.None)
            {
                count++;
            }
        }

        return count;
    }
}
