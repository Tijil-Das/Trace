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
    private readonly int _idlePollMs;
    private readonly int _burstPollMs;
    private readonly double _budgetMsPerFrame;

    private double _ewmaFrameMs;
    private double _ewmaIdleMs;
    private int _consecutiveIdleFrames;
    private int _throttleLevel;

    internal AdaptiveCadence(int idlePollMs, int burstPollMs, double budgetMsPerFrame = 6.0)
    {
        _idlePollMs = Math.Clamp(idlePollMs, 20, 5000);
        _burstPollMs = Math.Clamp(burstPollMs, 0, 500);
        _budgetMsPerFrame = budgetMsPerFrame;
    }

    /// <summary>Smoothed per-frame processing cost, in milliseconds.</summary>
    internal double AverageFrameMs => _ewmaFrameMs;

    /// <summary>How many steps the self-throttle has widened the cadence by.</summary>
    internal int ThrottleLevel => _throttleLevel;

    /// <summary>Smoothed time spent idling, in milliseconds.</summary>
    internal double AverageIdleMs => _ewmaIdleMs;

    /// <summary>Timeout to hand to the next acquire call.</summary>
    internal TimeSpan NextTimeout()
    {
        if (_consecutiveIdleFrames == 0)
        {
            return TimeSpan.FromMilliseconds(_burstPollMs);
        }

        // Back off smoothly the longer nothing happens, never past the configured idle ceiling.
        double target = Math.Min(_idlePollMs, 15.0 * Math.Pow(2, Math.Min(_consecutiveIdleFrames, 6)));
        target = Math.Min(target * (1 + (0.5 * _throttleLevel)), _idlePollMs);
        return TimeSpan.FromMilliseconds(Math.Clamp(target, _burstPollMs, _idlePollMs));
    }

    /// <summary>Records that the acquire call returned nothing.</summary>
    internal void OnIdle(TimeSpan waitedFor)
    {
        _consecutiveIdleFrames++;
        _ewmaIdleMs = (_ewmaIdleMs * 0.9) + (waitedFor.TotalMilliseconds * 0.1);
    }

    /// <summary>
    /// Records the cost of processing a frame with changes and re-evaluates the self-throttle: if
    /// recent frames trend above the CPU budget, widen the cadence instead of letting CPU climb.
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

    /// <summary>Resets smoothing after a session rebuild or a configuration change.</summary>
    internal void Reset()
    {
        _ewmaFrameMs = 0;
        _ewmaIdleMs = 0;
        _consecutiveIdleFrames = 0;
        _throttleLevel = 0;
    }
}
