using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

using ScreenRecall.Storage;

namespace ScreenRecall.Dashboard.Services;

/// <summary>
/// Named-pipe client for the capture service (spec 9): the dashboard reads live status and sends
/// commands through this and never opens the log or asset files while the service is writing them.
/// </summary>
public sealed class CaptureClient
{
    private readonly string _pipeName;
    private int _nextId;

    public CaptureClient(string pipeName = "ScreenRecall.Capture")
    {
        _pipeName = pipeName;
    }

    /// <summary>True when the service answered a ping.</summary>
    public bool IsRunning() => Send("ping")?.Ok == true;

    /// <summary>Live capture status, or null when the service is not reachable.</summary>
    public CaptureStatus? GetStatus() => Send("status")?.Status;

    /// <summary>Current configuration, or null when unreachable.</summary>
    public RecallConfig? GetConfig() => Send("config.get")?.Config;

    /// <summary>Saves configuration to the service.</summary>
    public RecallConfig? SetConfig(RecallConfig config) => Send("config.set", request => request.Config = config)?.Config;

    /// <summary>Pauses or resumes capture.</summary>
    public bool SetPaused(bool paused) => Send(paused ? "pause" : "resume")?.Ok == true;

    /// <summary>Drops the last N minutes and reclaims its assets.</summary>
    public PruneInfo? PurgeRecent(int minutes) => Send("purgeRecent", request => request.Minutes = minutes)?.Report;

    /// <summary>Runs retention pruning now.</summary>
    public PruneInfo? Prune() => Send("prune")?.Report;

    /// <summary>Recorded days as reported by the service.</summary>
    public List<DayInfo> GetDays() => Send("days")?.Days ?? new List<DayInfo>();

    /// <summary>Monitor enumeration from the service's probe.</summary>
    public List<MonitorDto> Probe() => Send("probe")?.Probe?.Monitors ?? new List<MonitorDto>();

    /// <summary>Flushes the log and manifest so a reader sees everything written so far.</summary>
    public bool Flush() => Send("flush")?.Ok == true;

    /// <summary>
    /// Asks the service to shut down. This is the graceful path: it drains queued tile writes and flushes the
    /// log before exiting, so the recording stays readable afterwards.
    /// </summary>
    public bool Shutdown() => Send("shutdown")?.Ok == true;

    private IpcEnvelope? Send(string command, Action<IpcEnvelope>? configure = null)
    {
        try
        {
            using NamedPipeClientStream client = new(".", _pipeName, PipeDirection.InOut, PipeOptions.None);
            client.Connect(1500);

            using StreamReader reader = new(client, Encoding.UTF8, false, 4096, leaveOpen: true);
            using StreamWriter writer = new(client, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };

            IpcEnvelope request = new() { Id = ++_nextId, Command = command };
            configure?.Invoke(request);
            writer.WriteLine(JsonSerializer.Serialize(request, RecallJson.WireOptions));

            string? line = reader.ReadLine();
            return line is null ? null : JsonSerializer.Deserialize<IpcEnvelope>(line, RecallJson.WireOptions);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                                      or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Wire format shared with the capture service (keep in sync with its IpcProtocol).</summary>
    public sealed class IpcEnvelope
    {
        public int Id { get; set; }

        public string Command { get; set; } = string.Empty;

        public bool Ok { get; set; }

        public string? Error { get; set; }

        public RecallConfig? Config { get; set; }

        public int? Minutes { get; set; }

        public CaptureStatus? Status { get; set; }

        public PruneInfo? Report { get; set; }

        public List<DayInfo>? Days { get; set; }

        public ProbeInfo? Probe { get; set; }
    }

    /// <summary>Probe payload from the service.</summary>
    public sealed class ProbeInfo
    {
        public string Source { get; set; } = string.Empty;

        public List<MonitorDto>? Monitors { get; set; }

        public bool DuplicationOk { get; set; }

        public string? Note { get; set; }
    }
}
