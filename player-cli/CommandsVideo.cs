using ScreenRecall.Player;

namespace ScreenRecall.PlayerCli;

/// <summary>
/// Video export (spec §7's optional output): a range of a recorded day transcoded into a real video file, with the
/// range, cadence, speed, size, codec and quality all chosen by the caller.
/// </summary>
/// <remarks>
/// This is the one command that needs a tool the project does not ship: ffmpeg does the encoding. Everything else —
/// the range, the reconstruction, the pointer — comes from the same lossless log playback uses, so an export can be
/// reproduced byte-for-byte as long as the same ffmpeg and options are used.
/// </remarks>
internal static class CommandsVideo
{
    internal static int Video(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        DateOnly day = ArgParser.Day(ArgParser.Required(positional, 1, "day"));

        string output = ArgParser.Option(args, "--out") ?? $"screen-recall-{day:yyyy-MM-dd}.mp4";
        string? cursor = ArgParser.Option(args, "--cursor");
        int width = ArgParser.Int(ArgParser.Option(args, "--width"), 0);
        double scale = ArgParser.Double(ArgParser.Option(args, "--scale"), 0);

        VideoExportOptions options = new(output, Resolve(args, root, day, "--from"), Resolve(args, root, day, "--to"))
        {
            Fps = ArgParser.Double(ArgParser.Option(args, "--fps"), 30),
            Speed = ArgParser.Double(ArgParser.Option(args, "--speed"), 1.0),
            Width = width > 0 ? width : null,
            Scale = scale > 0 ? scale : null,
            Codec = ArgParser.Option(args, "--codec") ?? "h264",
            Crf = ArgParser.Int(ArgParser.Option(args, "--crf"), 18),
            Preset = ArgParser.Option(args, "--preset"),
            PixelFormat = ArgParser.Option(args, "--pix-fmt") ?? "yuv420p",
            Tune = ArgParser.Option(args, "--tune"),
            MarkNotRecorded = !IsOff(ArgParser.Option(args, "--gaps")),
            Monitor = ArgParser.Monitor(ArgParser.Option(args, "--monitor")),
            IncludeCursor = cursor is null
                            || (!cursor.Equals("off", StringComparison.OrdinalIgnoreCase)
                                && !cursor.Equals("none", StringComparison.OrdinalIgnoreCase)
                                && !cursor.Equals("false", StringComparison.OrdinalIgnoreCase)),
            FfmpegPath = ArgParser.Option(args, "--ffmpeg"),
            Overwrite = ArgParser.Flag(args, "--overwrite"),
        };

        VideoExportPlan plan = VideoExporter.Plan(root, day, options);
        PrintPlan(plan);

        if (ArgParser.Flag(args, "--dry-run"))
        {
            Console.WriteLine("  dry run: nothing was written.");
            return 0;
        }

        long lastShown = 0;
        VideoExportResult result = VideoExporter.Run(root, day, options, progress =>
        {
            // One line every 25 frames: a replay loop runs far faster than real time, and a line per frame would cost
            // more than the encoding does.
            if (progress.FramesWritten == progress.TotalFrames || progress.FramesWritten - lastShown >= 25)
            {
                lastShown = progress.FramesWritten;
                double percent = 100.0 * progress.FramesWritten / Math.Max(1, progress.TotalFrames);
                Console.WriteLine(
                    $"  {progress.FramesWritten,8}/{progress.TotalFrames}  {percent,5:0.0}%  {ArgParser.FormatTimestamp(progress.PositionUs)}");
            }
        });

        Console.WriteLine($"  wrote  : {ArgParser.FormatBytes(result.OutputBytes)} in {result.Elapsed.TotalSeconds:0.0} s"
                          + $" ({result.AchievedFps:0.0} frames/s)");
        if (result.NotRecordedFrames > 0)
        {
            Console.WriteLine($"  gaps   : {result.NotRecordedFrames} of {result.Frames} frames are stretches nobody"
                              + " recorded, and say so in the video");
        }

        Console.WriteLine($"  done   : {result.OutputPath} — {result.Frames} frames, {result.VideoSeconds:0.0} s of video");
        return 0;
    }

    /// <summary>True for the values a user types to mean "off" on a flag that is on by default.</summary>
    private static bool IsOff(string? value)
        => value is not null
           && (value.Equals("off", StringComparison.OrdinalIgnoreCase)
               || value.Equals("none", StringComparison.OrdinalIgnoreCase)
               || value.Equals("false", StringComparison.OrdinalIgnoreCase)
               || value.Equals("no", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Resolves a range endpoint, or 0 for "the start of the day" / "the end of the day". Relative times need the
    /// day's first timestamp, which costs one pass over the log, so it is only paid when a relative value is used.
    /// </summary>
    private static long Resolve(string[] args, string root, DateOnly day, string name)
    {
        string? text = ArgParser.Option(args, name);
        if (text is null)
        {
            return 0;
        }

        long firstTimestampUs = 0;
        if (text.StartsWith('+'))
        {
            using SessionReplayer replayer = new(root, day);
            firstTimestampUs = replayer.FirstTimestampUs;
        }

        return ArgParser.Timestamp(text, day, firstTimestampUs);
    }

    private static void PrintPlan(in VideoExportPlan plan)
    {
        double recorded = (plan.ToUs - plan.FromUs) / 1_000_000d;
        Console.WriteLine($"video export {plan.Day:yyyy-MM-dd}");
        Console.WriteLine($"  range  : {ArgParser.FormatTimestamp(plan.FromUs)} – {ArgParser.FormatTimestamp(plan.ToUs)}"
                          + $"  ({recorded:0.0} s of recording)");
        Console.WriteLine($"  video  : {plan.FrameCount} frames at {plan.Fps:0.###} fps = {plan.VideoSeconds:0.0} s"
                          + (Math.Abs(plan.Speed - 1) > 0.001 ? $"  ({plan.Speed:0.###}x speed)" : string.Empty));
        Console.WriteLine($"  size   : {plan.SourceWidth}x{plan.SourceHeight} -> {plan.OutputWidth}x{plan.OutputHeight}"
                          + (plan.Monitor is { } monitorId ? $"  (monitor {monitorId})" : "  (virtual desktop)"));
        Console.WriteLine($"  codec  : {plan.Codec} -> {plan.Encoder}, crf {plan.Crf}"
                          + (plan.Preset.Length > 0 ? $", preset {plan.Preset}" : string.Empty));
        Console.WriteLine($"  cursor : {(plan.IncludeCursor ? "included" : "omitted")}");
        Console.WriteLine($"  out    : {plan.OutputPath}");
        Console.WriteLine($"  ffmpeg : {plan.FfmpegPath}");
        Console.WriteLine($"  command: {plan.CommandLine}");
    }
}
