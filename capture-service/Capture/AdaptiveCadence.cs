namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// Adaptive cadence policy (spec 5.1 / 5.8). Three regimes:
/// <list type="bullet">
///   <item>static screen — the acquire timeout backs off towards <c>idlePollMs</c> so the loop sleeps instead of spinning;</item>
///   <item>active use — frames arrive back-to-back, so the loop blocks with a zero/near-zero timeout and reacts immediately;</item>
///   <item>bursts (fast scroll, animation, video) — same tight cadence, and the self-throttle widens it only if recent frame processing time trends up.</item>
/// </list>
/// </summary>
internal sealed class AdaptiveCadence
{
    /// <summary>
    /// Share of the whole machine the frame path is allowed to consume. Spec 3's 2% has to cover everything else
    /// too - the asset writer, GC, housekeeping - so the frames get half of it and the rest is left as headroom.
    /// </summary>
    internal const double FrameCpuSharePercent = 1.0;

    /// <summary>
    /// Longest pause the governor will insert between frames, which is also the floor on the capture rate
    /// (~4 frames/s at the default). It bounds the damage: with a hugely expensive frame the budget can be
    /// exceeded, but a recorder that samples four times a second is still a recorder. Configurable through
    /// <c>RecallConfig.MaxPaceMs</c> (zero disables pacing), which is how the pipeline tests run the loop at its
    /// pre-governor behaviour instead of pacing an artificially chatty source.
    /// </summary>
    internal const int MaxPaceMs = 250;

    /// <summary>
    /// Shortest pause the governor inserts between iterations while pacing is enabled.
    ///
    /// The duty cycle alone cannot bound the loop, because a duty cycle of "<c>t</c> ms of work per iteration" is
    /// only a rate limit while <c>t</c> is non-zero: with a measured cost of zero the interval is zero, and a rate
    /// limit of zero milliseconds per frame permits an unbounded rate. That is not a hypothetical - it is what a
    /// cheap frame path produces by construction, and the packed store (spec 5.3) made the frame path almost free.
    /// The loop then re-acquired frames as fast as the CPU allowed while the screen barely changed: 82% of a core on
    /// a real desktop and 112.9% on an empty store with nothing whatsoever to record.
    ///
    /// 15 ms caps the loop at ~66 iterations a second - above a 60 Hz compositor, so a desktop that is actually
    /// changing is still sampled at its full rate, and low enough that empty frames cannot pin a core.
    /// </summary>
    internal const int MinPaceMs = 15;

    private readonly int _idlePollMs;
    private readonly int _burstPollMs;
    private readonly double _budgetMsPerFrame;

    private double _ewmaFrameMs;
    private double _ewmaIterationMs;
    private double _ewmaIdleMs;
    private double _paceMs;
    private int _consecutiveIdleFrames;
    private int _throttleLevel;
    private readonly int _maxPaceMs;

    internal AdaptiveCadence(int idlePollMs, int burstPollMs, double budgetMsPerFrame = 6.0, int maxPaceMs = MaxPaceMs)
    {
        _idlePollMs = Math.Clamp(idlePollMs, 20, 5000);
        _burstPollMs = Math.Clamp(burstPollMs, 0, 500);
        _budgetMsPerFrame = budgetMsPerFrame;

        // A test config may disable pacing outright: with zero there is nothing to clamp to and no pace to
        // compute, so the loop is governed like it always has been.
        _maxPaceMs = Math.Max(0, maxPaceMs);
    }

    /// <summary>Smoothed per-frame processing cost, in milliseconds.</summary>
    internal double AverageFrameMs => _ewmaFrameMs;

    /// <summary>How many steps the self-throttle has widened the cadence by.</summary>
    internal int ThrottleLevel => _throttleLevel;

    /// <summary>Smoothed time spent idling, in milliseconds.</summary>
    internal double AverageIdleMs => _ewmaIdleMs;

    /// <summary>The pause the loop is currently inserting between frames, in milliseconds (0 when unpaced).</summary>
    internal double PaceIntervalMs => _paceMs;

    /// <summary>
    /// How long the loop should wait after a frame that had changes.
    ///
    /// This is the lever that actually throttles capture. The acquire timeout cannot: while the compositor keeps
    /// presenting - a 60 Hz animation, video, a spinner, a remote session, an animated cursor - AcquireNextFrame
    /// returns a frame immediately, so widening its timeout changes nothing, and the loop runs as fast as it can.
    /// Measured on a live desktop that meant 6,201 frames in 1,779 s with the self-throttle pinned at maximum and
    /// no effect at all.
    ///
    /// The pace is a duty cycle: if an iteration costs <c>t</c> milliseconds of CPU, then running one every
    /// <c>t / share</c> keeps the frame path inside its share of the machine. Cheap iterations sit at the floor,
    /// expensive ones back off until the cost fits, and the self-throttle widens it further when the cost keeps
    /// trending above budget (spec 5.8).
    ///
    /// The cost is measured over the <b>whole iteration</b>, not just <c>ProcessFrame</c>, and the interval is
    /// floored at <see cref="MinPaceMs"/>. Both are load-bearing, and both are scar tissue. The frame cost alone is
    /// not a usable denominator: it can measure zero - the packed store made it almost free - and a duty cycle
    /// against a zero measurement collapses to a zero interval, which is not a pace at all. Measuring the iteration
    /// instead (acquire + process + bookkeeping, which is never free) and flooring the result means a cheap frame
    /// path is paced rather than exempted.
    /// </summary>
    internal TimeSpan NextDelay()
    {
        // The one legitimate way to run unpaced, and it is a configuration decision rather than a measurement
        // outcome: the pipeline tests assert invariants over a full-speed source, so they disable pacing outright.
        if (_maxPaceMs <= 0)
        {
            _paceMs = 0;
            return TimeSpan.Zero;
        }

        int floorMs = Math.Min(MinPaceMs, _maxPaceMs);

        // Nothing measured yet is not the same thing as measured-as-free. Before the first iteration lands, pace at
        // the floor rather than not at all, so a recorder can never open by running unpaced.
        double costMs = Math.Max(_ewmaFrameMs, _ewmaIterationMs);
        if (costMs <= 0)
        {
            _paceMs = floorMs;
            return TimeSpan.FromMilliseconds(_paceMs);
        }

        double allowanceMsPerSecond = FrameCpuSharePercent / 100.0 * Environment.ProcessorCount * 1000.0;
        double intervalMs = 1000.0 * costMs / Math.Max(1.0, allowanceMsPerSecond);
        intervalMs *= 1 + (0.5 * _throttleLevel);

        _paceMs = Math.Clamp(intervalMs, floorMs, _maxPaceMs);
        return TimeSpan.FromMilliseconds(_paceMs);
    }

    /// <summary>Timeout to hand to the next acquire call.</summary>
    internal TimeSpan NextTimeout()
    {
        if (_consecutiveIdleFrames == 0)
        {
            return TimeSpan.FromMilliseconds(_burstPollMs);
        }

        // Back off smoothly the longer nothing happens, never past the configured idle ceiling. The throttle level
        // does not appear here any more: widening this timeout did nothing on a chatty desktop, so the throttle now
        // acts through NextDelay, which is the lever that works (see its remarks).
        double target = Math.Min(_idlePollMs, 15.0 * Math.Pow(2, Math.Min(_consecutiveIdleFrames, 6)));
        return TimeSpan.FromMilliseconds(Math.Clamp(target, _burstPollMs, _idlePollMs));
    }

    /// <summary>Records that the acquire call returned nothing.</summary>
    internal void OnIdle(TimeSpan waitedFor)
    {
        _consecutiveIdleFrames++;
        _ewmaIdleMs = (_ewmaIdleMs * 0.9) + (waitedFor.TotalMilliseconds * 0.1);
    }

    /// <summary>
    /// Records the cost of processing a frame with changes and re-evaluates the self-throttle: if recent frames
    /// trend above the CPU budget, the throttle level rises and <see cref="NextDelay"/> paces the loop harder
    /// instead of letting CPU climb (spec 5.8).
    /// </summary>
    internal void OnFrameProcessed(double frameMs)
    {
        _consecutiveIdleFrames = 0;
        _ewmaFrameMs = _ewmaFrameMs == 0 ? frameMs : (_ewmaFrameMs * 0.8) + (frameMs * 0.2);

        if (_ewmaFrameMs > _budgetMsPerFrame && _throttleLevel < 4)
        {
            _throttleLevel++;
            _ewmaFrameMs *= 0.7; // re-evaluate against the new setting rather than compounding
        }
        else if (_ewmaFrameMs < _budgetMsPerFrame * 0.4 && _throttleLevel > 0)
        {
            _throttleLevel--;
        }
    }

    /// <summary>
    /// Records a frame that was acquired but carried nothing to record.
    ///
    /// This is the third case the cadence has to count, and the one it used to miss entirely.
    /// <see cref="OnIdle"/> runs only when an acquire <i>times out</i> and <see cref="OnFrameProcessed"/> only when
    /// a frame had content, so a desktop that keeps presenting frames with nothing in them - an animated cursor over
    /// a static window, a blinking caret, a chatty compositor - moved neither counter. <c>_consecutiveIdleFrames</c>
    /// stayed at zero, which pinned <see cref="NextTimeout"/> at the burst timeout (0 ms by default), and because a
    /// frame was always ready the loop never blocked on the acquire either. It re-acquired at CPU speed.
    ///
    /// Counting it as idle is the correct reading of the situation: a frame with nothing in it is idle time that
    /// happened to arrive as a frame, and the acquire timeout should back off for it exactly as it does for silence.
    /// </summary>
    internal void OnFrameWithoutChanges() => OnIdle(TimeSpan.Zero);

    /// <summary>
    /// Records the cost of one whole loop turn - acquire, process and bookkeeping - which is the denominator the
    /// duty cycle is measured against. Kept separate from <see cref="OnFrameProcessed"/> so the throttle still judges
    /// frame cost against its budget, while the pace follows a number that cannot be zero.
    /// </summary>
    internal void OnIteration(double iterationMs)
        => _ewmaIterationMs = _ewmaIterationMs == 0 ? iterationMs : (_ewmaIterationMs * 0.8) + (iterationMs * 0.2);

    /// <summary>Smoothed cost of a whole loop iteration, in milliseconds.</summary>
    internal double AverageIterationMs => _ewmaIterationMs;

    /// <summary>Resets smoothing after a session rebuild or a configuration change.</summary>
    internal void Reset()
    {
        _ewmaFrameMs = 0;
        _ewmaIterationMs = 0;
        _ewmaIdleMs = 0;
        _paceMs = 0;
        _consecutiveIdleFrames = 0;
        _throttleLevel = 0;
    }
}
