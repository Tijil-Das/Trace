using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Cli;

/// <summary>Parsed command line for the capture service.</summary>
internal sealed class CommandLine
{
    internal bool Console { get; private set; }

    internal bool Synthetic { get; private set; }

    /// <summary>Synthetic desktop size as "WxH" (default 1024x768).</summary>
    internal string? SyntheticSize { get; private set; }

    /// <summary>Interval between synthetic frames, in milliseconds (default 16).</summary>
    internal int SyntheticIntervalMs { get; private set; } = 16;

    internal bool Probe { get; private set; }

    internal bool Bench { get; private set; }

    internal int BenchTiles { get; private set; } = 512;

    internal bool Help { get; private set; }

    internal bool PrintServiceCommands { get; private set; }

    internal int OnceSeconds { get; private set; }

    /// <summary>Minutes to run the long-run soak check for (0 = not requested).</summary>
    internal int SoakMinutes { get; private set; }

    /// <summary>Seconds between soak samples.</summary>
    internal int SoakIntervalSeconds { get; private set; } = 60;

    /// <summary>Minutes per phase for the spec 3 CPU budget bench (0 = not requested).</summary>
    internal double CpuBenchMinutes { get; private set; }

    /// <summary>Scripted workload for the bench's active phase, or "none" to measure manual use.</summary>
    internal string? CpuBenchActivity { get; private set; }

    /// <summary>
    /// Turn on the per-tile phase split (hash/encode/store/log) for a bench run. Off by default because it costs
    /// real CPU (23% of capture-thread samples in the §5a trace); a default run measures what production runs.
    /// </summary>
    internal bool DetailedTiming { get; private set; }

    internal string ConfigPath { get; private set; } = RecallConfig.DefaultConfigPath();

    /// <summary>True when the user named a config file explicitly (so its storage path is honoured).</summary>
    internal bool ConfigExplicit { get; private set; }

    internal string? RootOverride { get; private set; }

    internal string[] Raw { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Why the command line was rejected, or null when it was accepted.
    ///
    /// An unrecognized option used to fall through this parser's switch with no <c>default</c> case, so it was
    /// silently ignored - which is how a run against a store nobody asked for reported plausible-looking numbers,
    /// because <c>--storage &lt;path&gt;</c> was not a real flag (the real one is <c>--root</c>) and nothing said so.
    /// A flag that does nothing is worse than a flag that fails: the run looks like it worked.
    /// </summary>
    internal string? Error { get; private set; }

    private void Reject(string message) => Error ??= message;

    internal static CommandLine Parse(string[] args)
    {
        CommandLine result = new() { Raw = args };

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            switch (arg.ToLowerInvariant())
            {
                case "--console":
                case "-c":
                    result.Console = true;
                    break;
                case "--synthetic":
                case "--synthetic-test":
                    // Test-only, and only ever when asked for by name: the service must never turn a display it
                    // cannot duplicate into a recording of injected frames (spec 5.1 / 13).
                    result.Synthetic = true;
                    break;
                case "--synthetic-size":
                    if (i + 1 < args.Length)
                    {
                        result.SyntheticSize = args[++i];
                    }

                    break;
                case "--synthetic-interval":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int interval))
                    {
                        result.SyntheticIntervalMs = Math.Max(1, interval);
                        i++;
                    }

                    break;
                case "--no-fallback":
                    // Retired: falling back to the synthetic source at all is gone, so there is nothing left to
                    // switch off. Accepted (and ignored) so existing scripts keep working.
                    break;
                case "--probe":
                    result.Probe = true;
                    break;
                case "--bench":
                    result.Bench = true;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int benchTiles))
                    {
                        result.BenchTiles = benchTiles;
                        i++;
                    }

                    break;
                case "--help":
                case "-h":
                case "/?":
                    result.Help = true;
                    break;
                case "--service-commands":
                    result.PrintServiceCommands = true;
                    break;
                case "--once":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int seconds))
                    {
                        result.OnceSeconds = Math.Max(1, seconds);
                    }

                    break;
                case "--soak":
                    result.SoakMinutes = 60;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int soakMinutes))
                    {
                        result.SoakMinutes = Math.Max(1, soakMinutes);
                        i++;
                    }

                    break;
                case "--soak-interval":
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out int soakInterval))
                    {
                        result.SoakIntervalSeconds = Math.Clamp(soakInterval, 5, 3600);
                        i++;
                    }

                    break;
                case "--cpu-bench":
                    result.CpuBenchMinutes = 5;
                    if (i + 1 < args.Length && double.TryParse(args[i + 1], out double benchMinutes))
                    {
                        // Fractional minutes are allowed on purpose: a sub-minute run is how the harness
                        // itself gets smoke-tested without waiting ten minutes for a verdict.
                        result.CpuBenchMinutes = Math.Clamp(benchMinutes, 5.0 / 60.0, 60);
                        i++;
                    }

                    break;
                case "--activity":
                    if (i + 1 < args.Length)
                    {
                        result.CpuBenchActivity = args[++i];
                    }

                    break;
                case "--detailed-timing":
                    result.DetailedTiming = true;
                    break;
                case "--config":
                    if (i + 1 < args.Length)
                    {
                        result.ConfigPath = args[++i];
                        result.ConfigExplicit = true;
                    }

                    break;
                case "--root":
                    if (i + 1 < args.Length)
                    {
                        result.RootOverride = args[++i];
                    }
                    else
                    {
                        result.Reject("--root needs a path");
                    }

                    break;
                default:
                    // An option that is not one is a mistake, and ignoring it silently is how a run against the wrong
                    // store produced plausible-looking numbers: "--storage <path>" is not a flag here (the real one is
                    // --root) and nothing complained, so the service happily recorded into whatever the config said.
                    // A do-nothing flag is worse than a failing one, because the run looks like it worked.
                    // Bare words are deliberately left alone: the host turns those into configuration keys.
                    if (arg.StartsWith('-') || arg.StartsWith('/'))
                    {
                        result.Reject($"unrecognized option '{arg}'");
                    }

                    break;
            }
        }

        return result;
    }

    /// <summary>Loads the configuration for this run, applying command-line overrides.</summary>
    internal RecallConfig LoadConfig(bool perUser)
    {
        string path = ConfigPath;
        RecallConfig config = ConfigStore.Load(path);
        if (RootOverride is not null)
        {
            config.StoragePath = RootOverride;
        }

        if (perUser && RootOverride is null && !File.Exists(path))
        {
            config.StoragePath = RecallConfig.UserStoragePath();
        }

        return config.Normalize();
    }

    internal static string HelpText => """
        Screen Recall capture service

        Usage: ScreenRecall.CaptureService [options]

          (no options)         Run as a Windows service when started by the SCM, otherwise run in
                               the foreground as a console app (dev/diagnostic mode).
          -c, --console        Force console mode.
              --once <sec>     Capture for N seconds, print a summary, then exit (validation runs).
              --soak [min]     Long-run check: sample CPU, memory, handles, dumps, temp files and store
                               size every interval, then print a verdict (default 60 minutes).
              --soak-interval <sec>
                               Sampling interval for --soak (default 60).
              --synthetic-test
                               Use the built-in synthetic desktop instead of DXGI duplication. Test and
                               validation only: it records injected frames, not the screen, and it is never
                               selected automatically. When no display can be duplicated the service goes idle
                               and reports that state instead of recording anything.
              --synthetic      Older name for --synthetic-test.
              --synthetic-size WxH
                               Synthetic desktop size (default 1024x768).
              --synthetic-interval <ms>
                               Interval between synthetic frames (default 16).
              --probe          Enumerate adapters, outputs and monitors, then exit.
              --bench [tiles]  Time tile hashing, QOI encoding, store writes and log appends, then exit.
              --cpu-bench [min]
                               Run the spec 3 CPU budget check: [min] minutes with a static screen plus
                               [min] minutes of active use (default 5 each, fractional allowed), then print
                               a per-metric verdict and one result line. Release builds only.
              --activity <path>
                               Scripted workload window for the cpu-bench active phase; pass "none" to
                               measure manual use instead. Defaults to dev-data\screen-activity.ps1.
              --detailed-timing
                               cpu-bench only: measure the per-tile phase split (hash/encode/store/log).
                               Off by default because it costs real CPU, so a default run measures what
                               production runs. Use it to find out where a frame went, not for a budget.
              --service-commands
                               Print the sc.exe commands that install/remove the service.
              --config <path>  Configuration file to use.
              --root <path>    Storage root override (also usable for throwaway test runs).
          -h, --help           Show this help.
        """;

    /// <summary>sc.exe commands a user with admin rights can run to install the service.</summary>
    internal string ServiceCommands(string executablePath)
        => $"""
            sc.exe create {Service.ServiceIdentity.ServiceName} binPath= "{executablePath}" start= auto DisplayName= "{Service.ServiceIdentity.DisplayName}"
            sc.exe description {Service.ServiceIdentity.ServiceName} "{Service.ServiceIdentity.Description}"
            sc.exe failure {Service.ServiceIdentity.ServiceName} reset= 86400 actions= restart/5000/restart/10000/restart/30000
            sc.exe start {Service.ServiceIdentity.ServiceName}
            """;
}
