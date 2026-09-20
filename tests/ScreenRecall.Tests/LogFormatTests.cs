using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>Reference-log format, writer and reader behaviour.</summary>
public sealed class LogFormatTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "screenrecall-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void LogEntryRoundTripsExactly()
    {
        LogEntry entry = LogEntry.Draw(1_789_912_729_000_000, 0xDEADBEEF, 3, 1234, 5678, 0x0123456789ABCDEF);

        byte[] buffer = new byte[LogEntry.Size];
        LogEntry.Write(buffer, entry);
        LogEntry read = LogEntry.Read(buffer);

        Assert.Equal(LogEntry.Size, buffer.Length);
        Assert.Equal(entry.TimestampUs, read.TimestampUs);
        Assert.Equal(entry.WindowId, read.WindowId);
        Assert.Equal(entry.MonitorId, read.MonitorId);
        Assert.Equal(entry.TileX, read.TileX);
        Assert.Equal(entry.TileY, read.TileY);
        Assert.Equal(entry.AssetHash, read.AssetHash);
        Assert.Equal(LogOp.Draw, read.Op);
    }

    [Fact]
    public void LayoutIsTheDocumentedTwentySevenBytes()
    {
        // The on-disk contract: uint64 timestamp, uint32 window, uint16 monitor, uint16 x, uint16 y,
        // uint64 hash, uint8 op — little endian. Tools outside this repo depend on it.
        LogEntry entry = LogEntry.Draw(0x0102030405060708, 0x11223344, 0x5566, 0x7788, 0x99AA, 0xBBCCDDEEFF001122);
        byte[] buffer = new byte[LogEntry.Size];
        LogEntry.Write(buffer, entry);

        Assert.Equal(0x08, buffer[0]);
        Assert.Equal(0x01, buffer[7]);
        Assert.Equal(0x44, buffer[8]);
        Assert.Equal(0x11, buffer[11]);
        Assert.Equal(0x66, buffer[12]);
        Assert.Equal(0x88, buffer[14]);
        Assert.Equal(0xAA, buffer[16]);
        Assert.Equal(0x22, buffer[18]);
        Assert.Equal(0xBB, buffer[25]);
        Assert.Equal((byte)LogOp.Draw, buffer[26]);
    }

    [Fact]
    public void WriterAndReaderRoundTripAcrossBufferBoundaries()
    {
        DateOnly day = new(2026, 9, 20);
        string path = SessionLayout.LogPath(_root, day);
        const int count = 5000;

        using (SessionLogWriter writer = new(path, DateTimeOffset.Now))
        {
            for (int i = 0; i < count; i++)
            {
                writer.Append(LogEntry.Draw(1_700_000_000_000_000 + i, (uint)i, 0, i % 64, i / 64, (ulong)i + 1));
            }
        }

        using SessionLogReader reader = new(path);
        int read = 0;
        while (reader.TryReadNext(out LogEntry entry))
        {
            Assert.Equal(1_700_000_000_000_000 + read, entry.TimestampUs);
            Assert.Equal((ulong)read + 1, entry.AssetHash);
            read++;
        }

        Assert.Equal(count, read);
        Assert.Equal(SessionLogFormat.HeaderSize + (count * LogEntry.Size), new FileInfo(path).Length);
    }

    [Fact]
    public void AppendingToAnExistingSegmentKeepsRecordsAligned()
    {
        DateOnly day = new(2026, 9, 20);
        string path = SessionLayout.LogPath(_root, day);

        using (SessionLogWriter first = new(path, DateTimeOffset.Now))
        {
            first.Append(LogEntry.Draw(1, 1, 0, 1, 1, 42));
        }

        using (SessionLogWriter second = new(path, DateTimeOffset.Now))
        {
            Assert.False(second.IsNewFile);
            second.Append(LogEntry.Draw(2, 1, 0, 2, 2, 43));
        }

        using SessionLogReader reader = new(path);
        Assert.True(reader.TryReadNext(out LogEntry a));
        Assert.True(reader.TryReadNext(out LogEntry b));
        Assert.False(reader.TryReadNext(out _));
        Assert.Equal(1, a.TimestampUs);
        Assert.Equal(43UL, b.AssetHash);
    }

    [Fact]
    public void LiveReaderOnlySeesCompleteRecords()
    {
        DateOnly day = new(2026, 9, 20);
        string path = SessionLayout.LogPath(_root, day);

        using SessionLogWriter writer = new(path, DateTimeOffset.Now);
        writer.Append(LogEntry.Draw(10, 1, 0, 0, 0, 1));
        writer.Flush();

        using SessionLogReader reader = new(path);
        Assert.True(reader.TryReadNext(out LogEntry first));
        Assert.Equal(10, first.TimestampUs);
        Assert.False(reader.TryReadNext(out _));

        // A record still sitting in the writer's buffer is not visible yet: a live reader only ever
        // sees whole, flushed records, which is what makes scrubbing today's log safe.
        writer.Append(LogEntry.Draw(20, 1, 0, 1, 0, 2));
        Assert.False(reader.TryReadNext(out _));

        writer.Flush();
        Assert.True(reader.TryReadNext(out LogEntry second));
        Assert.Equal(20, second.TimestampUs);
        Assert.False(reader.TryReadNext(out _));
    }
}
