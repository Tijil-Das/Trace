using Microsoft.Data.Sqlite;
using ScreenRecall.CaptureService.Capture;
using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// Long-run behaviour: what a recorder that stays up for days has to keep in check. Dumps, temp files,
/// index pages and in-memory hash sets all grow by default, and the size walks that feed status polling
/// are on the hot path twice a second — so each of these has an explicit cap or cache.
/// </summary>
public sealed class LongRunHousekeepingTests : IDisposable
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

    /// <summary>
    /// Reads a pragma from a fresh, unpooled connection, touching a schema object first: a connection
    /// that has not read the file yet can report the pager default instead of the database's setting,
    /// and a pooled connection may be handed over in exactly that state.
    /// </summary>
    private static long Pragma(string dbPath, string pragma)
    {
        using SqliteConnection connection = new($"Data Source={dbPath};Pooling=False");
        connection.Open();
        _ = Scalar(connection, "SELECT count(*) FROM sqlite_master;");
        return Scalar(connection, $"PRAGMA {pragma};");
    }

    private static long Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    [Fact]
    public void GroundTruthSweepDeletesDumpsFromEveryDayFolder()
    {
        DateOnly today = DateOnly.FromDateTime(DateTime.Now);
        DateOnly yesterday = today.AddDays(-1);

        string todayDump = Path.Combine(
            SessionLayout.GroundTruthDir(_root, today),
            GroundTruthFrame.FileNameFor(1_700_000_000_000_000, 0));
        string yesterdayDump = Path.Combine(
            SessionLayout.GroundTruthDir(_root, yesterday),
            GroundTruthFrame.FileNameFor(1_699_000_000_000_000, 0));
        GroundTruthFrame.Write(todayDump, 0, 0, 0, 4, 4, new byte[4 * 4 * 4]);
        GroundTruthFrame.Write(yesterdayDump, 0, 0, 0, 4, 4, new byte[4 * 4 * 4]);

        string logPath = SessionLayout.LogPath(_root, today);
        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        File.WriteAllBytes(logPath, new byte[32]);

        (int deleted, long freed) = GroundTruthStore.ClearAll(_root);

        Assert.Equal(2, deleted);
        Assert.True(freed > 0);
        Assert.False(File.Exists(todayDump));
        Assert.False(File.Exists(yesterdayDump));

        // The dump folders go too, and nothing else in the store is touched.
        Assert.False(Directory.Exists(SessionLayout.GroundTruthDir(_root, today)));
        Assert.False(Directory.Exists(SessionLayout.GroundTruthDir(_root, yesterday)));
        Assert.True(File.Exists(logPath));
    }

    [Fact]
    public void GroundTruthTrimDropsOldestDumpsAndRespectsTheCaps()
    {
        string dir = SessionLayout.GroundTruthDir(_root, DateOnly.FromDateTime(DateTime.Now));
        const int dumps = 12;
        for (int i = 0; i < dumps; i++)
        {
            long timestampUs = 1_700_000_000_000_000 + (i * 1_000_000L);
            GroundTruthFrame.Write(
                Path.Combine(dir, GroundTruthFrame.FileNameFor(timestampUs, 0)),
                0, 0, 0, 32, 32, new byte[32 * 32 * 4]);
        }

        (int deleted, long freed) = GroundTruthStore.Trim(dir, maxFrames: 8, maxBytes: 16 * 1024);

        Assert.True(deleted > 0);
        Assert.True(freed > 0);

        string[] remaining = Directory.GetFiles(dir, "*.raw");
        Assert.True(remaining.Length <= 8, $"kept {remaining.Length} dumps");
        Assert.True(remaining.Sum(file => new FileInfo(file).Length) <= 16 * 1024);

        // The window slides rather than empties: the newest dump is always still there.
        string newest = GroundTruthFrame.FileNameFor(1_700_000_000_000_000 + ((dumps - 1) * 1_000_000L), 0);
        Assert.True(File.Exists(Path.Combine(dir, newest)));
    }

    [Fact]
    public void ManifestStopsTrackingHashesAtItsCapAndStillReadsBackDistinct()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        string dir = SessionLayout.SessionDir(_root, day);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "assets.cap-test.idx");

        ulong[] hashes = { 11, 22, 33, 44, 55, 66, 11, 22, 33 };
        using (AssetManifestWriter writer = new(path, append: false, trackedHashLimit: 4))
        {
            foreach (ulong hash in hashes)
            {
                writer.Add(hash);
            }

            // Past the cap the set is released, so later duplicates are appended instead of remembered.
            Assert.True(writer.TrackingCapped);
            Assert.Equal(4 + 5, writer.HashesAppended);
            Assert.True(writer.DistinctHashes < 4);
        }

        HashSet<ulong> distinct = AssetManifest.ReadDistinctHashes(dir);
        Assert.Equal(6, distinct.Count);
        Assert.Contains(11UL, distinct);
        Assert.Contains(66UL, distinct);
    }

    [Fact]
    public void IndexEnablesIncrementalAutoVacuumForNewAndExistingDatabases()
    {
        string dbPath = SessionLayout.IndexPath(_root);
        Directory.CreateDirectory(_root);

        // A database written before the pragma existed: schema and rows, no auto-vacuum.
        using (SqliteConnection legacy = new($"Data Source={dbPath}"))
        {
            legacy.Open();
            using SqliteCommand command = legacy.CreateCommand();
            command.CommandText =
                "CREATE TABLE legacy (id INTEGER PRIMARY KEY, payload TEXT);"
                + "INSERT INTO legacy (payload) VALUES ('x');";
            command.ExecuteNonQuery();
        }

        using RecallIndex index = new(dbPath);

        Assert.Equal(2, Pragma(dbPath, "auto_vacuum")); // 2 = incremental
        index.UpsertSession("2026-09-20", 10, 20, "log.bin");
        Assert.Equal("2026-09-20", index.GetSession("2026-09-20")!.Day);
    }

    [Fact]
    public void CheckpointingReturnsDeletedPagesToTheFileSystem()
    {
        using RecallIndex index = new(SessionLayout.IndexPath(_root));
        for (int i = 0; i < 500; i++)
        {
            index.InsertCheckpoint("2026-09-20", 1_700_000_000_000 + i, $"/checkpoints/{i}.bin");
        }

        index.DeleteDay("2026-09-20");
        long freeBefore = Pragma(index.DbPath, "freelist_count");

        index.CheckpointWal();

        Assert.Equal(0, Pragma(index.DbPath, "freelist_count"));
        Assert.True(freeBefore > 0, $"expected pages to reclaim, freelist was {freeBefore}");
    }

    [Fact]
    public void EngineRefreshesItsCachedSessionSizeAndAssetStatsOffThread()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        RecallConfig config = new()
        {
            StoragePath = _root,
            TileSize = 64,
            IdlePollMs = 50,
            BurstPollMs = 0,
            ExcludedProcesses = new List<string>(),
            ExcludedTitlePatterns = new List<string>(),
        };

        using SyntheticFrameSource source = new(width: 480, height: 320, frameIntervalMs: 5, tileSize: 64);
        using CaptureEngine engine = new(config, source);

        // Startup value: whatever the session folder holds before anything is captured.
        long initial = engine.SessionBytes();
        (long initialAssets, _) = engine.AssetStats(refreshIntervalMs: 0);

        using (CancellationTokenSource cts = new(TimeSpan.FromSeconds(3)))
        {
            engine.Run(cts.Token);
        }

        long measured = SessionLayout.SessionBytes(_root, day);
        Assert.True(measured > 0, "the run should have written a session log");

        // Compare against the log's real length rather than against another walk: the log is open and
        // being appended to while the engine measures it, and a size taken from the directory listing
        // would report it as it was when it was created.
        long logLength = new FileInfo(SessionLayout.LogPath(_root, day)).Length;
        Assert.True(logLength > 0, "the run should have written log entries");

        // A zero-length refresh interval forces the off-thread walk on the next call; the call itself
        // still returns the previous value, so poll until the background pass lands.
        long reported = initial;
        long assets = initialAssets;
        for (int i = 0; i < 100 && (reported <= initial || assets == 0); i++)
        {
            reported = engine.SessionBytes(refreshIntervalMs: 0);
            (assets, _) = engine.AssetStats(refreshIntervalMs: 0);
            Thread.Sleep(50);
        }

        Assert.True(reported > initial, $"cached session size stayed at its startup value ({initial} bytes)");
        Assert.True(
            reported >= logLength - 4096,
            $"engine reported {reported} bytes for a session whose log alone holds {logLength}");
        Assert.True(assets > 0, "asset stats must refresh too");
    }

    [Fact]
    public void SessionSizeCountsALogThatIsStillOpen()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        SessionStore session = SessionStore.Open(_root, day, 64);

        using SessionLogWriter log = session.OpenLog(DateTimeOffset.Now);
        for (int i = 0; i < 20_000; i++)
        {
            log.Append(LogEntry.Draw(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, 1, 0, 0, 0, (ulong)(i + 1)));
        }

        log.Flush();

        // ~540 KB of entries: past the writer's record buffer and its file buffer, so the log is really
        // on disk while its handle is still open.
        long onDisk = new FileInfo(SessionLayout.LogPath(_root, day)).Length;
        Assert.True(onDisk >= 400_000, $"expected a flushed log well past its buffers, saw {onDisk}");

        // A size read that trusts the directory listing would still be reporting the freshly created log.
        Assert.True(
            SessionLayout.SessionBytes(_root, day) >= onDisk,
            $"session size is smaller than the {onDisk}-byte log it contains");
    }

    [Fact]
    public void AssetStatsAndSessionBytesReportWhatIsOnDisk()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        SessionStore session = SessionStore.Open(_root, day, 64);
        (ulong hash, int width, int height, byte[] pixels) = AssetStoreTests.MakeTile(7);
        session.Assets.Store(hash, QoiTileCodec.Instance.Id, width, height, QoiTileCodec.Instance.Encode(pixels, width, height));

        (long count, long bytes) = session.Assets.ComputeStats();
        Assert.Equal(1, count);
        Assert.Equal(new FileInfo(session.Assets.PathFor(hash)).Length, bytes);

        // The session folder itself holds the log; the assets live under their own root.
        using (SessionLogWriter log = session.OpenLog(DateTimeOffset.Now))
        {
            log.Append(LogEntry.Draw(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, 1, 0, 0, 0, hash));
            log.Flush();
        }

        long sessionBytes = SessionLayout.SessionBytes(_root, day);
        long expected = new DirectoryInfo(SessionLayout.SessionDir(_root, day))
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(file => file.Length);
        Assert.Equal(expected, sessionBytes);
        Assert.True(sessionBytes > 0);
    }
}
