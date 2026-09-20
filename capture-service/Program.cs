using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;

using ScreenRecall.CaptureService.Cli;
using ScreenRecall.CaptureService.Interop;
using ScreenRecall.CaptureService.Service;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService;

/// <summary>Entry point: service host, console host, and the diagnostic verbs.</summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        CommandLine commandLine = CommandLine.Parse(args);
        if (commandLine.Help)
        {
            Console.WriteLine(CommandLine.HelpText);
            return 0;
        }

        bool perUser = !WindowsServiceHelpers.IsWindowsService();
        RecallConfig config = commandLine.LoadConfig(perUser);

        if (commandLine.PrintServiceCommands)
        {
            Console.WriteLine(commandLine.ServiceCommands(Environment.ProcessPath ?? "ScreenRecall.CaptureService.exe"));
            return 0;
        }

        if (commandLine.Probe)
        {
            return ProbeCommand.Run(config, commandLine);
        }

        if (commandLine.Bench)
        {
            return BenchCommand.Run(commandLine.BenchTiles, commandLine.RootOverride);
        }

        // Steady-state governance applied as early as possible (spec 5.8).
        NativeMethods.EnterBackgroundMode();

        if (commandLine.OnceSeconds > 0)
        {
            return OnceCommand.Run(config, commandLine);
        }

        return RunHost(args, config, commandLine);
    }

    private static int RunHost(string[] args, RecallConfig config, CommandLine commandLine)
    {
        HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options =>
        {
            options.SingleLine = true;
            options.TimestampFormat = "HH:mm:ss ";
        });
        builder.Logging.SetMinimumLevel(LogLevel.Information);

        if (WindowsServiceHelpers.IsWindowsService())
        {
            builder.Services.AddWindowsService(options => options.ServiceName = ServiceIdentity.ServiceName);
        }

        builder.Services.AddSingleton(config);
        builder.Services.AddSingleton(commandLine);
        builder.Services.AddHostedService<CaptureWorker>();

        using IHost host = builder.Build();
        try
        {
            host.Run();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"fatal: {ex}");
            return 1;
        }
    }
}
