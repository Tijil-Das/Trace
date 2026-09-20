using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Ipc;

/// <summary>
/// Local named-pipe server (spec 9). The dashboard sends config changes, pause/resume and purge
/// commands over this pipe and reads live stats back; it never opens the log or asset files while the
/// service is writing them. One JSON request line in, one JSON response line out, per connection.
/// </summary>
internal sealed partial class CaptureIpcServer : IDisposable
{
    private readonly ICaptureControl _control;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = new();
    private Task? _acceptLoop;
    private bool _disposed;

    internal CaptureIpcServer(ICaptureControl control, string? pipeName = null)
    {
        _control = control;
        _pipeName = pipeName ?? IpcConstants.PipeName;
    }

    /// <summary>Requests handled since start.</summary>
    internal long RequestsHandled { get; private set; }

    internal string? LastError { get; private set; }

    /// <summary>Starts accepting connections and returns immediately.</summary>
    internal void Start() => _acceptLoop = Task.Run(AcceptLoopAsync);

    /// <summary>Handles a single request; also used in-process by tests and the CLI.</summary>
    internal IpcResponse Handle(IpcRequest request)
    {
        try
        {
            switch (request.Command)
            {
                case IpcConstants.Commands.Ping:
                    return new IpcResponse { Id = request.Id, Ok = true };
                case IpcConstants.Commands.Status:
                    return new IpcResponse { Id = request.Id, Ok = true, Status = _control.GetStatus() };
                case IpcConstants.Commands.Pause:
                    return Run(request.Id, _control.Pause);
                case IpcConstants.Commands.Resume:
                    return Run(request.Id, _control.Resume);
                case IpcConstants.Commands.TogglePause:
                    return Run(request.Id, _control.TogglePause);
                case IpcConstants.Commands.GetConfig:
                    return new IpcResponse { Id = request.Id, Ok = true, Config = _control.GetConfig() };
                case IpcConstants.Commands.SetConfig:
                    return SetConfig(request);
                case IpcConstants.Commands.PurgeRecent:
                    return Purge(request);
                case IpcConstants.Commands.Prune:
                    return new IpcResponse { Id = request.Id, Ok = true, Report = _control.Prune() };
                case IpcConstants.Commands.Days:
                    return new IpcResponse { Id = request.Id, Ok = true, Days = _control.GetDays() };
                case IpcConstants.Commands.Windows:
                    return new IpcResponse
                    {
                        Id = request.Id,
                        Ok = true,
                        Windows = _control.GetWindows(
                            request.Day ?? SessionLayout.DayName(DateOnly.FromDateTime(DateTime.Now))),
                    };
                case IpcConstants.Commands.Probe:
                    return new IpcResponse { Id = request.Id, Ok = true, Probe = _control.Probe(request.Deep ?? false) };
                case IpcConstants.Commands.Flush:
                    return Run(request.Id, _control.Flush);
                case IpcConstants.Commands.Shutdown:
                    return Run(request.Id, _control.RequestShutdown);
                default:
                    return IpcResponse.Failure(request.Id, $"unknown command '{request.Command}'");
            }
        }
        catch (Exception ex)
        {
            return IpcResponse.Failure(request.Id, ex.Message);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _cts.Cancel();
        try
        {
            Task[] pending = new[] { _acceptLoop ?? Task.CompletedTask }.Concat(_connections).ToArray();
            Task.WaitAll(pending, 1500);
        }
        catch (AggregateException)
        {
        }

        _cts.Dispose();
    }

    private IpcResponse SetConfig(IpcRequest request)
    {
        if (request.Config is null)
        {
            return IpcResponse.Failure(request.Id, "config.set requires a config body");
        }

        _control.SetConfig(request.Config.Normalize());
        return new IpcResponse
        {
            Id = request.Id,
            Ok = true,
            Config = _control.GetConfig(),
            Status = _control.GetStatus(),
        };
    }

    private IpcResponse Purge(IpcRequest request)
        => new()
        {
            Id = request.Id,
            Ok = true,
            Report = _control.PurgeRecent(request.Minutes is > 0 ? request.Minutes.Value : 15),
        };

    private static IpcResponse Run(int id, Action action)
    {
        action();
        return new IpcResponse { Id = id, Ok = true };
    }
}
