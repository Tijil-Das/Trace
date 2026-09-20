using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace ScreenRecall.Storage;

/// <summary>Tiles are written, moved or cleared (spec 5.5).</summary>
public enum LogOp : byte
{
    /// <summary>A tile at (X, Y) now holds the given asset.</summary>
    Draw = 0,

    /// <summary>The tile content moved from (SourceX, SourceY) to (X, Y) — both ends are logged.</summary>
    Move = 1,

    /// <summary>The tile at (X, Y) became empty (window closed, region cleared).</summary>
    Clear = 2,
}

/// <summary>
/// Fixed 27-byte reference-log record (spec 5.5). The layout is explicit and little-endian so the
/// bytes are readable from any tooling:
/// <code>
///   0  uint64 timestamp_us
///   8  uint32 window_id
///  12  uint16 monitor_id
///  14  uint16 tile_x
///  16  uint16 tile_y
///  18  uint64 asset_hash
///  26  uint8  op
/// </code>
/// A tile move is logged as a CLEAR of the source tile plus a DRAW of the destination, which keeps
/// the record minimal and makes replay a single uniform loop.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = Size)]
public struct LogEntry
{
    /// <summary>Serialized size in bytes.</summary>
    public const int Size = 27;

    /// <summary>Capture timestamp in microseconds since the unix epoch (UTC).</summary>
    [FieldOffset(0)]
    public long TimestampUs;

    [FieldOffset(8)]
    public uint WindowId;

    [FieldOffset(12)]
    public ushort MonitorId;

    [FieldOffset(14)]
    public ushort TileX;

    [FieldOffset(16)]
    public ushort TileY;

    [FieldOffset(18)]
    public ulong AssetHash;

    [FieldOffset(26)]
    public LogOp Op;

    /// <summary>Serializes the entry into <paramref name="destination"/> (little-endian).</summary>
    public static void Write(Span<byte> destination, in LogEntry entry)
    {
        if (destination.Length < Size)
        {
            throw new ArgumentException($"Destination must be at least {Size} bytes.", nameof(destination));
        }

        BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)entry.TimestampUs);
        BinaryPrimitives.WriteUInt32LittleEndian(destination[8..], entry.WindowId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[12..], entry.MonitorId);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[14..], entry.TileX);
        BinaryPrimitives.WriteUInt16LittleEndian(destination[16..], entry.TileY);
        BinaryPrimitives.WriteUInt64LittleEndian(destination[18..], entry.AssetHash);
        destination[26] = (byte)entry.Op;
    }

    /// <summary>Deserializes an entry (little-endian).</summary>
    public static LogEntry Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
        {
            throw new ArgumentException($"Source must be at least {Size} bytes.", nameof(source));
        }

        LogEntry entry = default;
        entry.TimestampUs = (long)BinaryPrimitives.ReadUInt64LittleEndian(source);
        entry.WindowId = BinaryPrimitives.ReadUInt32LittleEndian(source[8..]);
        entry.MonitorId = BinaryPrimitives.ReadUInt16LittleEndian(source[12..]);
        entry.TileX = BinaryPrimitives.ReadUInt16LittleEndian(source[14..]);
        entry.TileY = BinaryPrimitives.ReadUInt16LittleEndian(source[16..]);
        entry.AssetHash = BinaryPrimitives.ReadUInt64LittleEndian(source[18..]);
        entry.Op = (LogOp)source[26];
        return entry;
    }

    /// <summary>Timestamp as a UTC instant.</summary>
    public DateTimeOffset Timestamp => DateTimeOffset.UnixEpoch.AddTicks(TimestampUs * 10);

    /// <summary>Creates a DRAW entry.</summary>
    public static LogEntry Draw(long timestampUs, uint windowId, ushort monitorId, int tileX, int tileY, ulong hash)
        => new()
        {
            TimestampUs = timestampUs,
            WindowId = windowId,
            MonitorId = monitorId,
            TileX = (ushort)tileX,
            TileY = (ushort)tileY,
            AssetHash = hash,
            Op = LogOp.Draw,
        };

    /// <summary>Creates a CLEAR entry.</summary>
    public static LogEntry Clear(long timestampUs, uint windowId, ushort monitorId, int tileX, int tileY)
        => new()
        {
            TimestampUs = timestampUs,
            WindowId = windowId,
            MonitorId = monitorId,
            TileX = (ushort)tileX,
            TileY = (ushort)tileY,
            AssetHash = TileHash.None,
            Op = LogOp.Clear,
        };

    public override string ToString()
        => $"{Op} m{MonitorId} ({TileX},{TileY}) hash={TileHash.ToHex(AssetHash)} win={WindowId} t={TimestampUs}";
}
