namespace ScreenRecall.Storage;

/// <summary>
/// Everything the user can configure (spec 8 / 10). Persisted as JSON in
/// <c>%ProgramData%\ScreenRecall\config.json</c> so the background service and the dashboard share a
/// single source of truth.
/// </summary>
public sealed partial class RecallConfig
{
    /// <summary>Config schema version.</summary>
    public int Version { get; set; } = 1;

    /// <summary>Where recordings live; changeable after install (migrated, never moved in place).</summary>
    public string StoragePath { get; set; } = DefaultStoragePath();

    /// <summary>Retention window in days; older days are pruned automatically.</summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>"lossless" (default) or "balanced" (near-lossless, smaller).</summary>
    public string FidelityMode { get; set; } = "lossless";

    /// <summary>Tile edge length. Must stay constant for a store so tile dedupe keeps working.</summary>
    public int TileSize { get; set; } = TileGrid.DefaultTileSize;

    /// <summary>Acquire timeout while the screen is static (spec 5.1 backoff).</summary>
    public int IdlePollMs { get; set; } = 1000;

    /// <summary>Acquire timeout during bursts of change (0 = return as soon as a frame is ready).</summary>
    public int BurstPollMs { get; set; }

    /// <summary>
    /// Longest pause the capture governor inserts between frames with changes (spec 5.8), in milliseconds — and so
    /// the floor on the capture rate (250 means roughly four frames a second in the worst case). Governing bounds
    /// frames/second by what frames cost rather than by a fixed rate, so cheap frames run fast and only expensive
    /// ones are paced. Zero disables pacing entirely (tests, which validate the pipeline's invariants rather than
    /// its pacing, set it to 0).
    /// </summary>
    public int MaxPaceMs { get; set; } = 250;

    /// <summary>How often a full-state checkpoint is written (spec 5.6).</summary>
    public int CheckpointSeconds { get; set; } = 180;

    /// <summary>Capture every monitor when true, otherwise only <see cref="MonitorIds"/>.</summary>
    public bool CaptureAllMonitors { get; set; } = true;

    /// <summary>Monitors to capture when <see cref="CaptureAllMonitors"/> is false.</summary>
    public List<ushort> MonitorIds { get; set; } = new();

    /// <summary>Process names to skip (password managers etc. — spec 5.7 / 14).</summary>
    public List<string> ExcludedProcesses { get; set; } = ExclusionDefaults.Processes.ToList();

    /// <summary>Window-title fragments to skip (private/incognito windows — spec 5.7 / 14).</summary>
    public List<string> ExcludedTitlePatterns { get; set; } = ExclusionDefaults.TitlePatterns.ToList();

    /// <summary>
    /// Encrypt-at-rest toggle (spec 6 / 14). Reserved switch: the default flips to true once the
    /// AES-256-GCM + DPAPI envelope lands (build order step 7). Enabling it today changes nothing.
    /// </summary>
    public bool EncryptionAtRest { get; set; }

    /// <summary>Test-only: also dump full ground-truth frames for the fidelity harness (spec 12).</summary>
    public bool CaptureGroundTruth { get; set; }

    /// <summary>Pause capture while the machine runs on battery.</summary>
    public bool PauseOnBattery { get; set; }

    /// <summary>Soft daily budget in MB; exceeding it tightens compression and warns.</summary>
    public int MaxDailyMegabytes { get; set; } = 2048;

    /// <summary>Stop recording when free disk space drops below this.</summary>
    public int MinFreeDiskMegabytes { get; set; } = 2048;

    /// <summary>Persisted pause state, so the tray toggle survives a restart.</summary>
    public bool Paused { get; set; }

    /// <summary>Global pause/resume hotkey registered by the dashboard.</summary>
    public string PauseHotkey { get; set; } = "Ctrl+Alt+P";

    /// <summary>
    /// Per-tile phase timing for the diagnostics readout (`LastHashMs`/`LastEncodeMs`/`LastStoreMs`/`LastLogMs`).
    ///
    /// Off by default, and that default is a measured decision: four <c>Stopwatch.GetElapsedTime</c> pairs per tile
    /// were **23% of capture-thread samples** in a 30 s `dotnet-trace` of a Release recorder (`docs/PERFORMANCE.md`
    /// §5a) — the largest single cost on the thread, larger than encoding, hashing and the filesystem probe
    /// combined. The split is genuinely useful when you are chasing where a frame went, so it is kept behind this
    /// switch and turned on by the benchmark harness, which needs exactly those numbers. A production Release run
    /// pays two timestamp reads per *frame* instead of eight per *tile*.
    /// </summary>
    public bool DetailedTiming { get; set; }

    /// <summary>Clamps user input into sane ranges and normalises enumerations.</summary>
    public RecallConfig Normalize()
    {
        RetentionDays = Math.Clamp(RetentionDays, 1, 3650);
        TileSize = Math.Clamp(TileSize, 16, 512);
        IdlePollMs = Math.Clamp(IdlePollMs, 50, 5000);
        BurstPollMs = Math.Clamp(BurstPollMs, 0, 500);
        MaxPaceMs = Math.Clamp(MaxPaceMs, 0, 5000);
        CheckpointSeconds = Math.Clamp(CheckpointSeconds, 10, 3600);
        MaxDailyMegabytes = Math.Clamp(MaxDailyMegabytes, 64, 1024 * 1024);
        MinFreeDiskMegabytes = Math.Clamp(MinFreeDiskMegabytes, 128, 1024 * 1024);
        if (string.IsNullOrWhiteSpace(StoragePath))
        {
            StoragePath = DefaultStoragePath();
        }

        FidelityMode = TileCodecs.FromFidelityMode(FidelityMode) == TileCodecs.Balanced ? "balanced"
            : TileCodecs.FromFidelityMode(FidelityMode) == TileCodecs.Archive ? "archive"
            : "lossless";
        ExcludedProcesses ??= new List<string>();
        ExcludedTitlePatterns ??= new List<string>();
        MonitorIds ??= new List<ushort>();
        return this;
    }
}
