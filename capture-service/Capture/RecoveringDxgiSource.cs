using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// The DXGI backend wrapped in a retry policy - the answer to "what happens when the desktop cannot be captured
/// right now?" (spec 5.1 / 13).
///
/// It never invents frames. When no duplication session can be created - a locked workstation, a UAC prompt, a
/// disconnected Remote Desktop session, a second instance already capturing the display, an unduplicatable
/// desktop mode, a driver fault - the source reports that it is blocked and the engine's loop idles on its
/// cadence, which costs essentially nothing. Every few seconds the session is attempted again, backing off to a
/// minute, and capture resumes by itself the moment the desktop is visible again.
///
/// The alternative this class exists to prevent is falling back to the timer-driven synthetic desktop. That would
/// record invented content at 60 frames a second, and it measured 18% of a four-core machine: a lie and the
/// largest single CPU cost in the service, in one move. The synthetic source is now reachable only through an
/// explicit switch, for testing.
/// </summary>
internal sealed class RecoveringDxgiSource : IFrameSource
{
    /// <summary>First retry delay: immediate enough after an unlock, cheap enough to be invisible.</summary>
    internal const int FirstRetryMs = 5_000;

    /// <summary>Backoff ceiling, so a machine left locked overnight is still checked once a minute.</summary>
    internal const int MaxRetryMs = 60_000;

    private readonly int _tileSize;
    private readonly HashSet<ushort>? _filter;

    private DxgiFrameSource? _inner;
    private DxgiStatus.Failure? _failure;
    private int _attempts;
    private long _nextAttemptMs;
    private bool _disposed;

    internal RecoveringDxgiSource(int tileSize, IReadOnlyCollection<ushort>? filter)
    {
        _tileSize = tileSize;
        _filter = filter is { Count: > 0 } ? new HashSet<ushort>(filter) : null;

        // One attempt at startup: on a machine whose desktop is visible, recording starts immediately and the
        // retry machinery never runs at all.
        Attempt();
    }

    public string Name => _inner?.Name ?? "dxgi-desktop-duplication (idle)";

    public IReadOnlyList<MonitorInfo> Monitors => _inner?.Monitors ?? Array.Empty<MonitorInfo>();

    public bool FullFrameRequired
    {
        get => true;
        set
        {
            if (_inner is not null)
            {
                _inner.FullFrameRequired = value;
            }
        }
    }

    public SourceBlock? Block => _inner is null
        ? new SourceBlock(
            _failure?.Reason ?? DxgiStatus.UnavailableReason.NoOutput,
            _failure?.Detail ?? string.Empty,
            RetryInMs)
        : null;

    /// <summary>True while no duplication session exists, so nothing is being recorded.</summary>
    internal bool Unavailable => _inner is null;

    /// <summary>Why the last attempt failed (null while capture works).</summary>
    internal DxgiStatus.Failure? Failure => _failure;

    /// <summary>Build attempts since startup, including the startup attempt.</summary>
    internal int Attempts => _attempts;

    /// <summary>Milliseconds until the next attempt is allowed (0 when one is due now).</summary>
    internal int RetryInMs => _inner is not null ? 0 : (int)Math.Max(0, _nextAttemptMs - NowMs());

    public bool TryAcquire(TimeSpan timeout, out SourceFrame frame)
    {
        frame = null!;
        DxgiFrameSource? inner = _inner;
        if (inner is null)
        {
            // Nothing to wait for, so returning false is exactly right: the engine's cadence turns it into a
            // sleep, which is the point - a service with no capturable output must cost approximately nothing.
            if (NowMs() >= _nextAttemptMs)
            {
                Attempt();
            }

            return false;
        }

        inner.FullFrameRequired = FullFrameRequired;
        try
        {
            return inner.TryAcquire(timeout, out frame);
        }
        catch (DuplicationLostException ex)
        {
            Drop(ex);
            throw;
        }
    }

    public void Release(SourceFrame frame) => _inner?.Release(frame);

    public bool TryRecreate(out string? error)
    {
        // The engine calls this after a lost session or a display change: try now rather than waiting out the
        // backoff, then let the policy space out anything that keeps failing.
        DisposeInner();
        _attempts = 0;
        Attempt();

        error = _inner is null ? _failure?.Detail ?? "no capturable display output was found" : null;
        return _inner is not null;
    }

    public void Dispose()
    {
        _disposed = true;
        DisposeInner();
    }

    /// <summary>Tries to build a session; on failure schedules the next attempt with exponential backoff.</summary>
    private void Attempt()
    {
        if (_disposed)
        {
            return;
        }

        _attempts++;
        DxgiFrameSource candidate = DxgiFrameSource.Create(_tileSize, _filter);
        if (candidate.Monitors.Count > 0)
        {
            // The first frame of a new session is a full rescan: nothing in the canvas can be assumed current.
            candidate.FullFrameRequired = true;
            _inner = candidate;
            _failure = null;
            _nextAttemptMs = 0;
            return;
        }

        _failure = candidate.LastFailure ?? new DxgiStatus.Failure(
            DxgiStatus.UnavailableReason.NoOutput,
            "no capturable display output was found");
        candidate.Dispose();

        // 5s, 10s, 20s, 40s, then a minute.
        int delay = Math.Min(FirstRetryMs * (1 << Math.Min(_attempts - 1, 4)), MaxRetryMs);
        _nextAttemptMs = NowMs() + delay;
    }

    /// <summary>Drops a session whose duplication interface became invalid and schedules a quick retry.</summary>
    private void Drop(DuplicationLostException ex)
    {
        DisposeInner();
        _failure = new DxgiStatus.Failure(
            ex.Reason == DxgiStatus.UnavailableReason.None ? DxgiStatus.UnavailableReason.SessionLost : ex.Reason,
            ex.Message);

        // The usual cause is a desktop switch or a mode change, both over within a second or two, so retrying on
        // the first backoff step - not the last - is the right default here.
        _attempts = 1;
        _nextAttemptMs = NowMs() + FirstRetryMs;
    }

    private void DisposeInner()
    {
        DxgiFrameSource? inner = _inner;
        _inner = null;
        inner?.Dispose();
    }

    private static long NowMs() => Environment.TickCount64;
}
