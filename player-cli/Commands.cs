using ScreenRecall.Player;
using ScreenRecall.Storage;

namespace ScreenRecall.PlayerCli;

/// <summary>Browse and inspect commands.</summary>
internal static class Commands
{
    internal static int List(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        IReadOnlyList<DateOnly> days = SessionBrowser.Days(root);
        Console.WriteLine($"{root}: {days.Count} recorded day(s)");

        foreach (DateOnly day in days)
        {
            DaySummary summary = SessionBrowser.Summarize(root, day);
            string span = summary.FirstTimestampUs > 0
                ? $"{ArgParser.FormatTimestamp(summary.FirstTimestampUs)}–{ArgParser.FormatTimestamp(summary.LastTimestampUs)}"
                : "empty";
            Console.WriteLine(
                $"  {day:yyyy-MM-dd}  {span,-25}  log {ArgParser.FormatBytes(summary.LogBytes),-10} "
                + $"store {ArgParser.FormatBytes(summary.SessionBytes),-10} checkpoints {summary.Checkpoints,-4} "
                + $"monitors {summary.Monitors}");
        }

        return 0;
    }

    internal static int Info(string[] args)
    {
        List<string> positional = ArgParser.Positionals(args);
        string root = ArgParser.Required(positional, 0, "root");
        DateOnly day = ArgParser.Day(ArgParser.Required(positional, 1, "day"));

        DaySummary summary = SessionBrowser.Summarize(root, day);
        Console.WriteLine($"day            : {day:yyyy-MM-dd}");
        Console.WriteLine($"log span       : {ArgParser.FormatTimestamp(summary.FirstTimestampUs)} → "
                          + $"{ArgParser.FormatTimestamp(summary.LastTimestampUs)} ({summary.DurationUs / 1_000_000.0:0.0}s)");
        Console.WriteLine($"log bytes      : {ArgParser.FormatBytes(summary.LogBytes)}");
        Console.WriteLine($"session bytes  : {ArgParser.FormatBytes(summary.SessionBytes)}");
        Console.WriteLine($"assets in store: {summary.AssetCount}");
        Console.WriteLine($"checkpoints    : {summary.Checkpoints}");
        Console.WriteLine($"monitors       : {summary.Monitors}");

        SessionStore store = SessionStore.Open(root, day);
        foreach (string segment in store.LogSegments())
        {
            SessionLogStats stats = SessionLogReader.Scan(segment);
            Console.WriteLine(
                $"  segment {Path.GetFileName(segment)}: {stats.EntryCount} entries, "
                + $"{stats.DistinctWindows} window(s), {ArgParser.FormatBytes(stats.FileBytes)}");
        }

        foreach (MonitorInfo monitor in store.Meta.AllMonitors())
        {
            Console.WriteLine(
                $"  monitor #{monitor.Id} {monitor.Width}x{monitor.Height} at ({monitor.X},{monitor.Y}) "
                + $"grid {monitor.Columns}x{monitor.Rows}");
        }

        return 0;
    }
}
