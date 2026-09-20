using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>
/// Seek and replay engine (spec 7). Seeking loads the nearest checkpoint at or before the target time
/// and replays only the log entries after it; playback then walks forward one entry at a time. Nothing
/// is materialized per frame — the canvas is the state, and a frame is a render of it.
/// </summary>
public sealed partial class SessionReplayer : IDisposable
{
    public SessionReplayer(string root, DateOnly day, int tileCacheMegabytes = 256, bool verifyAssets = false)
    {
        Store = SessionStore.Open(root, day);
        Store.ReloadMeta();
        Renderer = new FrameRenderer(Store.Assets, new TileCache(Store.Assets, tileCacheMegabytes * 1024L * 1024L));
        Canvas = new ScreenCanvas();
        VerifyAssets = verifyAssets;

        foreach (string segment in Store.LogSegments())
        {
            _readers.Add(new SessionLogReader(segment));
        }

        _peek = new LogEntry?[_readers.Count];
        _exhausted = new bool[_readers.Count];
        _checkpoints = CheckpointFormat.List(Store.SessionDir).Select(entry => entry.TimestampUs).ToList();

        ApplyGeometry();
        (FirstTimestampUs, LastTimestampUs) = ScanBounds();
        RewindReaders();
    }

    /// <summary>Session being replayed.</summary>
    public SessionStore Store { get; }

    /// <summary>Reconstruction state.</summary>
    public ScreenCanvas Canvas { get; }

    /// <summary>Pixel renderer bound to this session's store.</summary>
    public FrameRenderer Renderer { get; }

    /// <summary>When true, decoded tiles are re-hashed to detect corruption.</summary>
    public bool VerifyAssets { get; }

    /// <summary>First log timestamp of the day (0 when the day is empty).</summary>
    public long FirstTimestampUs { get; }

    /// <summary>Last log timestamp of the day (0 when the day is empty).</summary>
    public long LastTimestampUs { get; }

    /// <summary>Recorded span of the day, in microseconds.</summary>
    public long DurationUs => LastTimestampUs > FirstTimestampUs ? LastTimestampUs - FirstTimestampUs : 0;

    /// <summary>Timestamp of the state currently on the canvas.</summary>
    public long PositionUs { get; private set; }

    /// <summary>Entries replayed into the canvas since the last seek.</summary>
    public long EntriesApplied { get; private set; }

    /// <summary>Timestamp of the checkpoint the canvas started from, when a seek used one.</summary>
    public long? LastCheckpointUs { get; private set; }

    /// <summary>Checkpoint timestamps available for this day.</summary>
    public IReadOnlyList<long> Checkpoints => _checkpoints;

    /// <summary>Timestamp of the next entry that would be applied, or null at end of log.</summary>
    public long? NextEntryTimestampUs => Peek()?.TimestampUs;

    /// <summary>
    /// Reconstructs the screen as it was at <paramref name="timestampUs"/>: nearest checkpoint at or
    /// before it, then forward replay of everything after.
    /// </summary>
    public void SeekTo(long timestampUs)
    {
        long? checkpointUs = FindCheckpointAtOrBefore(timestampUs);

        Canvas.Reset();
        ApplyGeometry();

        long startFrom = 0;
        if (checkpointUs is not null)
        {
            Canvas.Load(LoadCheckpoint(checkpointUs.Value));
            startFrom = checkpointUs.Value;
            LastCheckpointUs = checkpointUs.Value;
        }
        else
        {
            LastCheckpointUs = null;
        }

        RewindReaders();
        EntriesApplied = 0;
        PositionUs = startFrom;
        AdvanceTo(timestampUs);
    }

    /// <summary>
    /// Applies every log entry up to and including <paramref name="timestampUs"/>. Moving backwards is
    /// supported but costs a re-seek, exactly like scrubbing a video back past a keyframe.
    /// </summary>
    public void AdvanceTo(long timestampUs)
    {
        if (timestampUs < PositionUs)
        {
            SeekTo(timestampUs);
            return;
        }

        while (true)
        {
            LogEntry? next = Peek();
            if (next is null || next.Value.TimestampUs > timestampUs)
            {
                break;
            }

            Apply(next.Value);
        }
    }

    /// <summary>Applies exactly one entry — frame-by-frame stepping.</summary>
    public bool StepNext()
    {
        LogEntry? next = Peek();
        if (next is null)
        {
            return false;
        }

        Apply(next.Value);
        return true;
    }

    /// <summary>Advances to the timestamp of the next entry, returning it (null at end of log).</summary>
    public long? StepToNextEvent()
    {
        LogEntry? next = Peek();
        if (next is null)
        {
            return null;
        }

        AdvanceTo(next.Value.TimestampUs);
        return PositionUs;
    }

    private void Apply(in LogEntry entry)
    {
        Canvas.Apply(entry);
        PositionUs = entry.TimestampUs;
        EntriesApplied++;
        Consume();
    }

    /// <summary>Closes the log segments this replayer holds open.</summary>
    public void Dispose()
    {
        foreach (SessionLogReader reader in _readers)
        {
            reader.Dispose();
        }

        _readers.Clear();
    }
}
