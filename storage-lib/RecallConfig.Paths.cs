namespace ScreenRecall.Storage;

public sealed partial class RecallConfig
{
    /// <summary>Deep copy — the dashboard stages edits before saving.</summary>
    public RecallConfig Clone() => new()
    {
        Version = Version,
        StoragePath = StoragePath,
        RetentionDays = RetentionDays,
        FidelityMode = FidelityMode,
        TileSize = TileSize,
        IdlePollMs = IdlePollMs,
        BurstPollMs = BurstPollMs,
        CheckpointSeconds = CheckpointSeconds,
        CaptureAllMonitors = CaptureAllMonitors,
        MonitorIds = MonitorIds.ToList(),
        ExcludedProcesses = ExcludedProcesses.ToList(),
        ExcludedTitlePatterns = ExcludedTitlePatterns.ToList(),
        EncryptionAtRest = EncryptionAtRest,
        CaptureGroundTruth = CaptureGroundTruth,
        PauseOnBattery = PauseOnBattery,
        MaxDailyMegabytes = MaxDailyMegabytes,
        MinFreeDiskMegabytes = MinFreeDiskMegabytes,
        Paused = Paused,
        PauseHotkey = PauseHotkey,
    };

    /// <summary>Default storage root: <c>%ProgramData%\ScreenRecall\data</c>.</summary>
    public static string DefaultStoragePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ScreenRecall",
            "data");

    /// <summary>Default config path: <c>%ProgramData%\ScreenRecall\config.json</c>.</summary>
    public static string DefaultConfigPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ScreenRecall",
            "config.json");

    /// <summary>Per-user config path: <c>%LOCALAPPDATA%\ScreenRecall\config.json</c>.</summary>
    public static string UserConfigPath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenRecall",
            "config.json");

    /// <summary>Per-user storage root: <c>%LOCALAPPDATA%\ScreenRecall\data</c>.</summary>
    public static string UserStoragePath()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ScreenRecall",
            "data");
}
