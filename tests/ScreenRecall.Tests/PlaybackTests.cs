using ScreenRecall.Player;
using ScreenRecall.Storage;

using Xunit;

namespace ScreenRecall.Tests;

/// <summary>
/// Playback-as-video behaviour: a wall-clock playhead that only ever moves forward, holds one still frame across
/// every quiet stretch, and never re-seeks while it plays. These are the properties that decide whether a
/// recorded day feels like a video or like a slideshow of jumps; the reconstruction itself is covered by
/// <see cref="PipelineTests"/>.
/// </summary>
public sealed class PlaybackTests : IDisposable
{
    private static readonly DateTimeOffset BaseTime = new(2026, 9, 21, 10, 0, 0, TimeSpan.Zero);
    private static readonly long BaseUs = BaseTime.ToUnixTimeMilliseconds() * 1000;
    private static readonly DateOnly Day = new(2026, 9, 21);

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

    private static long At(long offsetUs) => BaseUs + offsetUs;

    /// <summary>
    /// A day with a known shape: three events inside the first 200ms, a five-second quiet stretch, then two more.
    /// The quiet stretch is the part a video player has to sit through without doing any work, and the part a
    /// naive driver re-seeks its way through.
    /// </summary>
    private ulong BuildSession()
    {
        SessionStore store = SessionStore.Open(_root, Day, 64);
        MonitorInfo monitor = new(0, "test", 0, 0, 128, 128, 64);
        store.UpdateMonitors(new[] { monitor }, BaseTime);

        (ulong hashA, int widthA, int heightA, byte[] pixelsA) = AssetStoreTests.MakeTile(3);
        (ulong hashB, int widthB, int heightB, byte[] pixelsB) = AssetStoreTests.MakeTile(7);
        store.Assets.Store(hashA, QoiTileCodec.Instance.Id, widthA, heightA, QoiTileCodec.Instance.Encode(pixelsA, widthA, heightA));
        store.Assets.Store(hashB, QoiTileCodec.Instance.Id, widthB, heightB, QoiTileCodec.Instance.Encode(pixelsB, widthB, heightB));

        using (SessionLogWriter writer = store.OpenLog(BaseTime))
        {
            writer.Append(LogEntry.Draw(At(0), 1u, 0, 0, 0, hashA));
            writer.Append(LogEntry.Draw(At(100_000), 1u, 0, 1, 0, hashA));
            writer.Append(LogEntry.Draw(At(200_000), 1u, 0, 0, 1, hashB));
            writer.Append(LogEntry.Draw(At(5_200_000), 1u, 0, 1, 1, hashB));
            writer.Append(LogEntry.Draw(At(5_300_000), 1u, 0, 0, 1, hashA));
        }

        return hashB;
    }

    [Fact]
    public void ForwardPlaybackNeverSeeks()
    {
        BuildSession();
        using SessionPlayer player = new(_root, Day);
        Assert.False(player.IsEmpty);
        long seeksAfterOpen = player.SeekCount;

        Assert.True(player.Play());
        for (int i = 0; i < 12; i++)
        {
            Thread.Sleep(20);
            player.Advance();
        }

        Assert.Equal(seeksAfterOpen, player.SeekCount);
        Assert.True(player.PositionUs > BaseUs, "the playhead should have moved forward");
    }

    [Fact]
    public void QuietStretchHoldsTheFrameAndDoesNoWork()
    {
        BuildSession();
        using SessionPlayer player = new(_root, Day);

        player.SeekTo(At(300_000));                 // inside the quiet stretch
        long seeks = player.SeekCount;
        RenderedFrame before = player.Render();
        byte[] beforePixels = before.Bgra[..(before.Width * before.Height * 4)];

        Assert.True(player.Play());
        Thread.Sleep(60);
        PlaybackAdvance advance = player.Advance();

        Assert.False(advance.CanvasChanged, "no entry arrives during a quiet stretch, so nothing needs redrawing");
        Assert.False(advance.ReachedEnd);
        Assert.True(player.PositionUs > At(300_000), "the playhead still crosses the quiet stretch in real time");
        Assert.Equal(seeks, player.SeekCount);

        RenderedFrame after = player.Render();
        Assert.Equal(before.Width, after.Width);
        Assert.Equal(before.Height, after.Height);
        Assert.Equal(beforePixels, after.Bgra[..(after.Width * after.Height * 4)]);
    }

    [Fact]
    public void IdleStretchesComeFromTheRawLog()
    {
        BuildSession();
        using SessionPlayer player = new(_root, Day);

        IdleSpan span = Assert.Single(player.IdleSpans());
        Assert.Equal(At(200_000), span.StartUs);
        Assert.Equal(At(5_200_000), span.EndUs);
        Assert.Equal(5_000_000, span.DurationUs);
        Assert.True(span.Contains(At(2_000_000)));

        Assert.Equal(At(5_200_000), player.IdleSpanAfter(At(1_000_000))!.EndUs);
        Assert.Null(player.IdleSpanAfter(At(5_300_000)));
    }

    [Fact]
    public void PlaybackRateIsAppliedToWallClockTime()
    {
        BuildSession();
        using SessionPlayer player = new(_root, Day);
        player.SeekTo(At(200_000));
        player.SetSpeed(8.0);
        Assert.Equal(8.0, player.Speed);

        Assert.True(player.Play());
        Thread.Sleep(300);
        player.Advance();

        // Eight times real time over roughly 0.3s of wall clock is well over a second of recorded time, and at
        // real time it could not reach one. The bound is loose on purpose: a loaded machine may hand back more
        // time than was asked for, never less.
        long advanced = player.PositionUs - At(200_000);
        Assert.True(advanced > 1_000_000, $"the playhead advanced only {advanced}us in 300ms at 8x");
    }

    [Fact]
    public void SpeedChangeDoesNotMoveTheFrozenPlayhead()
    {
        BuildSession();
        using SessionPlayer player = new(_root, Day);
        player.SeekTo(At(2_000_000));
        Assert.True(player.Play());
        player.Pause();
        long before = player.PositionUs;

        Thread.Sleep(150);                          // wall clock passes while the playhead is frozen
        player.SetSpeed(4.0);

        Assert.Equal(4.0, player.Speed);
        Assert.InRange(player.PositionUs, before - 5_000, before + 5_000);
    }

    [Fact]
    public void PlayingFromTheEndRestartsTheDay()
    {
        BuildSession();
        using SessionPlayer player = new(_root, Day);
        player.SeekTo(player.LastTimestampUs);
        Assert.True(player.Play());
        Assert.True(player.IsPlaying);
        Assert.Equal(player.FirstTimestampUs, player.PositionUs);
    }

    [Fact]
    public void SteppingMovesOneEventAtATime()
    {
        BuildSession();
        using SessionPlayer player = new(_root, Day);
        player.SeekTo(player.FirstTimestampUs);
        Assert.Equal(At(0), player.PositionUs);
        Assert.Equal(1, player.Replayer.EntriesApplied);

        Assert.True(player.StepForward());
        Assert.Equal(At(100_000), player.PositionUs);
        Assert.Equal(2, player.Replayer.EntriesApplied);

        Assert.True(player.StepBackward());
        Assert.True(player.PositionUs < At(100_000));
        Assert.Equal(1, player.Replayer.EntriesApplied);

        player.SeekTo(player.FirstTimestampUs);
        Assert.False(player.StepBackward(), "nothing precedes the first recorded moment");
    }

    [Fact]
    public void RenderReusesOneBufferAcrossFrames()
    {
        BuildSession();
        using SessionPlayer player = new(_root, Day);

        RenderedFrame first = player.Render();
        RenderedFrame second = player.Render();

        Assert.Equal(128, first.Width);
        Assert.Equal(128, first.Height);
        Assert.Same(first.Bgra, second.Bgra);
    }

    [Fact]
    public void ReplaysListsEveryRecordedStretchOfADay()
    {
        ulong hashB = BuildSession();
        SessionStore store = SessionStore.Open(_root, Day, 64);
        using (SessionLogWriter second = store.OpenLog(BaseTime, 1))
        {
            second.Append(LogEntry.Draw(At(6_000_000), 2u, 0, 0, 0, hashB));
        }

        IReadOnlyList<DayReplay> replays = SessionPlayer.Replays(_root, Day);

        Assert.Equal(2, replays.Count);
        Assert.Equal("log.bin", replays[0].FileName);
        Assert.Equal(5, replays[0].EntryCount);
        Assert.Equal(At(0), replays[0].StartUs);
        Assert.Equal(At(5_300_000), replays[0].EndUs);
        Assert.Equal(5_300_000, replays[0].DurationUs);
        Assert.Equal("log.0001.bin", replays[1].FileName);
        Assert.Equal(1, replays[1].EntryCount);
        Assert.Equal(At(6_000_000), replays[1].StartUs);
    }
}
