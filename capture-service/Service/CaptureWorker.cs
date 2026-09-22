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
    private readonly IHostApplicationLifetime _lifetime;

    private CaptureController? _controller;
    private CaptureIpcServer? _server;
    private IFrameSource? _source;

    /// <summary>Public because the generic host's container constructs hosted services.</summary>
    public CaptureWorker(
        ILogger<CaptureWorker> logger,
        RecallConfig config,
        Cli.CommandLine commandLine,
        IHostApplicationLifetime lifetime)
    {
        _logger = logger;
        _config = config;
        _commandLine = commandLine;
        _lifetime = lifetime;
    }

    /// <summary>
    /// Builds the capture backend.
    ///
    /// The synthetic desktop is a test backend and is reachable only through an explicit switch: when the real
    /// display cannot be duplicated the service goes idle and says so, because recording invented frames at 60 a
    /// second is both a false recording and - measured - 18% of a four-core machine (spec 5.1 / 13).
    /// </summary>
    internal static IFrameSource CreateSource(RecallConfig config, Cli.CommandLine commandLine, out string? note)
    {
        note = null;
        if (commandLine.Synthetic)
        {
            note = "synthetic desktop requested explicitly: this run records injected frames, not the screen";
            return new SyntheticFrameSource(
                width: SyntheticWidth(commandLine),
                height: SyntheticHeight(commandLine),
                frameIntervalMs: commandLine.SyntheticIntervalMs,
                tileSize: config.TileSize);
        }

        // The retrying DXGI source: a locked session, a disconnected Remote Desktop session or a second instance
        // leaves capture idle with a reported reason, and it resumes on its own when the desktop is visible again.
        return new RecoveringDxgiSource(
            config.TileSize,
            config.CaptureAllMonitors ? null : config.MonitorIds);
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

        // If the desktop is not capturable right now, say so at startup rather than looking like a working
        // recorder that simply has nothing to record (spec 13).
        if (_source.Block is { } block)
        {
            _logger.LogWarning(
                "capture is idle: {Reason}; nothing is being recorded, retrying in {RetrySeconds}s",
                block.Describe(),
                block.RetryInMs / 1000);
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

        // A hosted service returning does not stop the host, and nothing else here stops it either: without this
        // an IPC "shutdown" made the recorder stop recording and then stay alive as an idle process. That is
        // worse than not stopping at all, because the dashboard's Stop button looks like it failed while the
        // recorder quietly holds the store open. Asking the application to stop runs StopAsync, which disposes the
        // IPC server and lets the controller drain and flush before the process exits.
        if (_controller is { ShutdownRequested: true } && !stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("shutdown requested over IPC: stopping");
            _lifetime.StopApplication();
        }
    }
}
