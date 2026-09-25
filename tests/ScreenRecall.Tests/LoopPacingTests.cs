using ScreenRecall.CaptureService.Capture;
using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// Drives the <b>real</b> capture loop against a source that is always ready and never has changes — the state a
/// static desktop with an animated cursor, a blinking caret or a chatty compositor is in — and bounds how fast the
/// loop may turn.
///
/// This is the test that was missing. Every other cadence test asserts <c>NextDelay()</c>'s arithmetic in isolation,
/// which is exactly why the spinning regression shipped: the governor's policy was covered by tests, but nothing
/// checked that the loop actually slept. A cheap frame path measures zero, <c>NextDelay()</c> answered
/// <see cref="TimeSpan.Zero"/>, <c>Thread.Sleep(0)</c> is not a sleep, and the process sat at 82% of a core on a real
/// desktop and 112.9% on an empty store with nothing at all to record (<c>docs/PERFORMANCE.md</c> §5d).
///
/// The source here never blocks in <c>TryAcquire</c>, which is deliberately the hostile case: while a frame is always
/// ready the acquire timeout cannot pace anything, however large it grows, so if the governor does not pace the loop
/// then nothing does. Before the fix this measured thousands of iterations a second; it is an assertion about the
/// loop rather than about the policy, so it fails on the old code and passes on the new one.
/// </summary>
public sealed class LoopPacingTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "screenrecall-pacing", Guid.NewGuid().ToString("n"));

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
    /// Always ready, never any content. <c>TryAcquire</c> returns a frame on every call without waiting, and the frame
    /// carries no dirty rects, no move rects and no full-rescan flag — so the engine's tile path has nothing to do.
    /// </summary>
    private sealed class AlwaysReadyNoChangeSource : IFrameSource
    {
        private const int Size = 256;

        private readonly MonitorInfo _monitor;
        private readonly byte[] _pixels = new byte[Size * Size * 4];

        internal AlwaysReadyNoChangeSource()
        {
            _monitor = new MonitorInfo(0, "always-ready", 0, 0, Size, Size, TileGrid.DefaultTileSize);
            Monitors = new[] { _monitor };
        }

        public string Name => "always-ready-no-change";

        public IReadOnlyList<MonitorInfo> Monitors { get; }

        public SourceBlock? Block => null;

        public bool FullFrameRequired { get; set; }

        /// <summary>Frames handed out, which is one per loop turn that reached the acquire.</summary>
        internal int Acquisitions;

        public bool TryAcquire(TimeSpan timeout, out SourceFrame frame)
        {
            Interlocked.Increment(ref Acquisitions);
            frame = new SourceFrame(
                _monitor,
                Size,
                Size,
                Size * 4,
                _pixels,
                Array.Empty<IntRect>(),
                Array.Empty<IntRect>(),
                fullRescan: false,
                protectedContent: false,
                timestampUs: DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
            return true;
        }

        public void Release(SourceFrame frame)
        {
        }

        public bool TryRecreate(out string? error)
        {
            error = null;
            return true;
        }

        public void Dispose()
        {
        }
    }

    private RecallConfig Config() => new()
    {
        StoragePath = _root,
        TileSize = TileGrid.DefaultTileSize,
        RetentionDays = 2,
        CheckpointSeconds = 10,
        IdlePollMs = 1000,
        BurstPollMs = 0,

        // The governor is on at its production default. This is the configuration that regressed, so pacing must be
        // enabled here — a test that switched it off would assert nothing at all.
        MaxPaceMs = 250,
        CaptureGroundTruth = false,
        CaptureAllMonitors = true,
        ExcludedProcesses = new List<string>(),
        ExcludedTitlePatterns = new List<string>(),
    };

    [Fact]
    public void AnAlwaysReadySourceWithNoChangesCannotRunTheLoopUnpaced()
    {
        AlwaysReadyNoChangeSource source = new();
        CaptureEngine engine = new(Config(), source);

        TimeSpan window = TimeSpan.FromSeconds(3);
        using CancellationTokenSource cts = new();

        // A safety net rather than the measurement clock: the window is started by hand below, once the loop is
        // demonstrably running, so the token's lifetime must not decide when the run ends.
        cts.CancelAfter(window + TimeSpan.FromSeconds(45));
        Task run = Task.Run(() => engine.Run(cts.Token));

        // The measurement window must not start until the loop is actually running. Creating the token first and
        // asserting on whatever happened during those three seconds measured *thread-pool scheduling latency*, not
        // pacing: under a loaded full-suite run the loop was not started inside the window at all and this test
        // reported "turned 0 times". Wait for the first acquire, then measure the rate over the next window.
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (Volatile.Read(ref source.Acquisitions) == 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(10);
        }

        int baseline = Volatile.Read(ref source.Acquisitions);
        Assert.True(baseline >= 1, "the capture loop never acquired a frame within 30s");
        Thread.Sleep(window);
        cts.Cancel();
        Assert.True(run.Wait(TimeSpan.FromSeconds(30)), "the capture loop did not stop after cancellation");
        Assert.Null(engine.Stats.FatalException);

        int acquisitions = Volatile.Read(ref source.Acquisitions) - baseline;

        // The bug this test exists for: an unbounded spin. Generous slack for a loaded machine and for the
        // governor's own conservatism (the opening full rescan sets a high frame-cost average that nothing lowers
        // while no frame has content, so the pace sits at its 250 ms ceiling) - but nowhere near the thousands of
        // turns a second an unpaced loop manages.
        int ceiling = (int)(window.TotalSeconds * 1000 / AdaptiveCadence.MinPaceMs * 1.5);
        Assert.True(
            acquisitions <= ceiling,
            $"the loop turned {acquisitions} times in {window.TotalSeconds:0}s, over the {ceiling} a paced loop may "
            + $"manage (pace={engine.Stats.PaceIntervalMs:0.0} ms, paced={engine.Stats.FramesPaced}, "
            + $"frames={engine.Stats.FramesAcquired})");

        // Liveness, phrased so machine load cannot decide it. How many turns fit in a fixed window depends on how
        // contended the box is - the first cut of this test asserted "at least 20" and then "at least 5", and the
        // full suite failed it at 4 turns while an isolated run managed 9 - so only load-independent facts are
        // asserted: the loop closed whole turns at all, and it paced them rather than running free.
        Assert.True(
            acquisitions >= 2,
            $"the loop only turned {acquisitions} times in {window.TotalSeconds:0}s: it is not pacing, it is stuck");
        Assert.True(
            engine.Stats.PaceIntervalMs > 0,
            $"the loop ran with a zero pace ({engine.Stats.PaceIntervalMs} ms): nothing is bounding it");

        // The assertion that pins the actual defect: turns whose frame had no changes must still be paced. On the old
        // code the pace was applied only inside the "frame had content" branch, so this counter stayed at 0 while the
        // loop spun - which is what makes this test fail on the old code and pass on the fix.
        Assert.True(
            engine.Stats.FramesPaced >= 1,
            $"the loop turned {acquisitions} times without pacing any of them "
            + $"(paced={engine.Stats.FramesPaced}), so nothing is bounding it");

        // The opening frame is a forced full rescan, so a working engine records exactly one frame with content and
        // then finds nothing to do in every frame that follows.
        Assert.True(engine.Stats.FramesWithChanges >= 1, "the loop never processed a frame at all");
    }
}
