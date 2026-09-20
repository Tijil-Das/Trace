using System.Diagnostics;

using ScreenRecall.Player;

namespace ScreenRecall.PlayerCli;

/// <summary>Frame reconstruction and export commands.</summary>
internal static class CommandsRender
{
    internal static int Render(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        DateOnly day = ArgParser.Day(ArgParser.Required(positional, 1, "day"));
        string output = ArgParser.Option(args, "--out") ?? $"frame-{day:yyyy-MM-dd}.png";
        ushort? monitor = ArgParser.Monitor(ArgParser.Option(args, "--monitor"));
        bool composite = ArgParser.Flag(args, "--composite");

        using SessionReplayer replayer = new(root, day, verifyAssets: ArgParser.Flag(args, "--verify-assets"));
        string? at = ArgParser.Option(args, "--at");
        long timestamp = at is null ? replayer.LastTimestampUs : ArgParser.Timestamp(at, day, replayer.FirstTimestampUs);

        long seekTicks = Stopwatch.GetTimestamp();
        replayer.SeekTo(timestamp);
        double seekMs = Stopwatch.GetElapsedTime(seekTicks).TotalMilliseconds;

        long renderTicks = Stopwatch.GetTimestamp();
        RenderedFrame frame = composite || monitor is null
            ? replayer.RenderVirtualDesktop()
            : replayer.Render(monitor.Value);
        double renderMs = Stopwatch.GetElapsedTime(renderTicks).TotalMilliseconds;

        PngWriter.Write(output, frame.Width, frame.Height, frame.Bgra);
        Console.WriteLine($"render at {ArgParser.FormatTimestamp(timestamp)} → {output}");
        Console.WriteLine($"  size   : {frame.Width}x{frame.Height} at ({frame.X},{frame.Y})");
        Console.WriteLine($"  seek   : {seekMs:0.0} ms (checkpoint {replayer.LastCheckpointUs?.ToString() ?? "none"}, "
                          + $"{replayer.EntriesApplied} entries replayed)");
        Console.WriteLine($"  render : {renderMs:0.0} ms, canvas tiles {replayer.Canvas.NonEmptyTiles}");
        Console.WriteLine($"  cache  : {replayer.Renderer.Cache.Count} tiles, hits {replayer.Renderer.Cache.Hits}, "
                          + $"misses {replayer.Renderer.Cache.Misses}");
        return 0;
    }

    internal static int Export(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        DateOnly day = ArgParser.Day(ArgParser.Required(positional, 1, "day"));
        string outputDir = ArgParser.Option(args, "--out")
                           ?? Path.Combine(Environment.CurrentDirectory, $"{day:yyyy-MM-dd}-export");
        ushort? monitor = ArgParser.Monitor(ArgParser.Option(args, "--monitor"));
        bool composite = ArgParser.Flag(args, "--composite");
        bool quiet = ArgParser.Flag(args, "--quiet");

        using SessionReplayer replayer = new(root, day);
        long from = ArgParser.Timestamp(ArgParser.Option(args, "--from") ?? "+0", day, replayer.FirstTimestampUs);
        long to = ArgParser.Timestamp(ArgParser.Option(args, "--to") ?? "+10", day, replayer.FirstTimestampUs);
        double every = Math.Max(0.05, ArgParser.Double(ArgParser.Option(args, "--every"), 1.0));

        Directory.CreateDirectory(outputDir);
        int written = 0;
        long frameBudgetUs = (long)(every * 1_000_000);

        replayer.SeekTo(from);
        long cursor = replayer.PositionUs;
        while (cursor <= to)
        {
            RenderedFrame frame = composite || monitor is null
                ? replayer.RenderVirtualDesktop()
                : replayer.Render(monitor.Value);

            string path = Path.Combine(outputDir, $"{written:00000}-{ArgParser.FileTimestamp(cursor)}.png");
            PngWriter.Write(path, frame.Width, frame.Height, frame.Bgra);
            written++;
            if (!quiet)
            {
                Console.WriteLine($"  {ArgParser.FormatTimestamp(cursor)} → {Path.GetFileName(path)}");
            }

            cursor += frameBudgetUs;
            replayer.AdvanceTo(cursor);
        }

        long bytes = Directory.EnumerateFiles(outputDir).Sum(file => new FileInfo(file).Length);
        Console.WriteLine($"exported {written} frame(s) to {outputDir} ({ArgParser.FormatBytes(bytes)})");
        return 0;
    }
}
