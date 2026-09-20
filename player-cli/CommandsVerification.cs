using System.Diagnostics;

using ScreenRecall.Player;

namespace ScreenRecall.PlayerCli;

/// <summary>Verification and performance commands.</summary>
internal static class CommandsVerification
{
    internal static int Fidelity(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        DateOnly day = ArgParser.Day(ArgParser.Required(positional, 1, "day"));
        int max = ArgParser.Int(ArgParser.Option(args, "--max"), 0);
        string? dump = ArgParser.Option(args, "--dump");
        ushort? monitor = ArgParser.Monitor(ArgParser.Option(args, "--monitor"));
        Progress<string>? progress = ArgParser.Flag(args, "--quiet") ? null : new Progress<string>(Console.WriteLine);

        FidelityReport report = FidelityVerifier.Verify(root, day, monitor, max, dump, progress);
        Console.WriteLine();
        Console.WriteLine($"fidelity {day:yyyy-MM-dd}: {report.Describe()}");
        Console.WriteLine($"  canvas tiles : {report.CanvasTiles}");
        Console.WriteLine($"  mean render  : {report.MeanRenderMs:0.0} ms/frame");
        Console.WriteLine($"  worst frame  : {ArgParser.FormatTimestamp(report.WorstTimestampUs)} at {report.WorstFrameMatchRatio * 100:0.0000}%");
        Console.WriteLine($"  spec target  : {(report.MeetsTarget ? "MET" : "NOT MET")} (> 99.9% pixel match)");
        return report.MeetsTarget ? 0 : 2;
    }

    internal static int Verify(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        DateOnly day = ArgParser.Day(ArgParser.Required(positional, 1, "day"));
        bool content = ArgParser.Flag(args, "--content");

        IntegrityReport report = IntegrityVerifier.Verify(root, day, content);
        Console.WriteLine($"integrity {day:yyyy-MM-dd}: {report.Describe()}");
        foreach (string example in report.Examples)
        {
            Console.WriteLine($"  {example}");
        }

        return report.IsHealthy ? 0 : 2;
    }

    internal static int Spans(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        DateOnly day = ArgParser.Day(ArgParser.Required(positional, 1, "day"));

        IReadOnlyList<FocusSpan> spans = SessionBrowser.FocusSpans(root, day);
        Console.WriteLine($"{spans.Count} window(s) focused on {day:yyyy-MM-dd}");
        foreach (FocusSpan span in spans)
        {
            Console.WriteLine(
                $"  {ArgParser.FormatTimestamp(span.StartTs * 1000)} → {ArgParser.FormatTimestamp(span.EndTs * 1000)}  "
                + $"{span.AppName} — {span.WindowTitle} ({span.Count} span(s))");
        }

        return 0;
    }

    internal static int BenchSeek(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        DateOnly day = ArgParser.Day(ArgParser.Required(positional, 1, "day"));
        int iterations = Math.Max(1, ArgParser.Int(ArgParser.Option(args, "--iterations"), 20));

        using SessionReplayer replayer = new(root, day);
        if (replayer.DurationUs <= 0)
        {
            Console.WriteLine("nothing recorded on that day");
            return 1;
        }

        Random random = new(42);
        double totalSeek = 0;
        double totalRender = 0;
        double worst = 0;
        for (int i = 0; i < iterations; i++)
        {
            long target = replayer.FirstTimestampUs + (long)(random.NextDouble() * replayer.DurationUs);
            long ticks = Stopwatch.GetTimestamp();
            replayer.SeekTo(target);
            double seekMs = Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
            ticks = Stopwatch.GetTimestamp();
            _ = replayer.RenderVirtualDesktop();
            double renderMs = Stopwatch.GetElapsedTime(ticks).TotalMilliseconds;
            totalSeek += seekMs;
            totalRender += renderMs;
            worst = Math.Max(worst, seekMs);
            replayer.Renderer.Cache.Clear();
        }

        Console.WriteLine($"{iterations} random seek(s) on {day:yyyy-MM-dd}");
        Console.WriteLine($"  mean seek   : {totalSeek / iterations:0.0} ms (worst {worst:0.0} ms)");
        Console.WriteLine($"  mean render : {totalRender / iterations:0.0} ms (cold cache)");
        Console.WriteLine($"  checkpoints : {replayer.Checkpoints.Count}");
        return 0;
    }
}
