using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Ipc;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Service;

/// <summary>Windows service name registered with the SCM (spec 9).</summary>
internal static class ServiceIdentity
{
    internal const string ServiceName = "ScreenRecallCapture";

    internal const string DisplayName = "Screen Recall Capture";

    internal const string Description =
        "Records what is visibly drawn on screen as a content-addressable tile cache plus a timestamped reference log.";
}

/// <summary>
/// Hosts the capture engine and the IPC server for the lifetime of the service (or of a console run),
/// restarting the capture backend if a duplication session is lost (spec 9).
/// </summary>
internal sealed class CaptureWorker : BackgroundService
{
    private readonly ILogger<CaptureWorker> _logger;
    private readonly RecallConfig _config;
    private readonly Cli.CommandLine _commandLine;

    private CaptureController? _controller;
    private CaptureIpcServer? _server;
    private IFrameSource? _source;

    /// <summary>Public because the generic host's container constructs hosted services.</summary>
    public CaptureWorker(ILogger<CaptureWorker> logger, RecallConfig config, Cli.CommandLine commandLine)
    {
        _logger = logger;
        _config = config;
        _commandLine = commandLine;
    }

    /// <summary>Builds the capture backend, honouring the synthetic-source switch.</summary>
    internal static IFrameSource CreateSource(RecallConfig config, Cli.CommandLine commandLine, out string? note)
    {
        note = null;
        if (commandLine.Synthetic)
        {
            return new SyntheticFrameSource(
                width: SyntheticWidth(commandLine),
                height: SyntheticHeight(commandLine),
                frameIntervalMs: commandLine.SyntheticIntervalMs,
                tileSize: config.TileSize);
        }

        DxgiFrameSource dxgi = DxgiFrameSource.Create(
            config.TileSize,
            config.CaptureAllMonitors ? null : config.MonitorIds);

        if (dxgi.Monitors.Count == 0 && commandLine.SyntheticFallback)
        {
            note = $"DXGI duplication unavailable ({string.Join("; ", dxgi.Notes)}); falling back to the synthetic source";
            dxgi.Dispose();
            return new SyntheticFrameSource(1024, 768, commandLine.SyntheticIntervalMs, config.TileSize);
        }

        return dxgi;
    }

    private static int SyntheticWidth(Cli.CommandLine commandLine) => ParseSize(commandLine).Width;

    private static int SyntheticHeight(Cli.CommandLine commandLine) => ParseSize(commandLine).Height;

    private static (int Width, int Height) ParseSize(Cli.CommandLine commandLine)
    {
        if (commandLine.SyntheticSize is { Length: > 0 } size)
        {
            string[] parts = size.Split('x', 'X');
            if (parts.Length == 2 && int.TryParse(parts[0], out int width) && int.TryParse(parts[1], out int height))
            {
                return (Math.Clamp(width, 64, 16384), Math.Clamp(height, 64, 16384));
            }
        }

        return (1024, 768);
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _source = CreateSource(_config, _commandLine, out string? note);
        if (note is not null)
        {
            _logger.LogWarning("{Note}", note);
        }

        _controller = new CaptureController(_commandLine.ConfigPath, _config);
        _controller.Start(_source, out CaptureIpcServer server);
        _server = server;

        _logger.LogInformation(
            "Screen Recall capture running: source={Source} monitors={Monitors} root={Root} pipe={Pipe}",
            _source.Name,
            _source.Monitors.Count,
            _controller.Engine?.StorageRoot ?? _config.StoragePath,
            IpcConstants.PipeName);

        foreach (string problem in _source is DxgiFrameSource { Notes: var notes } ? notes : Array.Empty<string>())
        {
            _logger.LogWarning("{Problem}", problem);
        }

        return MonitorAsync(stoppingToken);
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _server?.Dispose();
        _controller?.Stop();
        _source?.Dispose();
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested && _controller is { ShutdownRequested: false })
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
