using ScreenRecall.CaptureService.Capture;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// The cadence is the only thing between "records the desktop" and "pins a core while the desktop animates", so
/// both of its levers are pinned here: the acquire timeout, which only helps a silent screen, and the loop pace,
/// which is what actually throttles a chatty one.
/// </summary>
public sealed class AdaptiveCadenceTests
{
    [Fact]
    public void ASilentScreenBacksTheAcquireTimeoutOff()
    {
        AdaptiveCadence cadence = new(idlePollMs: 1000, burstPollMs: 0);
        Assert.Equal(TimeSpan.Zero, cadence.NextTimeout());

        for (int i = 0; i < 8; i++)
        {
            cadence.OnIdle(TimeSpan.FromMilliseconds(15));
        }

        // The idle backoff doubles from 15 ms and stops at 960 ms: each idle stretches the wait geometrically
        // until it settles just short of the configured one-second poll.
        Assert.Equal(TimeSpan.FromMilliseconds(960), cadence.NextTimeout());
    }

    [Fact]
    public void AChattyDesktopIsPacedByWhatItsFramesCost()
    {
        // The measurement that motivated this: on a live desktop the acquire timeout was 0 ms on every iteration
        // while the self-throttle sat at maximum, so nothing throttled anything. The pace is what does.
        AdaptiveCadence cadence = new(idlePollMs: 1000, burstPollMs: 0);
        cadence.OnFrameProcessed(6);

        double allowanceMsPerSecond = AdaptiveCadence.FrameCpuSharePercent / 100.0 * Environment.ProcessorCount * 1000.0;
        double expected = 1000.0 * 6 / allowanceMsPerSecond;
        double pace = cadence.NextDelay().TotalMilliseconds;

        Assert.True(pace >= expected * 0.9, $"pace {pace:0.0} ms should hold 6 ms frames inside the budget");
        Assert.True(pace <= AdaptiveCadence.MaxPaceMs);

        // A cheaper frame is paced less: the governor follows measured cost, not a fixed frame rate.
        AdaptiveCadence cheap = new(idlePollMs: 1000, burstPollMs: 0);
        cheap.OnFrameProcessed(0.5);
        Assert.True(cheap.NextDelay().TotalMilliseconds < pace);
    }

    [Fact]
    public void ThePaceIsBoundedSoRecordingNeverStopsButCanBeDisabled()
    {
        AdaptiveCadence governed = new(idlePollMs: 1000, burstPollMs: 0);
        governed.OnFrameProcessed(500); // an absurdly expensive frame
        Assert.Equal(AdaptiveCadence.MaxPaceMs, governed.NextDelay().TotalMilliseconds, 1);

        AdaptiveCadence unpaced = new(idlePollMs: 1000, burstPollMs: 0, maxPaceMs: 0);
        unpaced.OnFrameProcessed(500);
        Assert.Equal(TimeSpan.Zero, unpaced.NextDelay());
        Assert.Equal(0, unpaced.PaceIntervalMs);
    }

    [Fact]
    public void AnUntouchedCadenceDoesNotPaceAtAll()
    {
        AdaptiveCadence cadence = new(idlePollMs: 1000, burstPollMs: 0);
        Assert.Equal(TimeSpan.Zero, cadence.NextDelay());
        Assert.Equal(0, cadence.PaceIntervalMs);
    }

    [Fact]
    public void SustainedOverBudgetFramesRaiseTheThrottleAndWidenThePace()
    {
        AdaptiveCadence relaxed = new(idlePollMs: 1000, burstPollMs: 0);
        relaxed.OnFrameProcessed(3);

        AdaptiveCadence throttled = new(idlePollMs: 1000, burstPollMs: 0);
        for (int i = 0; i < 60; i++)
        {
            throttled.OnFrameProcessed(20);
        }

        Assert.Equal(4, throttled.ThrottleLevel);
        Assert.True(throttled.NextDelay() >= relaxed.NextDelay());

        // The throttle must not have leaked into the acquire timeout: widening that did nothing on a desktop that
        // always has a frame ready, which is the bug this replaced.
        throttled.OnIdle(TimeSpan.FromMilliseconds(10));
        Assert.True(throttled.NextTimeout() <= TimeSpan.FromMilliseconds(1000));
    }

    [Fact]
    public void ResetClearsThePaceAndTheThrottle()
    {
        AdaptiveCadence cadence = new(idlePollMs: 1000, burstPollMs: 0);
        for (int i = 0; i < 60; i++)
        {
            cadence.OnFrameProcessed(20);
        }

        cadence.Reset();

        Assert.Equal(0, cadence.ThrottleLevel);
        Assert.Equal(0, cadence.PaceIntervalMs);
        Assert.Equal(TimeSpan.Zero, cadence.NextDelay());
    }
}
