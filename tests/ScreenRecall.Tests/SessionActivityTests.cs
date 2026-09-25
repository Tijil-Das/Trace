using Xunit;

using ScreenRecall.Player;
using ScreenRecall.Storage;

namespace ScreenRecall.Tests;

/// <summary>
/// Tests for <see cref="SessionActivity"/>: telling a quiet stretch of a day apart from one nobody recorded.
/// </summary>
public class SessionActivityTests : IDisposable
{
    /// <summary>Base instant the synthetic days are built around.</summary>
    private const long T0 = 1_772_000_000_000_000;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "screenrecall-tests", Guid.NewGuid().ToString("n"));
    private readonly DateOnly _day = new(2026, 3, 4);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // A leftover temp directory is not worth failing a run over.
        }

        GC.SuppressFinalize(this);
    }

    /// <summary>Writes one log segment and returns its path.</summary>
    private string Segment(int index, params LogEntry[] entries)
    {
        string path = SessionLayout.LogPath(_root, _day, index);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using SessionLogWriter writer = new(path, DateTimeOffset.Now);
        foreach (LogEntry entry in entries)
        {
            writer.Append(entry);
        }

        return path;
    }

    /// <summary>Heartbeats every 15 seconds for <paramref name="seconds"/>, starting at <paramref name="startUs"/>.</summary>
    private static IEnumerable<LogEntry> Beats(long startUs, long seconds)
    {
        for (long offset = 0; offset <= seconds; offset += 15)
        {
            yield return LogEntry.Heartbeat(startUs + (offset * 1_000_000));
        }
    }

    private static LogEntry Change(long timestampUs) => LogEntry.Draw(timestampUs, 0, 0, 0, 0, 1);

    [Fact]
    public void AnEmptyDayHasNoGaps()
    {
        Assert.Empty(SessionActivity.NotRecordedGaps(_root, _day));
    }

    [Fact]
    public void NoGapsForAContinuousStretchWithHeartbeats()
    {
        Segment(0, Beats(T0, 600).ToArray());

        Assert.Empty(SessionActivity.NotRecordedGaps(_root, _day));
    }

    [Fact]
    public void AQuietHalfHourWithHeartbeatsIsNotAGap()
    {
        // Nothing changed for half an hour, and the recorder said so every 15 seconds: quiet, not missing.
        Segment(0, new[] { Change(T0) }.Concat(Beats(T0 + 15_000_000, 1800)).Append(Change(T0 + 1_815_000_000)).ToArray());

        Assert.Empty(SessionActivity.NotRecordedGaps(_root, _day));
    }

    [Fact]
    public void AHoleInASegmentWithHeartbeatsIsNotRecorded()
    {
        // Twenty minutes of nothing at all, in a segment that otherwise beats every 15 seconds: the recorder was
        // not watching, and the picture would otherwise be a held frame.
        Segment(0, LogEntry.Heartbeat(T0), LogEntry.Heartbeat(T0 + 15_000_000), LogEntry.Heartbeat(T0 + 1_200_000_000), LogEntry.Heartbeat(T0 + 1_215_000_000));

        RecordingGap gap = Assert.Single(SessionActivity.NotRecordedGaps(_root, _day));
        Assert.Equal(T0 + 15_000_000, gap.StartUs);
        Assert.Equal(T0 + 1_200_000_000, gap.EndUs);
        Assert.Equal(1_185_000_000, gap.DurationUs);
        Assert.Contains("watching", gap.Reason);
        Assert.True(SessionActivity.IsNotRecorded(new[] { gap }, T0 + 600_000_000));
        Assert.False(SessionActivity.IsNotRecorded(new[] { gap }, T0 + 1_300_000_000));
    }

    [Fact]
    public void AHoleBetweenSegmentsIsARestart()
    {
        Segment(0, LogEntry.Heartbeat(T0), LogEntry.Heartbeat(T0 + 60_000_000));
        Segment(1, LogEntry.Heartbeat(T0 + 5_400_000_000), LogEntry.Heartbeat(T0 + 5_415_000_000));

        RecordingGap gap = Assert.Single(SessionActivity.NotRecordedGaps(_root, _day));
        Assert.Equal(T0 + 60_000_000, gap.StartUs);
        Assert.Equal(T0 + 5_400_000_000, gap.EndUs);
        Assert.Contains("restart", gap.Reason);
    }

    [Fact]
    public void ShortHolesAreNotReported()
    {
        // A busy desktop can easily log nothing for a minute; that is not a missing recording.
        Segment(0, LogEntry.Heartbeat(T0), LogEntry.Heartbeat(T0 + 100_000_000), LogEntry.Heartbeat(T0 + 200_000_000));

        Assert.Empty(SessionActivity.NotRecordedGaps(_root, _day));
    }

    [Fact]
    public void WithoutHeartbeatsALongHoleIsReportedAsAHeuristic()
    {
        // No heartbeats anywhere: the store predates them, so the only evidence left is the checkpoint cadence.
        Segment(0, Change(T0), Change(T0 + 5_400_000_000));

        RecordingGap gap = Assert.Single(SessionActivity.NotRecordedGaps(_root, _day));
        Assert.Equal(T0, gap.StartUs);
        Assert.Equal(T0 + 5_400_000_000, gap.EndUs);
        Assert.Contains("no activity", gap.Reason);
    }

    [Fact]
    public void WithoutHeartbeatsASevenMinuteHoleIsAlreadyAWarning()
    {
        // Seven minutes with a checkpoint every three: two checkpoints missing, and no heartbeat to say the recorder
        // was still watching. Short enough that a sleeping machine cannot pass for content.
        Segment(0, Change(T0), Change(T0 + 420_000_000));

        RecordingGap gap = Assert.Single(SessionActivity.NotRecordedGaps(_root, _day));
        Assert.Equal(420_000_000, gap.DurationUs);
        Assert.Contains("no activity", gap.Reason);
    }

    [Fact]
    public void WithoutHeartbeatsAShortQuietStretchIsNotAWarning()
    {
        // Four minutes of a still screen is a plausible quiet stretch, not evidence of a missing recorder.
        Segment(0, Change(T0), Change(T0 + 240_000_000));

        Assert.Empty(SessionActivity.NotRecordedGaps(_root, _day));
    }

    [Fact]
    public void ACheckpointInsideALongHoleMeansTheRecorderWasThere()
    {
        Segment(0, Change(T0), Change(T0 + 5_400_000_000));
        Assert.Single(SessionActivity.NotRecordedGaps(_root, _day));

        AddCheckpoint(_root, _day, T0 + 1_800_000_000);

        Assert.Empty(SessionActivity.NotRecordedGaps(_root, _day));
    }

    [Fact]
    public void GapLookupFindsTheStretchAnInstantFallsInto()
    {
        RecordingGap gap = new(100, 200, "test");

        Assert.True(gap.Contains(100));
        Assert.True(gap.Contains(150));
        Assert.True(gap.Contains(200));
        Assert.False(gap.Contains(99));
        Assert.False(gap.Contains(201));
        Assert.Equal(100, gap.DurationUs);
        Assert.Same(gap, SessionActivity.GapAt(new[] { gap }, 150));
        Assert.Null(SessionActivity.GapAt(new[] { gap }, 400));
    }

    /// <summary>
    /// Writes a checkpoint with no monitors: enough to record that the recorder checked in at that instant, which is
    /// all the fallback heuristic looks for.
    /// </summary>
    private static void AddCheckpoint(string root, DateOnly day, long timestampUs)
    {
        SessionStore store = SessionStore.Open(root, day);
        Directory.CreateDirectory(store.CheckpointDir);

        byte[] bytes = new byte[CheckpointFormat.HeaderSize];
        System.Text.Encoding.ASCII.GetBytes("SRCKP1").CopyTo(bytes, 0);
        bytes[6] = CheckpointFormat.Version;
        BitConverter.TryWriteBytes(bytes.AsSpan(8, 8), timestampUs);

        File.WriteAllBytes(
            Path.Combine(store.CheckpointDir, CheckpointFormat.FileNameFor(timestampUs)),
            bytes);
    }
}
