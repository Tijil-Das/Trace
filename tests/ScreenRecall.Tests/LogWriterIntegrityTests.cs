using System.Collections.Concurrent;

using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// Writer integrity guards: a log with holes in it silently truncates playback, so these are the
/// regression tests for the corruption that a concurrent flush used to cause.
/// </summary>
public sealed class LogWriterIntegrityTests : IDisposable
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
    public async Task ConcurrentAppendsAndFlushesNeverWriteZeroRecords()
    {
        // Flush() is reachable from control paths (pause, purge, dispose) while the capture loop
        // appends. Before the writer serialized its state this produced blocks of all-zero records in
        // the middle of the log, which playback read as holes.
        DateOnly day = new(2026, 9, 20);
        string path = SessionLayout.LogPath(_root, day);
        const int perProducer = 20_000;
        const int producers = 4;

        using SessionLogWriter writer = new(path, DateTimeOffset.Now, entryBuffer: 128);
        ConcurrentBag<Exception> failures = new();

        Task flusher = Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < 400; i++)
                {
                    writer.Flush();
                    Thread.Sleep(1);
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        });

        Task[] writers = Enumerable.Range(0, producers).Select(producer => Task.Run(() =>
        {
            try
            {
                for (int i = 0; i < perProducer; i++)
                {
                    writer.Append(LogEntry.Draw(
                        1_700_000_000_000_000 + (producer * perProducer) + i,
                        (uint)producer,
                        0,
                        i % 64,
                        producer,
                        (ulong)i + 1));
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex);
            }
        })).ToArray();

        await Task.WhenAll(writers.Append(flusher));
        writer.Flush();

        Assert.Empty(failures);
        Assert.Equal(0, writer.ZeroRecordFaults);
        Assert.False(writer.ExternalWriterDetected);

        using SessionLogReader reader = new(path);
        int total = 0;
        while (reader.TryReadNext(out LogEntry entry))
        {
            Assert.NotEqual(0, entry.TimestampUs); // a zero record would surface here
            total++;
        }

        Assert.Equal(perProducer * producers, total);
        Assert.Equal(
            SessionLogFormat.HeaderSize + ((long)perProducer * producers * LogEntry.Size),
            new FileInfo(path).Length);
    }

    [Fact]
    public void ZeroRecordsAreDroppedRatherThanPersisted()
    {
        DateOnly day = new(2026, 9, 20);
        string path = SessionLayout.LogPath(_root, day);

        using SessionLogWriter writer = new(path, DateTimeOffset.Now);
        writer.Append(LogEntry.Draw(5, 1, 0, 1, 1, 99));
        writer.Flush();

        // Another writer drops a hole into the file, the way a torn write would. The writer must
        // notice, and the records it owns must stay readable and whole-record aligned.
        using (FileStream stream = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            stream.Write(new byte[LogEntry.Size * 3]);
            stream.Flush();
        }

        writer.Append(LogEntry.Draw(6, 1, 0, 2, 2, 100));
        writer.Flush();

        Assert.True(writer.ExternalWriterDetected);
        Assert.Equal(0, writer.ZeroRecordFaults); // the hole came from outside this writer's buffer
        Assert.Equal(0, new FileInfo(path).Length % LogEntry.Size - (SessionLogFormat.HeaderSize % LogEntry.Size));

        using SessionLogReader reader = new(path);
        List<LogEntry> entries = reader.ReadAll().Where(entry => entry.TimestampUs != 0).ToList();
        Assert.Equal(2, entries.Count);
        Assert.Equal(5, entries[0].TimestampUs);
        Assert.Equal(6, entries[1].TimestampUs);
    }

    [Fact]
    public void ExternalWriterOnTheLogIsDetected()
    {
        DateOnly day = new(2026, 9, 20);
        string path = SessionLayout.LogPath(_root, day);

        using SessionLogWriter writer = new(path, DateTimeOffset.Now);
        writer.Append(LogEntry.Draw(1, 1, 0, 1, 1, 7));
        writer.Flush();
        Assert.False(writer.ExternalWriterDetected);

        // Another handle grows the log behind this writer's back: the next flush must notice, because
        // the file is no longer the length this writer's own appends account for.
        using (FileStream interloper = new(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
        {
            interloper.Write(new byte[LogEntry.Size]);
            interloper.Flush();
        }

        writer.Flush();

        Assert.True(writer.ExternalWriterDetected);
    }

    [Fact]
    public void StoreLockExcludesASecondRecorderOnTheSameRoot()
    {
        Assert.True(StoreLock.TryAcquire(_root, out StoreLock? first, out _));
        Assert.NotNull(first);
        try
        {
            Assert.False(StoreLock.TryAcquire(_root, out StoreLock? second, out string? holder));
            Assert.Null(second);
            Assert.False(string.IsNullOrWhiteSpace(holder));
        }
        finally
        {
            first!.Dispose();
        }

        // Released: the next process may record into the root.
        Assert.True(StoreLock.TryAcquire(_root, out StoreLock? third, out _));
        third!.Dispose();
    }
}
