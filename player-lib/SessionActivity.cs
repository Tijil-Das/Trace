using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>
/// A stretch of a day the recorder was not there for. Holding the last frame across it — which is the right thing to
/// do for a quiet stretch, where nothing changed — would be a quiet lie here: the screen went on changing and none
/// of it exists.
/// </summary>
public sealed record RecordingGap(long StartUs, long EndUs, string Reason)
{
    /// <summary>Length of the unrecorded stretch.</summary>
    public long DurationUs => EndUs > StartUs ? EndUs - StartUs : 0;

    /// <summary>True when an instant falls inside the stretch.</summary>
    public bool Contains(long timestampUs) => timestampUs >= StartUs && timestampUs <= EndUs;
}

/// <summary>
/// Tells a quiet stretch of a day apart from a stretch nobody recorded.
/// </summary>
/// <remarks>
/// The log only grows when something changes, so "nothing happened" and "nobody was recording" leave the same trail:
/// a hole. Three kinds of evidence separate them, in order of how certain they are, and each gap says which one it
/// came from:
/// <list type="number">
/// <item><b>A segment boundary.</b> The service opens a new log segment every time it starts, so a hole that spans
/// the end of one segment and the start of the next is a hole in the recording, certain. (A restart is also the usual
/// shape of "the recorder was off for an hour".)</item>
/// <item><b>Heartbeats.</b> A recorder that is watching and has nothing to write says so every 15 seconds
/// (<c>LogOp.Heartbeat</c>), so in a segment that carries heartbeats a hole of any length is a stretch the recorder
/// was not watching — asleep, stopped, locked, or with the source blocked. Certain for any store written since
/// heartbeats existed.</item>
/// <item><b>The checkpoint cadence, as a fallback.</b> Older stores have no heartbeats at all. There, a hole longer
/// than <see cref="FallbackGapMinutes"/> with no checkpoint inside it is reported as not recorded. That is a
/// heuristic and it is labelled as one: a genuinely quiet hour on a machine where nothing moves looks the same, and
/// the two cannot be told apart from the data.</item>
/// </list>
/// A hole shorter than <see cref="MinimumGapUs"/> is never reported: it is far more likely to be a busy desktop that
/// logged nothing for a minute than a recorder that was missing for one.
/// </remarks>
public static class SessionActivity
{
    /// <summary>Shortest hole worth reporting as unrecorded (2 minutes).</summary>
    public const long MinimumGapUs = 2 * 60 * 1_000_000L;

    /// <summary>
    /// Fallback threshold for stores written before heartbeats existed (5 minutes). Lower than a heartbeat-based
    /// decision deserves to be, and deliberately so: without heartbeats a long quiet stretch and a missing recorder
    /// look identical, and of the two possible mistakes — flagging a quiet screen, or letting a held frame pass for
    /// content — the held frame is the worse one, because the reader cannot see through it. The reason text says
    /// which evidence produced the stretch, so a reader can tell the certainty of one from the guess of the other.
    /// </summary>
    public const int FallbackGapMinutes = 5;

    /// <summary>Upper bound on reported gaps, so a pathological day cannot exhaust memory.</summary>
    private const int MaxGaps = 10_000;

    /// <summary>Reason text for a hole that spans two log segments.</summary>
    private const string RestartReason = "recording restarted";

    /// <summary>Reason text for a hole in a segment that carries heartbeats.</summary>
    private const string NotWatchingReason = "recorder was not watching";

    /// <summary>Reason text for a hole inferred from the checkpoint cadence alone.</summary>
    private const string FallbackReason = "no activity for a long stretch";

    /// <summary>
    /// The stretches of a day that were not recorded, oldest first. Cheap enough to call on a day that is still
    /// being written: one sequential pass over each segment's fixed-size records, no tile decoding, no rendering.
    /// </summary>
    /// <param name="root">Store root, as passed to the player.</param>
    /// <param name="day">Day to describe.</param>
    /// <param name="checkpointSeconds">Checkpoint cadence the recorder was configured with, used by the fallback.</param>
    public static IReadOnlyList<RecordingGap> NotRecordedGaps(string root, DateOnly day, int checkpointSeconds = 180)
    {
        SessionStore store = SessionStore.Open(root, day);
        store.ReloadMeta();

        List<SegmentActivity> segments = ReadSegments(store);
        if (segments.Count == 0)
        {
            return Array.Empty<RecordingGap>();
        }

        segments.Sort((left, right) => left.FirstUs.CompareTo(right.FirstUs));
        List<long> checkpoints = CheckpointFormat.List(store.SessionDir).Select(entry => entry.TimestampUs).ToList();
        bool dayHasHeartbeats = segments.Any(segment => segment.Heartbeats > 0);
        long fallbackThresholdUs = Math.Max(
            FallbackGapMinutes * 60L * 1_000_000L,

            // Two checkpoints missing, not four: the fallback only has to be plausible, and every extra checkpoint
            // of margin is another stretch of missing recording that goes unmentioned.
            Math.Max(1, checkpointSeconds) * 2L * 1_000_000L);

        List<RecordingGap> gaps = new();

        // Holes between segments: a restart is proof, not a guess.
        for (int i = 1; i < segments.Count && gaps.Count < MaxGaps; i++)
        {
            AddGap(gaps, segments[i - 1].LastUs, segments[i].FirstUs, RestartReason);
        }

        // Holes inside a segment: heartbeats decide when the store carries them, the checkpoint cadence is the
        // fallback for stores written before they existed.
        foreach (SegmentActivity segment in segments)
        {
            foreach ((long startUs, long endUs) in segment.Holes)
            {
                if (gaps.Count >= MaxGaps)
                {
                    break;
                }

                if (dayHasHeartbeats)
                {
                    AddGap(gaps, startUs, endUs, NotWatchingReason);
                    continue;
                }

                if (endUs - startUs >= fallbackThresholdUs && !HasCheckpointBetween(checkpoints, startUs, endUs))
                {
                    AddGap(gaps, startUs, endUs, FallbackReason);
                }
            }
        }

        gaps.Sort((left, right) => left.StartUs.CompareTo(right.StartUs));
        return gaps;
    }

    /// <summary>True when an instant falls inside one of the stretches.</summary>
    public static bool IsNotRecorded(IReadOnlyList<RecordingGap> gaps, long timestampUs)
        => GapAt(gaps, timestampUs) is not null;

    /// <summary>The stretch an instant falls into, or null when the recorder was there.</summary>
    public static RecordingGap? GapAt(IReadOnlyList<RecordingGap> gaps, long timestampUs)
    {
        foreach (RecordingGap gap in gaps)
        {
            if (gap.Contains(timestampUs))
            {
                return gap;
            }
        }

        return null;
    }

    /// <summary>
    /// One pass over a segment: its bounds, whether it carries heartbeats, and the holes inside it — the stretches
    /// between two consecutive records longer than <see cref="MinimumGapUs"/>.
    /// </summary>
    private static List<SegmentActivity> ReadSegments(SessionStore store)
    {
        List<SegmentActivity> segments = new();
        foreach (string path in store.LogSegments())
        {
            long first = 0;
            long last = 0;
            long previous = 0;
            int heartbeats = 0;
            List<(long StartUs, long EndUs)> holes = new();

            try
            {
                using SessionLogReader reader = new(path);
                while (reader.TryReadNext(out LogEntry entry))
                {
                    if (entry.TimestampUs <= 0)
                    {
                        continue;
                    }

                    if (entry.Op == LogOp.Heartbeat)
                    {
                        heartbeats++;
                    }

                    if (first == 0)
                    {
                        first = entry.TimestampUs;
                    }
                    else if (entry.TimestampUs - previous >= MinimumGapUs)
                    {
                        holes.Add((previous, entry.TimestampUs));
                    }

                    previous = entry.TimestampUs;
                    last = entry.TimestampUs;
                }
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException)
            {
                // A segment that cannot be read is skipped: the rest of the day is still worth describing.
                _ = exception;
            }

            if (first > 0)
            {
                segments.Add(new SegmentActivity(first, last, heartbeats, holes));
            }
        }

        return segments;
    }

    private static void AddGap(List<RecordingGap> gaps, long startUs, long endUs, string reason)
    {
        if (endUs - startUs >= MinimumGapUs)
        {
            gaps.Add(new RecordingGap(startUs, endUs, reason));
        }
    }

    private static bool HasCheckpointBetween(List<long> checkpoints, long startUs, long endUs)
    {
        foreach (long checkpoint in checkpoints)
        {
            if (checkpoint > startUs && checkpoint < endUs)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>What one log segment says about the recorder being present.</summary>
    private sealed record SegmentActivity(long FirstUs, long LastUs, int Heartbeats, List<(long StartUs, long EndUs)> Holes);
}
