using System.Diagnostics;

using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>One ground-truth frame and how the reconstruction of the same instant compared to it.</summary>
public sealed record FidelityFrameResult(
    long TimestampUs,
    ushort MonitorId,
    FrameDiffResult Diff,
    int RenderedTiles,
    double RenderMs);

/// <summary>Aggregate fidelity outcome for a day (spec 12's target: &gt; 99.9% pixel match).</summary>
public sealed record FidelityReport(
    DateOnly Day,
    int FramesChecked,
    int FramesExact,
    double PixelMatchRatio,
    double MeanFrameMatchRatio,
    double WorstFrameMatchRatio,
    long WorstTimestampUs,
    double MeanRenderMs,
    int CanvasTiles)
{
    /// <summary>True when every checked frame matched pixel-for-pixel.</summary>
    public bool IsPixelPerfect => FramesChecked > 0 && FramesExact == FramesChecked;

    /// <summary>True when the spec's &gt; 99.9% pixel-match target is met.</summary>
    public bool MeetsTarget => PixelMatchRatio >= 0.999;

    /// <summary>One-line summary for the CLI and the dashboard.</summary>
    public string Describe()
        => $"{FramesChecked} frame(s) checked, {FramesExact} exact, pixel match {PixelMatchRatio * 100:0.0000}% "
           + $"(mean frame {MeanFrameMatchRatio * 100:0.0000}%, worst {WorstFrameMatchRatio * 100:0.0000}%)";
}

/// <summary>
/// Fidelity harness (spec 12). In test-only mode the capture service writes a ground-truth full frame
/// alongside normal operation; this compares each of those against what the player reconstructs for
/// the very same timestamp, so the number reported is the real end-to-end reconstruction fidelity —
/// tile grid, hashing, dedupe, log format and checkpointing all included.
/// </summary>
public static class FidelityVerifier
{
    /// <summary>Runs the comparison for a recorded day.</summary>
    public static FidelityReport Verify(
        string root,
        DateOnly day,
        ushort? monitorFilter = null,
        int maxFrames = 0,
        string? dumpMismatchesTo = null,
        IProgress<string>? progress = null)
    {
        SessionStore store = SessionStore.Open(root, day);
        IReadOnlyList<string> frames = GroundTruthFrame.List(store.SessionDir);
        if (monitorFilter is not null)
        {
            frames = frames.Where(file => file.Contains($"-{monitorFilter.Value}.raw", StringComparison.Ordinal)).ToArray();
        }

        if (maxFrames > 0 && frames.Count > maxFrames)
        {
            frames = frames.Take(maxFrames).ToArray();
        }

        using SessionReplayer replayer = new(root, day);
        int checkedFrames = 0;
        int exactFrames = 0;
        long totalPixels = 0;
        long totalMismatched = 0;
        double matchRatioSum = 0;
        double worstRatio = 1.0;
        long worstTimestampUs = 0;
        double renderMsTotal = 0;

        foreach (string file in frames)
        {
            if (!GroundTruthFrame.TryParseTimestamp(file, out long timestampUs))
            {
                continue;
            }

            GroundTruthImage expected = GroundTruthFrame.Read(file);
            replayer.SeekTo(timestampUs);

            long renderTicks = Stopwatch.GetTimestamp();
            RenderedFrame rendered = replayer.Render(expected.MonitorId);
            double renderMs = Stopwatch.GetElapsedTime(renderTicks).TotalMilliseconds;
            renderMsTotal += renderMs;

            FrameDiffResult diff = FrameDiff.Compare(expected.Bgra, rendered.Bgra, expected.Width, expected.Height);
            checkedFrames++;
            totalPixels += diff.TotalPixels;
            totalMismatched += diff.MismatchedPixels;
            double ratio = diff.MatchRatio;
            matchRatioSum += ratio;
            if (diff.IsExact)
            {
                exactFrames++;
            }

            if (ratio < worstRatio)
            {
                worstRatio = ratio;
                worstTimestampUs = timestampUs;
            }

            if (!diff.IsExact && dumpMismatchesTo is not null)
            {
                Directory.CreateDirectory(dumpMismatchesTo);
                PngWriter.Write(
                    Path.Combine(dumpMismatchesTo, $"mismatch-{timestampUs}-m{expected.MonitorId}.expected.png"),
                    expected.Width,
                    expected.Height,
                    expected.Bgra);
                PngWriter.Write(
                    Path.Combine(dumpMismatchesTo, $"mismatch-{timestampUs}-m{expected.MonitorId}.actual.png"),
                    rendered.Width,
                    rendered.Height,
                    rendered.Bgra);
            }

            progress?.Report(
                $"{DateTimeOffset.UnixEpoch.AddTicks(timestampUs * 10):HH:mm:ss.fff} monitor {expected.MonitorId}: "
                + $"match {ratio * 100:0.0000}% ({diff.MismatchedPixels} px differ, worst delta {diff.MaxChannelDelta}"
                + (diff.DifferenceBounds is { } bounds ? $", bounds {bounds.X},{bounds.Y} {bounds.Width}x{bounds.Height}" : string.Empty)
                + ")");
            if (checkedFrames % 25 == 0)
            {
                replayer.Renderer.Cache.Clear();
            }
        }

        return new FidelityReport(
            day,
            checkedFrames,
            exactFrames,
            totalPixels == 0 ? 1.0 : 1.0 - ((double)totalMismatched / totalPixels),
            checkedFrames == 0 ? 1.0 : matchRatioSum / checkedFrames,
            worstRatio,
            worstTimestampUs,
            checkedFrames == 0 ? 0 : renderMsTotal / checkedFrames,
            replayer.Canvas.NonEmptyTiles);
    }
}
