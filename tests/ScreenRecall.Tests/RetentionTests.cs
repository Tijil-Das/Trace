using ScreenRecall.Player;
using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>Retention pruning, asset GC and the panic purge.</summary>
public sealed class RetentionTests : IDisposable
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
    public void RetentionGcKeepsReferencedAssetsAndReclaimsTheRest()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        SessionStore session = SessionStore.Open(_root, day, 64);

        (ulong kept, int w1, int h1, byte[] p1) = AssetStoreTests.MakeTile(11);
        (ulong orphan, int w2, int h2, byte[] p2) = AssetStoreTests.MakeTile(13);
        session.Assets.Store(kept, QoiTileCodec.Instance.Id, w1, h1, QoiTileCodec.Instance.Encode(p1, w1, h1));
        session.Assets.Store(orphan, QoiTileCodec.Instance.Id, w2, h2, QoiTileCodec.Instance.Encode(p2, w2, h2));

        using (SessionLogWriter log = session.OpenLog(DateTimeOffset.Now))
        {
            log.Append(LogEntry.Draw(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000, 1, 0, 0, 0, kept));
        }

        using (AssetManifestWriter manifest = session.OpenManifest())
        {
            manifest.Add(kept);
        }

        // Age the tile data past the grace period so GC may judge it: pack creation time — not buffered
        // .idx clocks — is what the merge walk checks, so both the pack and its index are backdated.
        DateTime old = DateTime.UtcNow.AddHours(-2);
        foreach (string file in Directory.EnumerateFiles(SessionLayout.AssetsRoot(_root), "*.pack", SearchOption.AllDirectories))
        {
            File.SetCreationTimeUtc(file, old);
            File.SetLastWriteTimeUtc(file, old);
        }

        foreach (string file in Directory.EnumerateFiles(SessionLayout.AssetsRoot(_root), "*.idx", SearchOption.AllDirectories))
        {
            File.SetCreationTimeUtc(file, old);
            File.SetLastWriteTimeUtc(file, old);
        }

        foreach (string file in Directory.EnumerateFiles(SessionLayout.AssetsRoot(_root), "*.tile", SearchOption.AllDirectories))
        {
            File.SetLastWriteTimeUtc(file, old);
        }

        (int deleted, _) = new RetentionPruner(_root).CollectGarbage(DateTimeOffset.Now, TimeSpan.FromMinutes(30));

        Assert.Equal(1, deleted);
        // The store accessed through SessionStore was constructed before the collector's deletes committed to disk:
        // its in-memory index still sees the orphan. The disk answers truthfully — the orphan is really gone.
        Assert.True(session.Assets.Contains(orphan, checkDisk: true) == false);
        Assert.True(session.Assets.Contains(kept, checkDisk: true));

        IntegrityReport report = IntegrityVerifier.Verify(_root, day);
        Assert.True(report.IsHealthy, report.Describe());
    }

    [Fact]
    public void GracePeriodProtectsFreshAssetsFromGc()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        SessionStore session = SessionStore.Open(_root, day, 64);
        (ulong fresh, int width, int height, byte[] pixels) = AssetStoreTests.MakeTile(29);
        session.Assets.Store(fresh, QoiTileCodec.Instance.Id, width, height, QoiTileCodec.Instance.Encode(pixels, width, height));

        // A fresh pack is always young (its mtime is "now"), so the walk must skip it without deleting —
        // even though the tile is unreferenced. This is the pack equivalent of the .tile grace rule.
        (int deleted, _) = new RetentionPruner(_root).CollectGarbage(DateTimeOffset.Now, TimeSpan.FromMinutes(30));

        Assert.Equal(0, deleted);
        Assert.True(session.Assets.Contains(fresh, checkDisk: true));
    }

    [Fact]
    public void RetentionPruneDeletesOldDaysAndTheirIndexRows()
    {
        DateOnly old = DateOnly.FromDateTime(DateTime.Now).AddDays(-40);
        DateOnly recent = DateOnly.FromDateTime(DateTime.Now);
        SessionStore oldSession = SessionStore.Open(_root, old, 64);
        SessionStore recentSession = SessionStore.Open(_root, recent, 64);

        using (SessionLogWriter log = oldSession.OpenLog(DateTimeOffset.Now))
        {
            log.Append(LogEntry.Draw(1, 1, 0, 0, 0, 5));
        }

        using (SessionLogWriter log = recentSession.OpenLog(DateTimeOffset.Now))
        {
            log.Append(LogEntry.Draw(2, 1, 0, 0, 0, 6));
        }

        using (RecallIndex index = new(SessionLayout.IndexPath(_root)))
        {
            index.UpsertSession(SessionLayout.DayName(old), 1, 2, oldSession.LogPath);
            index.UpsertSession(SessionLayout.DayName(recent), 1, 2, recentSession.LogPath);
        }

        PruneReport report = new RetentionPruner(_root).Prune(30, DateTimeOffset.Now, collectGarbage: false);

        Assert.Contains(old, report.DeletedDays);
        Assert.False(Directory.Exists(oldSession.SessionDir));
        Assert.True(Directory.Exists(recentSession.SessionDir));

        using RecallIndex reopened = new(SessionLayout.IndexPath(_root));
        Assert.DoesNotContain(SessionLayout.DayName(old), reopened.GetDays());
        Assert.Contains(SessionLayout.DayName(recent), reopened.GetDays());
    }

    [Fact]
    public void PurgeRecentRewritesTheLogWithoutTheRange()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        SessionStore session = SessionStore.Open(_root, day, 64);
        long nowUs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000;

        (ulong old, int w1, int h1, byte[] p1) = AssetStoreTests.MakeTile(21);
        (ulong fresh, int w2, int h2, byte[] p2) = AssetStoreTests.MakeTile(23);
        session.Assets.Store(old, QoiTileCodec.Instance.Id, w1, h1, QoiTileCodec.Instance.Encode(p1, w1, h1));
        session.Assets.Store(fresh, QoiTileCodec.Instance.Id, w2, h2, QoiTileCodec.Instance.Encode(p2, w2, h2));

        using (SessionLogWriter log = session.OpenLog(DateTimeOffset.Now))
        {
            log.Append(LogEntry.Draw(nowUs - (10 * 60 * 1_000_000), 1, 0, 0, 0, old));
            log.Append(LogEntry.Draw(nowUs - 1_000_000, 1, 0, 1, 0, fresh));
        }

        using (AssetManifestWriter manifest = session.OpenManifest())
        {
            manifest.Add(old);
            manifest.Add(fresh);
        }

        new RetentionPruner(_root).PurgeRecent(TimeSpan.FromMinutes(5), DateTimeOffset.Now);

        using SessionLogReader reader = new(session.LogPath);
        LogEntry survivor = Assert.Single(reader.ReadAll().ToList());
        Assert.Equal(old, survivor.AssetHash);
    }
}
