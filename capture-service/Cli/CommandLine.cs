using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Cli;

/// <summary>Parsed command line for the capture service.</summary>
internal sealed class CommandLine
{
    internal bool Console { get; private set; }

    internal bool Synthetic { get; private set; }

    internal bool SyntheticFallback { get; private set; }

    internal bool Probe { get; private set; }

    internal bool Bench { get; private set; }

    internal int BenchTiles { get; private set; } = 512;

    internal bool Help { get; private set; }

    internal bool PrintServiceCommands { get; private set; }

    internal int OnceSeconds { get; private set; }

    internal string ConfigPath { get; private set; } = RecallConfig.DefaultConfigPath();

    internal string? RootOverride { get; private set; }

    internal string[] Raw { get; private set; } = Array.Empty<string>();

    internal static CommandLine Parse(string[] args)
    {
        CommandLine result = new() { Raw = args, SyntheticFallback = true };

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
                    result.Synthetic = true;
                    break;
                case "--no-fallback":
                    result.SyntheticFallback = false;
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
                case "--config":
                    if (i + 1 < args.Length)
                    {
                        result.ConfigPath = args[++i];
                    }

                    break;
                case "--root":
                    if (i + 1 < args.Length)
                    {
                        result.RootOverride = args[++i];
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
              --synthetic      Use the built-in synthetic desktop instead of DXGI duplication.
              --no-fallback    Do not silently fall back to the synthetic source when DXGI fails.
              --probe          Enumerate adapters, outputs and monitors, then exit.
              --bench [tiles]  Time tile hashing, QOI encoding, store writes and log appends, then exit.
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
