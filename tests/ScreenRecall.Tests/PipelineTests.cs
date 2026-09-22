using System.Runtime.CompilerServices;

using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Service;
using ScreenRecall.Player;
using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// End-to-end pipeline test: capture a deterministic synthetic desktop through the real engine, then
/// reconstruct it through the real player and pixel-diff against the ground-truth frames the engine
/// dumped. This is the spec 12 fidelity harness, run in-process and without a display.
/// </summary>
public sealed class PipelineTests : IDisposable
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

    private static RecallConfig TestConfig(string root) => new()
    {
        StoragePath = root,
        TileSize = 64,
        RetentionDays = 2,
        CheckpointSeconds = 10,
        IdlePollMs = 50,
        BurstPollMs = 0,

        // The governor is a rate policy, and these tests assert the pipeline's invariants (what is stored,
        // deduped and logged), not how fast a full-speed synthetic source is allowed to run. A paced source would
        // turn every watermark into a wall-clock race, so they run unpaced.
        MaxPaceMs = 0,
        CaptureGroundTruth = true,
        CaptureAllMonitors = true,
        ExcludedProcesses = new List<string>(),
        ExcludedTitlePatterns = new List<string>(),
    };

    /// <summary>
    /// Runs the engine until the session holds what the test needs, instead of for a fixed number of seconds.
    /// The suite runs test classes in parallel, so a wall-clock window is not a usable oracle: on a loaded
    /// machine a three-second window can expire before the loop has logged anything, which says nothing about
    /// the engine. A product bug still fails here â€” the loop simply never satisfies the condition and the
    /// deadline assertion fires with the real reason attached.
    /// </summary>
    private static void RunEngine(CaptureEngine engine, Func<CaptureStats, bool> enough, TimeSpan deadline)
        => RunEngine(engine, _ => true, enough, deadline);

    /// <summary>
    /// Same, but the run must also pass the <paramref name="started"/> gate before the deadline: a test that needs
    /// sustained progress rather than a one-off early spike asserts both, so a loop that stalls halfway still fails
    /// even though it started.
    /// </summary>
    private static void RunEngine(
        CaptureEngine engine,
        Func<CaptureStats, bool> started,
        Func<CaptureStats, bool> enough,
        TimeSpan deadline)
    {
        using CancellationTokenSource cts = new(deadline);
        Task run = Task.Run(() => engine.Run(cts.Token));
        while (!run.IsCompleted && !cts.IsCancellationRequested && !enough(engine.Stats))
        {
            Thread.Sleep(20);
        }

        cts.Cancel();
        // Generous: shutdown drains queued tiles before flushing the log (never make an entry durable before
        // the tile it points at), and a test that outruns the writer can have a full queue to write out.
        Assert.True(run.Wait(TimeSpan.FromSeconds(45)), "the capture loop did not stop after cancellation");
        Assert.Null(engine.Stats.FatalException);
        Assert.True(
            started(engine.Stats) && enough(engine.Stats),
            $"the capture loop did not produce what the test needs within {deadline.TotalSeconds:0}s "
            + $"(frames={engine.Stats.FramesAcquired}, changed={engine.Stats.FramesWithChanges}, "
            + $"stored={engine.Stats.TilesStored}, deduped={engine.Stats.TilesDeduped}, dumps={engine.Stats.GroundTruthFrames}, "
            + $"log={engine.Stats.LogEntriesWritten}): {engine.Stats.LastError ?? "no error reported"}");
    }

    [Fact]
    public void CapturedSessionReconstructsPixelPerfectly()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        RecallConfig config = TestConfig(_root);
        using SyntheticFrameSource source = new(width: 480, height: 320, frameIntervalMs: 5, tileSize: 64);
        using CaptureEngine engine = new(config, source);

        // Enough ground-truth dumps (roughly one per second) for the fidelity check, and enough changed
        // frames that the reconstruction has content to compare. Dedupe is checked by its own test below,
        // where the source repeats itself deterministically instead of depending on how fast the loop ran.
        RunEngine(
            engine,
            stats => stats.GroundTruthFrames >= 3 && stats.FramesWithChanges >= 10,
            TimeSpan.FromSeconds(45));

        Assert.True(engine.Stats.TilesStored > 0, "the synthetic desktop should have produced stored tiles");

        // The log must be structurally sound: no zero records, no phantom external writer.
        Assert.Equal(0, engine.Stats.LogZeroRecordFaults);
        Assert.False(engine.Stats.LogExternalWriterDetected);

        string dumpDirectory = Path.Combine(Path.GetTempPath(), "screenrecall-tests-dumps");
        FidelityReport report = FidelityVerifier.Verify(_root, day, dumpMismatchesTo: dumpDirectory);
        Assert.True(report.FramesChecked > 0, "ground-truth frames should have been captured");
        Assert.True(report.IsPixelPerfect, $"{report.Describe()} (dump: {dumpDirectory})");
        Assert.True(report.MeetsTarget, report.Describe());

        IntegrityReport integrity = IntegrityVerifier.Verify(_root, day, verifyContent: true);
        Assert.True(integrity.IsHealthy, integrity.Describe());
    }

    [Fact]
    public void UnchangedScreenIsDedupedInsteadOfStoredAgain()
    {
        // The source runs unpaced (MaxPaceMs = 0 in TestConfig) and this test asserts the store's *invariants*, not a
        // ratio against the clock.
        //
        // It used to assert `stored * 2 < hashed`, which was only ever true by accident: the frame count behind that
        // ratio depends on machine load (measured between 108 and 419 frames in the same 30-second window while other
        // test classes ran in parallel), and this workload keeps inventing genuinely new content - the cursor moves
        // 7px per frame, so it lands in fresh tiles - while the first full screen is stored whole. Over runs of
        // 100-800 frames the ratio measured 0.6-0.75 whatever the loop did, so a tighter bound would have been a
        // flaky test, not a stronger one. What is asserted here holds at any frame rate.
        using SyntheticFrameSource source = new(width: 320, height: 240, frameIntervalMs: 1, tileSize: 64);
        using CaptureEngine engine = new(TestConfig(_root), source);

        // The 20 tiles/frame (5x4 grid) each cover 64x64px, so a 24x24 cursor or a 208x24 typing strip only
        // touches 1-4 new tiles: the loop does ~4 hashes/frame, mostly canvas hits that never reach the dedupe
        // counters. 2,000 hashes is ~500 frames (~8s at the unpaced rate the failure line shows: 365 frames in
        // ~30s while the whole suite runs in parallel). 600 is ~150 frames and keeps the same invariants.
        RunEngine(
            engine,
            started: stats => stats.TilesStored > 0,
            enough: stats => stats.TilesHashed >= 600 && stats.TilesDeduped > 0,
            deadline: TimeSpan.FromSeconds(45));

        Assert.True(engine.Stats.TilesStored > 0, "the first frame has to be stored");
        Assert.True(engine.Stats.TilesDeduped > 0, "an unchanged tile must not be stored twice");
        Assert.True(
            engine.Stats.TilesStored < engine.Stats.TilesHashed,
            $"stored {engine.Stats.TilesStored} of {engine.Stats.TilesHashed} hashed tiles: "
            + "hashing must recognise content it has already seen instead of storing it again");

        // The same invariant on disk: the store is content-addressed, so it can never hold more files than there
        // were stores.
        SessionStore store = SessionStore.Open(_root, DateOnly.FromDateTime(DateTime.Now));
        (long count, _) = store.Assets.ComputeStats();
        Assert.True(count > 0, "the store should hold the tiles the log references");
        Assert.True(count <= engine.Stats.TilesStored, $"store holds {count} files for {engine.Stats.TilesStored} stores");
    }

    [Fact]
    public void SeekingBackwardsReproducesTheSameFrameAsForwardReplay()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        using SyntheticFrameSource source = new(width: 320, height: 240, frameIntervalMs: 5, tileSize: 64);
        using CaptureEngine engine = new(TestConfig(_root), source);
        RunEngine(engine, stats => stats.LogEntriesWritten >= 400, TimeSpan.FromSeconds(30));

        using SessionReplayer replayer = new(_root, day);
        Assert.True(replayer.DurationUs > 0, "the session should contain log entries");

        long mid = replayer.FirstTimestampUs + (replayer.DurationUs / 2);

        // Forward: replay from the beginning to mid.
        replayer.SeekTo(replayer.FirstTimestampUs);
        replayer.AdvanceTo(mid);
        RenderedFrame forward = replayer.RenderVirtualDesktop();
        ulong[] forwardTiles = replayer.Canvas.SnapshotTiles(replayer.Canvas.Monitors.First().Id)!;

        // Backwards: seek straight to mid (checkpoint + replay) and compare.
        replayer.SeekTo(replayer.LastTimestampUs);
        replayer.SeekTo(mid);
        RenderedFrame backwards = replayer.RenderVirtualDesktop();
        ulong[] backwardTiles = replayer.Canvas.SnapshotTiles(replayer.Canvas.Monitors.First().Id)!;

        Assert.Equal(forwardTiles, backwardTiles);
        Assert.Equal(forward.Bgra, backwards.Bgra);
    }

    [Fact]
    public void ExcludedWindowIsNotRecorded()
    {
        DateOnly day = DateOnly.FromDateTime(DateTime.Now);
        RecallConfig config = TestConfig(_root);
        using SyntheticFrameSource source = new(width: 320, height: 240, frameIntervalMs: 5, tileSize: 64);
        using CaptureEngine engine = new(config, source);

        engine.Pause();
        using (CancellationTokenSource cts = new(TimeSpan.FromMilliseconds(600)))
        {
            engine.Run(cts.Token);
        }

        SessionStore store = SessionStore.Open(_root, day);
        long entries = store.LogSegments().Sum(segment => SessionLogReader.Scan(segment).EntryCount);
        Assert.Equal(0, entries);
    }
}
