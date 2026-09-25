using ScreenRecall.Player;
using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// The log's non-tile op codes. A HEARTBEAT says "the recorder was watching and nothing needed writing", so a
/// reader can tell a quiet stretch apart from a stretch nobody recorded; a POINTER entry carries the cursor, which
/// never appears in the duplicated pixels. Both ride in the same 27-byte record as a tile entry, so what has to hold
/// is that they round-trip byte for byte and that nothing applies them to a canvas.
/// </summary>
public sealed class PointerAndHeartbeatLogTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "screenrecall-pointer-" + Guid.NewGuid().ToString("N"));

    public PointerAndHeartbeatLogTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not a test failure.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void PointerEntryRoundTripsWithItsHotspot()
    {
        string path = Path.Combine(_directory, "log.bin");
        using (SessionLogWriter writer = new(path, DateTimeOffset.Now))
        {
            writer.Append(LogEntry.Pointer(
                1_700_000_000_000_000, monitorId: 2, x: 1234, y: 567, hotspotX: 7, hotspotY: 3, shapeHash: 0xABCDEF));
            writer.Flush();
        }

        // A second writer appends to the same segment (the format is append-only and the header is written once).
        using (SessionLogWriter appended = new(path, DateTimeOffset.Now))
        {
            appended.Append(LogEntry.Pointer(1_700_000_000_100_000, 2, 0, 0, 0, 0, shapeHash: 0));
            appended.Flush();
        }

        using SessionLogReader reader = new(path);
        Assert.True(reader.TryReadNext(out LogEntry entry));
        Assert.Equal(LogOp.Pointer, entry.Op);
        Assert.Equal(1_700_000_000_000_000, entry.TimestampUs);
        Assert.Equal(2, entry.MonitorId);
        Assert.Equal(1234, entry.PointerX);
        Assert.Equal(567, entry.PointerY);
        Assert.Equal(7, entry.PointerHotspotX);
        Assert.Equal(3, entry.PointerHotspotY);
        Assert.Equal(0xABCDEFUL, entry.AssetHash);

        // An invisible pointer is the same record with no shape: that is the whole encoding of "not visible".
        Assert.True(reader.TryReadNext(out LogEntry hidden));
        Assert.Equal(LogOp.Pointer, hidden.Op);
        Assert.Equal(0UL, hidden.AssetHash);
    }

    [Fact]
    public void HeartbeatEntryRoundTripsAndCarriesNothingElse()
    {
        string path = Path.Combine(_directory, "heartbeat.bin");
        using (SessionLogWriter writer = new(path, DateTimeOffset.Now))
        {
            writer.Append(LogEntry.Heartbeat(1_700_000_000_000_000));
            writer.Flush();
        }

        using SessionLogReader reader = new(path);
        Assert.True(reader.TryReadNext(out LogEntry entry));
        Assert.Equal(LogOp.Heartbeat, entry.Op);
        Assert.Equal(1_700_000_000_000_000, entry.TimestampUs);
        Assert.Equal(0UL, entry.AssetHash);
        Assert.Equal(0, entry.MonitorId);
    }

    [Fact]
    public void CanvasIgnoresEntriesThatAreNotTiles()
    {
        ScreenCanvas canvas = new();
        canvas.SetMonitor(new MonitorInfo(0, "\\\\.\\DISPLAY1", 0, 0, 128, 64, 64));

        canvas.Apply(LogEntry.Draw(1, windowId: 0, monitorId: 0, tileX: 0, tileY: 0, hash: 0x1111));
        Assert.Equal(0x1111UL, canvas.TileAt(0, 0, 0));

        // The same fields, in records that mean something else entirely: applied blindly, a heartbeat would clear
        // or draw tile (0, 0) with hash 0 and a pointer would draw its shape hash into the picture.
        canvas.Apply(LogEntry.Heartbeat(2));
        canvas.Apply(LogEntry.Pointer(3, monitorId: 0, x: 5, y: 5, hotspotX: 0, hotspotY: 0, shapeHash: 0x9999));

        Assert.Equal(0x1111UL, canvas.TileAt(0, 0, 0));
        Assert.Equal(0UL, canvas.TileAt(0, 1, 0));
    }
}
