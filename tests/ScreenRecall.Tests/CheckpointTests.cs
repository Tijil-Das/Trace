using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>Checkpoint and canvas round-trips, including the geometry cases that used to break them.</summary>
public sealed class CheckpointTests
{
    [Theory]
    [InlineData(0, 0, 1366, 768)]
    [InlineData(1366, 0, 1366, 768)]   // second monitor, origin not tile aligned
    [InlineData(-1920, -100, 1920, 1080)]
    [InlineData(0, 0, 3840, 2160)]
    public void RoundTripsMonitorGeometryIncludingEdgeTiles(int x, int y, int width, int height)
    {
        MonitorInfo monitor = new(7, "test", x, y, width, height, TileGrid.DefaultTileSize);
        ulong[] tiles = new ulong[monitor.TileCount];
        for (int i = 0; i < tiles.Length; i++)
        {
            tiles[i] = i % 3 == 0 ? 0 : (ulong)(i + 1);
        }

        byte[] serialized = CheckpointFormat.Serialize(1_789_912_729_000_000, new[] { new CheckpointMonitorState(monitor, tiles) });
        CheckpointData parsed = CheckpointFormat.Deserialize(serialized);

        Assert.Equal(1_789_912_729_000_000, parsed.TimestampUs);
        CheckpointMonitorState state = Assert.Single(parsed.Monitors);

        // Device name is intentionally not persisted in a checkpoint (meta.json owns it), so geometry
        // is compared field by field.
        Assert.Equal(monitor.Id, state.Monitor.Id);
        Assert.Equal(monitor.X, state.Monitor.X);
        Assert.Equal(monitor.Y, state.Monitor.Y);
        Assert.Equal(monitor.Width, state.Monitor.Width);
        Assert.Equal(monitor.Height, state.Monitor.Height);
        Assert.Equal(monitor.TileSize, state.Monitor.TileSize);
        Assert.Equal(monitor.Columns, state.Monitor.Columns);
        Assert.Equal(monitor.Rows, state.Monitor.Rows);
        Assert.Equal(tiles, state.Tiles);
    }

    [Fact]
    public void PartialEdgeTileRectsStayInsideTheMonitor()
    {
        // 1366 is not a multiple of 64: the last column is a 22px-wide partial tile.
        MonitorInfo monitor = new(0, "test", 0, 0, 1366, 768, 64);
        Assert.Equal(22, monitor.Columns);
        Assert.Equal(12, monitor.Rows);

        monitor.TilePixelRect(monitor.OriginCellX + 21, monitor.OriginCellY, out int x, out int y, out int w, out int h);
        Assert.Equal(1344, x);
        Assert.Equal(0, y);
        Assert.Equal(22, w);
        Assert.Equal(64, h);
        Assert.Equal(1366, x + w);
    }

    [Fact]
    public void CheckpointFilesAreWrittenAtomicallyAndListedInOrder()
    {
        string root = Path.Combine(Path.GetTempPath(), "screenrecall-tests", Guid.NewGuid().ToString("n"));
        DateOnly day = new(2026, 9, 20);
        SessionStore store = SessionStore.Open(root, day, 64);
        MonitorInfo monitor = new(0, "test", 0, 0, 128, 128, 64);
        ulong[] tiles = { 1, 2, 3, 4 };

        try
        {
            for (int i = 0; i < 3; i++)
            {
                CheckpointFormat.Write(store.CheckpointPathFor(1000 + i), 1000 + i, new[] { new CheckpointMonitorState(monitor, tiles) });
            }

            IReadOnlyList<(long TimestampUs, string Path)> listed = CheckpointFormat.List(store.SessionDir);
            Assert.Equal(3, listed.Count);
            Assert.Equal(new long[] { 1000, 1001, 1002 }, listed.Select(entry => entry.TimestampUs));
            Assert.False(File.Exists(store.CheckpointPathFor(1002) + ".part"));

            (long TimestampUs, string Path)? atOrBefore = CheckpointFormat.FindAtOrBefore(store.SessionDir, 1001);
            Assert.NotNull(atOrBefore);
            Assert.Equal(1001, atOrBefore!.Value.TimestampUs);
            Assert.Equal(1000, CheckpointFormat.FindAtOrBefore(store.SessionDir, 1000)!.Value.TimestampUs);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
