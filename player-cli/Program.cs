using ScreenRecall.Storage;

namespace ScreenRecall.PlayerCli;

/// <summary>
/// Minimal CLI player (spec §16 build order step 3): reconstruct frames, export them, and run the
/// fidelity and integrity harnesses. Everything the dashboard does visually, this does by hand —
/// which is exactly what makes it useful for validating the pipeline end to end.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return args.Length == 0 ? 1 : 0;
        }

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "list" => Commands.List(args),
                "info" => Commands.Info(args),
                "render" => CommandsRender.Render(args),
                "export" => CommandsRender.Export(args),
                "fidelity" => CommandsVerification.Fidelity(args),
                "verify" => CommandsVerification.Verify(args),
                "spans" => CommandsVerification.Spans(args),
                "bench-seek" => CommandsVerification.BenchSeek(args),
                "diagnose" => DiagnoseCommand.Run(args),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                      or IOException or InvalidDataException or FileNotFoundException)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static bool IsHelp(string value) => value is "-h" or "--help" or "help" or "/?";

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"unknown command '{command}'");
        PrintHelp();
        return 1;
    }

    private static void PrintHelp() => Console.WriteLine("""
        Screen Recall — player CLI

        Usage:
          list   <root>                                    List recorded days
          info   <root> <day>                              Summarize one day
          render <root> <day> [--at <time>] [options]       Reconstruct a frame as PNG
          export <root> <day> [--from <t>] [--to <t>] [--every <sec>] [--out <dir>]
                                                           Export a PNG sequence for a time range
          fidelity <root> <day> [--max N] [--dump <dir>]   Pixel-diff reconstructions vs ground truth
          verify <root> <day> [--content]                  Check referenced tiles exist / are intact
          spans  <root> <day>                              List focus spans (jump-to-focus navigation)
          bench-seek <root> <day> [--iterations N]         Time random seeks (checkpoint + replay)

        Options:
          --monitor <id>     Restrict render/export/fidelity to one monitor
          --composite        Render all monitors into one virtual-desktop frame
          --verify-assets    Re-hash tiles while decoding (slow, catches corruption)
          --quiet            Suppress per-frame progress output

        Time formats for --at/--from/--to:
          HH:mm:ss.fff       Local time on the chosen day
          HH:mm:ss           Local time on the chosen day
          +<seconds>         Seconds after the day's first recorded moment
          <epoch-ms>         Raw unix milliseconds
        """);
}
