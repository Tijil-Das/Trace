using ScreenRecall.CaptureService.Capture;
using ScreenRecall.CaptureService.Ipc;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Service;

/// <summary>
/// Glue between the IPC surface and the running engine: holds the live configuration, turns dashboard
/// commands into engine calls, and assembles status/statistics payloads.
/// </summary>
internal sealed partial class CaptureController : ICaptureControl
{
    private readonly object _sync = new();
    private readonly string _configPath;
    private CaptureEngine? _engine;
    private CancellationTokenSource? _cts;

    internal CaptureController(string configPath, RecallConfig config)
    {
        _configPath = configPath;
        CurrentConfig = config.Clone().Normalize();
    }

    /// <summary>Configuration currently in force.</summary>
    internal RecallConfig CurrentConfig { get; private set; }

    /// <summary>Path the configuration is persisted to.</summary>
    internal string ConfigPath => _configPath;

    /// <summary>Running engine, when capture has started.</summary>
    internal CaptureEngine? Engine => _engine;

    /// <summary>Set when the service was asked to stop over IPC.</summary>
    internal bool ShutdownRequested { get; private set; }

    public RecallConfig GetConfig() => CurrentConfig.Clone();

    public void SetConfig(RecallConfig config)
    {
        lock (_sync)
        {
            CurrentConfig = config.Clone().Normalize();
            ConfigStore.Save(_configPath, CurrentConfig);
            _engine?.ApplyConfig(CurrentConfig);
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            CurrentConfig.Paused = true;
            ConfigStore.Save(_configPath, CurrentConfig);
            _engine?.Pause();
        }
    }

    public void Resume()
    {
        lock (_sync)
        {
            CurrentConfig.Paused = false;
            ConfigStore.Save(_configPath, CurrentConfig);
            _engine?.Resume();
        }
    }

    public void TogglePause()
    {
        if (CurrentConfig.Paused)
        {
            Resume();
        }
        else
        {
            Pause();
        }
    }

    public void Flush() => _engine?.FlushSessionState();

    public PruneReportDto PurgeRecent(int minutes)
    {
        CaptureEngine? engine = _engine;
        if (engine is null)
        {
            return EmptyReport(Array.Empty<string>());
        }

        return ToDto(engine.PurgeRecent(TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60))));
    }

    public PruneReportDto Prune()
    {
        CaptureEngine? engine = _engine;
        if (engine is null)
        {
            return ToDto(new RetentionPruner(CurrentConfig.StoragePath)
                .Prune(CurrentConfig.RetentionDays, DateTimeOffset.Now));
        }

        return ToDto(engine.PruneNow());
    }

    public void RequestShutdown()
    {
        ShutdownRequested = true;
        _cts?.Cancel();
    }

    /// <summary>Creates the engine, then starts the capture loop and the IPC server.</summary>
    internal CaptureEngine Start(IFrameSource source, out CaptureIpcServer server)
    {
        lock (_sync)
        {
            _cts ??= new CancellationTokenSource();
            CaptureEngine engine = new(CurrentConfig, source);
            _engine = engine;
            if (CurrentConfig.Paused)
            {
                engine.Pause();
            }

            server = new CaptureIpcServer(this);
            server.Start();
            _ = Task.Run(
                () =>
                {
                    try
                    {
                        engine.Run(_cts.Token);
                    }
                    catch (Exception ex)
                    {
                        engine.ReportFatal(ex);
                    }
                },
                CancellationToken.None);
            return engine;
        }
    }

    /// <summary>Stops capture and disposes the engine.</summary>
    internal void Stop()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _engine?.Dispose();
            _engine = null;
        }
    }

    private static PruneReportDto EmptyReport(string[] days) => new(0, 0, 0, 0, 0, days);

    private static PruneReportDto ToDto(PruneReport report)
        => new(
            report.DaysDeleted,
            report.SessionBytesDeleted,
            report.AssetsDeleted,
            report.AssetBytesDeleted,
            report.BytesReclaimed,
            report.DeletedDays.Select(SessionLayout.DayName).ToArray());
}
