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
///  20  uint32  reserved
///  24  ... per monitor:
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

    /// <summary>Header size in bytes: magic(6) + version(1) + flags(1) + timestamp(8) + monitor count(4) + reserved(4).</summary>
    public const int HeaderSize = 24;

    /// <summary>
    /// Fixed per-monitor header size: id(2) + tileSize(2) + x(4) + y(4) + width(4) + height(4) +
    /// columns(4) + rows(4) + entry count(4) = 32 bytes, followed by 12 bytes per stored tile.
    /// </summary>
    public const int MonitorHeaderSize = 32;

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

    /// <summary>
    /// Serializes a checkpoint. Written through a BinaryWriter rather than hand-rolled offsets: the
    /// buffer size and the write path must never be able to disagree, which is exactly the class of bug
    /// a manual layout invites.
    /// </summary>
    public static byte[] Serialize(long timestampUs, IReadOnlyList<CheckpointMonitorState> monitors)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        Span<byte> magic = stackalloc byte[6];
        "SRCKP1"u8.CopyTo(magic);
        writer.Write(magic);
        writer.Write(Version);
        writer.Write((byte)1); // flags: sparse tile entries
        writer.Write(timestampUs);
        writer.Write((uint)monitors.Count);
        writer.Write(0u); // reserved

        foreach (CheckpointMonitorState state in monitors)
        {
            MonitorInfo monitor = state.Monitor;
            writer.Write(monitor.Id);
            writer.Write((ushort)monitor.TileSize);
            writer.Write(monitor.X);
            writer.Write(monitor.Y);
            writer.Write((uint)monitor.Width);
            writer.Write((uint)monitor.Height);
            writer.Write((uint)monitor.Columns);
            writer.Write((uint)monitor.Rows);
            writer.Write((uint)CountNonEmpty(state.Tiles));

            for (int index = 0; index < state.Tiles.Length; index++)
            {
                ulong hash = state.Tiles[index];
                if (hash == TileHash.None)
                {
                    continue;
                }

                writer.Write((uint)index);
                writer.Write(hash);
            }
        }

        writer.Flush();
        return stream.ToArray();
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
