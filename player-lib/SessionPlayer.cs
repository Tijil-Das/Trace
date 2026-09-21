using System.Diagnostics;

using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>
/// A stretch of a recording with no log entries in it: nothing changed on screen, so reconstruction holds
/// the previous frame for the whole span. This is why recorded time and "video" time are the same thing —
/// quiet minutes cost a still frame, not a gap.
/// </summary>
public sealed record IdleSpan(long StartUs, long EndUs)
{
    /// <summary>Length of the quiet stretch.</summary>
    public long DurationUs => EndUs > StartUs ? EndUs - StartUs : 0;

    /// <summary>True when an instant falls inside the stretch.</summary>
    public bool Contains(long timestampUs) => timestampUs >= StartUs && timestampUs <= EndUs;
}

/// <summary>
/// One recorded stretch ("replay") of a day: a single log segment with its span and weight. The service
/// opens a new segment every time it starts, so a day that spanned a restart holds more than one.
/// </summary>
public sealed record DayReplay(
    string FileName,
    long StartUs,
    long EndUs,
    long EntryCount,
    int DistinctWindows,
    long Bytes)
{
    /// <summary>Recorded span of this stretch.</summary>
    public long DurationUs => EndUs > StartUs ? EndUs - StartUs : 0;
}

/// <summary>What one playback tick did.</summary>
public readonly record struct PlaybackAdvance(bool CanvasChanged, bool ReachedEnd)
{
    /// <summary>Nothing to redraw: paused, or the screen genuinely did not change.</summary>
    public static PlaybackAdvance Idle => new(false, false);
}

/// <summary>
/// Playback of a recorded day as a proper video: a wall-clock playhead moving over the raw reference log,
/// reconstructing each frame live from the stored tiles.
/// </summary>
/// <remarks>
/// Two properties make this behave like a video player rather than a slideshow of seeks, and both are the
/// reason this type exists instead of a host driving <see cref="SessionReplayer"/> directly:
///
/// <list type="number">
/// <item>
/// <b>The position is wall-clock, not log-entry.</b> The playhead advances by elapsed real time times the
/// speed, so playback runs at the rate the user asked for even when the machine is loaded. The host asks for
/// a frame on every display tick; this turns that request into "apply every entry up to here".
/// </item>
/// <item>
/// <b>Forward motion never re-seeks.</b> The naive driver seeks to the target on every tick, which throws the
/// reconstruction away and rebuilds it from a checkpoint each time — the entire cost of a seek, ten or sixty
/// times a second. Here the canvas only ever moves forward, so a tick costs the entries that actually arrived
/// since the last one, and an idle stretch costs nothing at all.
/// </item>
/// </list>
///
/// Nothing is pre-rendered, transcoded or cached as video: every frame is blitted from the same lossless
/// tiles the recorder wrote, so what plays is exactly what was stored.
/// </remarks>
public sealed class SessionPlayer : IDisposable
{
    /// <summary>Default quiet gap that counts as an idle stretch (2 seconds).</summary>
    public const long DefaultIdleGapUs = 2_000_000;

    /// <summary>Upper bound on remembered idle stretches, so a pathological day cannot exhaust memory.</summary>
    private const int MaxIdleSpans = 10_000;

    private readonly SessionReplayer _replayer;
    private readonly Stopwatch _clock = new();
    private long _anchorUs;
    private long _positionUs;
    private double _speed = 1.0;
    private bool _playing;
    private bool _disposed;
    private List<IdleSpan>? _idleSpans;
    private byte[] _surface = Array.Empty<byte>();

    public SessionPlayer(string root, DateOnly day, int tileCacheMegabytes = 256, bool verifyAssets = false)
    {
        Day = day;
        _replayer = new SessionReplayer(root, day, tileCacheMegabytes, verifyAssets);
        if (_replayer.FirstTimestampUs > 0)
        {
            SeekTo(_replayer.FirstTimestampUs);
        }
    }

    /// <summary>Day being played.</summary>
    public DateOnly Day { get; }

    /// <summary>The reconstruction underneath (canvas, checkpoints, window spans).</summary>
    public SessionReplayer Replayer => _replayer;

    /// <summary>First recorded instant of the day.</summary>
    public long FirstTimestampUs => _replayer.FirstTimestampUs;

    /// <summary>Last recorded instant of the day.</summary>
    public long LastTimestampUs => _replayer.LastTimestampUs;

    /// <summary>Recorded span of the day, in microseconds.</summary>
    public long DurationUs => _replayer.DurationUs;

    /// <summary>True when the day holds nothing to play.</summary>
    public bool IsEmpty => _replayer.LastTimestampUs <= _replayer.FirstTimestampUs;

    /// <summary>True while the playhead is running.</summary>
    public bool IsPlaying => _playing;

    /// <summary>Playback rate (1.0 = real time).</summary>
    public double Speed => _speed;

    /// <summary>Playhead position in wall-clock terms — where the video is, not where the last entry was.</summary>
    public long PositionUs => _positionUs;

    /// <summary>Timestamp of the last entry applied to the canvas (diagnostics: how far reconstruction has caught up).</summary>
    public long CanvasPositionUs => _replayer.PositionUs;

    /// <summary>Playhead as a fraction of the day (0..1).</summary>
    public double Progress => DurationUs <= 0
        ? 0d
        : Math.Clamp((double)(_positionUs - FirstTimestampUs) / DurationUs, 0d, 1d);

    /// <summary>Checkpoint timestamps available for this day.</summary>
    public IReadOnlyList<long> Checkpoints => _replayer.Checkpoints;

    /// <summary>
    /// How many times this player has rebuilt the canvas from a checkpoint. Opening a day costs one; a healthy
    /// playback session must not add any, because every one of these on the playback path is a seek's worth of
    /// work inside a frame tick.
    /// </summary>
    public long SeekCount { get; private set; }

    /// <summary>Starts (or resumes) playback. Playing from the end restarts the day, the way a video does.</summary>
    public bool Play()
    {
        ThrowIfDisposed();
        if (IsEmpty)
        {
            return false;
        }

        if (_playing)
        {
            return true;
        }

        if (_positionUs >= LastTimestampUs)
        {
            SeekTo(FirstTimestampUs);
        }

        Anchor(_positionUs);
        _playing = true;
        return true;
    }

    /// <summary>Stops the playhead where it currently is.</summary>
    public void Pause()
    {
        ThrowIfDisposed();
        if (!_playing)
        {
            return;
        }

        _positionUs = Project(_clock.Elapsed);
        _clock.Stop();
        _playing = false;
    }

    /// <summary>Pauses when playing, plays when paused. Returns true when it ends up playing.</summary>
    public bool TogglePlay()
    {
        if (_playing)
        {
            Pause();
            return false;
        }

        return Play();
    }

    /// <summary>
    /// Changes the rate without moving the playhead: the clock is re-anchored at the current position, so
    /// changing speed mid-playback never jumps forward or back.
    /// </summary>
    public void SetSpeed(double speed)
    {
        ThrowIfDisposed();
        double clamped = Math.Clamp(speed, 0.05d, 64d);
        if (Math.Abs(clamped - _speed) < 0.0001d)
        {
            return;
        }

        if (_playing)
        {
            long now = Project(_clock.Elapsed);
            _speed = clamped;
            _positionUs = now;
            Anchor(now);
        }
        else
        {
            _speed = clamped;
        }
    }

    /// <summary>
    /// Jumps the playhead, reconstructing from the nearest checkpoint at or before the target. This is the only
    /// path that pays for a seek, and it is only reached by scrubbing, stepping back or a jump-to-focus.
    /// </summary>
    public void SeekTo(long timestampUs)
    {
        ThrowIfDisposed();
        if (IsEmpty)
        {
            return;
        }

        long target = Math.Clamp(timestampUs, FirstTimestampUs, LastTimestampUs);
        _replayer.SeekTo(target);
        SeekCount++;
        _positionUs = target;
        if (_playing)
        {
            Anchor(target);
        }
    }

    /// <summary>Jumps to a fraction of the day (0..1).</summary>
    public void SeekToProgress(double progress)
        => SeekTo(FirstTimestampUs + (long)(Math.Clamp(progress, 0d, 1d) * DurationUs));

    /// <summary>Advances to the next log entry — frame-by-frame stepping.</summary>
    public bool StepForward()
    {
        ThrowIfDisposed();
        if (IsEmpty)
        {
            return false;
        }

        Pause();
        long? next = _replayer.StepToNextEvent();
        if (next is null)
        {
            return false;
        }

        _positionUs = next.Value;
        return true;
    }

    /// <summary>
    /// Steps back one event. The log is forward-only, so this costs a re-seek to just before the entry that
    /// produced the current frame — the same trade a video makes scrubbing back before a keyframe.
    /// </summary>
    public bool StepBackward()
    {
        ThrowIfDisposed();
        if (IsEmpty || _positionUs <= FirstTimestampUs)
        {
            return false;
        }

        Pause();
        SeekTo(_positionUs - 1);
        return true;
    }

    /// <summary>
    /// Advances the playhead by real elapsed time and applies whatever the log holds up to that point. Call
    /// once per display tick and render only when <see cref="PlaybackAdvance.CanvasChanged"/> is set: that is
    /// what makes a quiet stretch of the day hold one still frame for its full duration at no cost.
    /// </summary>
    public PlaybackAdvance Advance()
    {
        if (_disposed || !_playing)
        {
            return PlaybackAdvance.Idle;
        }

        long last = LastTimestampUs;
        long target = Project(_clock.Elapsed);
        bool reachedEnd = target >= last;
        if (reachedEnd)
        {
            target = last;
        }

        bool changed = false;
        if (target > _positionUs)
        {
            long appliedBefore = _replayer.EntriesApplied;
            _replayer.AdvanceTo(target);
            changed = _replayer.EntriesApplied != appliedBefore;
            _positionUs = target;
        }

        if (reachedEnd)
        {
            _clock.Stop();
            _playing = false;
        }

        return new PlaybackAdvance(changed, reachedEnd);
    }

    /// <summary>
    /// Renders the current frame into a buffer this player owns and reuses, so steady playback allocates
    /// nothing. The returned frame's <c>Bgra</c> may be longer than Width * Height * 4: use Width, Height and
    /// Stride, never <c>Bgra.Length</c>.
    /// </summary>
    public RenderedFrame Render()
    {
        ThrowIfDisposed();
        (int width, int height) = VirtualDesktopSize();
        int needed = width * height * 4;
        if (_surface.Length < needed)
        {
            _surface = new byte[needed];
        }

        return _replayer.Renderer.RenderVirtualDesktopInto(_replayer.Canvas, _surface);
    }

    /// <summary>Pixel size of the whole virtual desktop (every monitor composited).</summary>
    public (int Width, int Height) VirtualDesktopSize()
    {
        List<MonitorInfo> monitors = _replayer.Canvas.Monitors.ToList();
        if (monitors.Count == 0)
        {
            return (1, 1);
        }

        int left = monitors.Min(monitor => monitor.X);
        int top = monitors.Min(monitor => monitor.Y);
        return (
            Math.Max(1, monitors.Max(monitor => monitor.X + monitor.Width) - left),
            Math.Max(1, monitors.Max(monitor => monitor.Y + monitor.Height) - top));
    }

    /// <summary>
    /// Quiet stretches of the day: spans between consecutive entries longer than <paramref name="minGapUs"/>.
    /// These are where a timeline shows "nothing happened" and where a player can offer to skip ahead.
    /// </summary>
    public IReadOnlyList<IdleSpan> IdleSpans(long minGapUs = DefaultIdleGapUs)
    {
        ThrowIfDisposed();
        if (minGapUs == DefaultIdleGapUs && _idleSpans is not null)
        {
            return _idleSpans;
        }

        List<IdleSpan> spans = new();
        long? previous = null;
        foreach (LogEntry entry in _replayer.Store.ReadAllEntries())
        {
            if (previous is { } prior && entry.TimestampUs - prior > minGapUs)
            {
                spans.Add(new IdleSpan(prior, entry.TimestampUs));
                if (spans.Count >= MaxIdleSpans)
                {
                    break;
                }
            }

            previous = entry.TimestampUs;
        }

        if (minGapUs == DefaultIdleGapUs)
        {
            _idleSpans = spans;
        }

        return spans;
    }

    /// <summary>The first quiet stretch ending after an instant, for "skip the quiet parts".</summary>
    public IdleSpan? IdleSpanAfter(long timestampUs)
    {
        foreach (IdleSpan span in IdleSpans())
        {
            if (span.EndUs > timestampUs)
            {
                return span;
            }
        }

        return null;
    }

    /// <summary>Recorded stretches of this day — the day's replay list.</summary>
    public IReadOnlyList<DayReplay> Replays() => Replays(_replayer.Store.Root, Day);

    /// <summary>
    /// The recorded stretches a day holds, one per log segment, oldest first. Reading this costs one pass over
    /// each segment's fixed-size records and decodes no tiles, so it is safe to call while the service is still
    /// recording the day.
    /// </summary>
    public static IReadOnlyList<DayReplay> Replays(string root, DateOnly day)
    {
        SessionStore store = SessionStore.Open(root, day);
        List<DayReplay> replays = new();
        foreach (string segment in store.LogSegments())
        {
            SessionLogStats stats = SessionLogReader.Scan(segment);
            replays.Add(new DayReplay(
                Path.GetFileName(segment),
                stats.FirstTimestampUs,
                stats.LastTimestampUs,
                stats.EntryCount,
                stats.DistinctWindows,
                stats.FileBytes));
        }

        replays.Sort((left, right) => left.StartUs.CompareTo(right.StartUs));
        return replays;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _clock.Stop();
        _replayer.Dispose();
    }

    /// <summary>Where the playhead should be after a given amount of elapsed time.</summary>
    private long Project(TimeSpan elapsed)
    {
        long projected = _anchorUs + (long)(elapsed.TotalSeconds * 1_000_000d * _speed);
        long last = LastTimestampUs;
        if (projected > last)
        {
            return last;
        }

        return projected < FirstTimestampUs ? FirstTimestampUs : projected;
    }

    private void Anchor(long timestampUs)
    {
        _anchorUs = timestampUs;
        _clock.Restart();
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
