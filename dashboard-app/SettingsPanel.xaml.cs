using System.Windows;
using System.Windows.Controls;

using ScreenRecall.Dashboard.Services;
using ScreenRecall.Storage;

namespace ScreenRecall.Dashboard;

/// <summary>
/// Status and settings panel (spec 8): live resource meter, pause/purge controls, and the settings the
/// spec asks for — storage path, retention, exclusions, monitors, fidelity mode. Everything goes
/// through the service over IPC, so the dashboard never touches the files the service is writing.
/// </summary>
public partial class SettingsPanel : System.Windows.Controls.UserControl
{
    private readonly CaptureClient _client = new();
    private RecallConfig? _loaded;

    public SettingsPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => ReloadSettings();
    }

    /// <summary>Raised when something happened that affects the day list (prune, purge).</summary>
    public event EventHandler? DaysChanged;

    /// <summary>Storage root in force, falling back to the per-user default when the service is down.</summary>
    public string StorageRootOrUserDefault() => _loaded?.StoragePath ?? RecallConfig.UserStoragePath();

    /// <summary>Refreshes the live status readout.</summary>
    public void RefreshStatus()
    {
        CaptureStatus? status = _client.GetStatus();
        if (status is null)
        {
            ServiceStateText.Text = "capture service not reachable — start ScreenRecall.CaptureService "
                                    + "(dev mode: --console) or register it as a Windows service.";
            ResourceText.Text = string.Empty;
            IntegrityText.Text = string.Empty;
            ErrorText.Text = string.Empty;
            PauseButton.Content = "Pause recording";
            return;
        }

        ServiceStateText.Text =
            $"{status.State.ToUpperInvariant()}   source {status.Source}   monitors {status.Monitors}   day {status.Day}\n"
            + (status.IsWaitingForOutput
                ? $"capture paused — no capturable output: {status.UnavailableDetail}. Nothing is being recorded; "
                  + $"retrying every {(int)Math.Max(1, status.RetryInSeconds)}s.\n"
                : string.Empty)
            + $"foreground: {status.ForegroundApp ?? "-"}{(status.Excluded ? "   (excluded — not recorded)" : string.Empty)}";

        ResourceText.Text =
            $"cpu {status.CpuPercent:0.00}%   mem {status.WorkingSetMb:0} MB   queue {status.AssetQueueDepth}\n"
            + $"frames {status.FramesAcquired} ({status.FramesWithChanges} changed)   frame cost {status.AverageProcessMs:0.0} ms   "
            + $"pace {status.PaceIntervalMs:0} ms ({status.FramesPaced} paced)\n"
            + $"tiles hashed {status.TilesHashed}   stored {status.TilesStored}   deduped {status.TilesDeduped} ({status.DedupePercent:0.0}%)\n"
            + $"dedupe cache {status.DedupeCacheEntries} hash(es), {status.DedupeCacheHitPercent:0.0}% answered from memory\n"
            + $"log entries {status.LogEntries}   session {status.SessionBytes / 1024.0:0} KB\n"
            + $"store {status.AssetCountOnDisk} assets / {status.AssetBytesOnDisk / 1024.0 / 1024.0:0.00} MB   free {status.FreeDiskGb:0.0} GB\n"
            + $"rects last frame {status.LastDirtyRects} dirty / {status.LastMoveRects} move   throttle {status.ThrottleLevel}";

        IntegrityText.Text = status.HasIntegrityWarning
            ? $"log integrity warning: {status.LogZeroRecordFaults} zero record(s), external writer "
              + $"{status.LogExternalWriterDetected}"
            : string.Empty;

        ErrorText.Text = status.LastError ?? status.LastMaintenance ?? string.Empty;
        PauseButton.Content = status.Paused ? "Resume recording" : "Pause recording";
    }

    /// <summary>Reloads configuration from the service into the form.</summary>
    public void ReloadSettings()
    {
        RecallConfig? config = _client.GetConfig();
        if (config is null)
        {
            SettingsStatusText.Text = "service not reachable — showing saved defaults";
            config = ConfigStore.Load(RecallConfig.UserConfigPath());
        }
        else
        {
            SettingsStatusText.Text = "loaded from service";
        }

        _loaded = config;
        StoragePathBox.Text = config.StoragePath;
        RetentionBox.Text = config.RetentionDays.ToString();
        CheckpointBox.Text = config.CheckpointSeconds.ToString();
        FidelityBox.SelectedIndex = string.Equals(config.FidelityMode, "balanced", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        CaptureAllMonitorsBox.IsChecked = config.CaptureAllMonitors;
        ExclusionsBox.Text = string.Join(Environment.NewLine, config.ExcludedProcesses);
        TitleExclusionsBox.Text = string.Join(Environment.NewLine, config.ExcludedTitlePatterns);
    }

    private void OnReloadSettings(object sender, RoutedEventArgs e) => ReloadSettings();

    private static List<string> SplitLines(string text)
        => text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
