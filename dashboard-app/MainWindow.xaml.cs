using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

using ScreenRecall.Dashboard.Services;
using ScreenRecall.Player;
using ScreenRecall.Storage;

using Application = System.Windows.Application;

namespace ScreenRecall.Dashboard;

/// <summary>
/// Timeline, player and jump-to-focus window (spec 8). Playback reconstructs frames from the checkpoint
/// plus reference log through the same player library the CLI uses; nothing here decodes video.
/// </summary>
public partial class MainWindow : Window
{
    private readonly CaptureClient _client = new();
    private WebDashboardBridge? _bridge;
    private SessionPlayer? _player;
    private DateOnly _day;
    private WriteableBitmap? _bitmap;
    private System.Windows.Threading.DispatcherTimer? _statusTimer;
    private bool _scrubbing;
    private bool _needsRender;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Playback runs on the display's own tick. A timer can only fire slower than the compositor and its
        // interval is a guess about how long the work takes; the compositor says when a frame is actually
        // wanted, and the player decides whether anything changed.
        System.Windows.Media.CompositionTarget.Rendering += OnCompositorTick;

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => SafeTick("status", () => Settings.RefreshStatus());
        _statusTimer.Start();

        Settings.DaysChanged += (_, _) => SafeTick("days-changed", () => RefreshDays());
        SafeTick("init-status", () => Settings.RefreshStatus());
        SafeTick("init-days", () => RefreshDays());

        // The React dashboard takes over the window when it has been built; the panels above stay as the
        // fallback, so a missing dist folder degrades to the older shell instead of an empty window.
        _ = InitializeWebDashboardAsync();
    }

    /// <summary>Compositor tick: free when nothing changed, and skipped entirely while hidden in the tray.</summary>
    private void OnCompositorTick(object? sender, EventArgs e)
    {
        if (_player is null || !IsLoaded)
        {
            return;
        }

        if (!IsVisible)
        {
            // Hidden in the tray: stop the playhead rather than advance a video nobody is looking at.
            _player.Pause();
            return;
        }

        SafeTick("render", OnRenderTick);
    }

    /// <summary>
    /// Runs one UI tick, reporting failures in the window rather than a log file. This is a GUI: a swallowed
    /// exception here is indistinguishable from a frozen player, and the log it used to go to was a hardcoded
    /// path on one developer's drive.
    /// </summary>
    private void SafeTick(string name, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            try
            {
                PlaybackInfoText.Text = $"{name} failed: {ex.Message}";
            }
            catch (Exception)
            {
                // The label itself is gone (window tearing down): nothing left to report to.
            }
        }
    }

    /// <summary>
    /// Brings up the React dashboard if it exists. Failure is not an error: a missing build or a machine without
    /// the WebView2 runtime simply keeps the WPF panels, which are still fully functional.
    /// </summary>
    private async Task InitializeWebDashboardAsync()
    {
        WebDashboardBridge bridge = new(WebView, _client, Settings.StorageRootOrUserDefault);
        try
        {
            if (!await bridge.InitializeAsync())
            {
                bridge.Dispose();
                PlaybackInfoText.Text = "web dashboard not built — using the built-in shell";
                return;
            }
        }
        catch (Exception ex)
        {
            bridge.Dispose();
            PlaybackInfoText.Text = $"web dashboard unavailable: {ex.Message}";
            return;
        }

        bridge.OpenDay = OpenDayForPlayer;
        bridge.Player = _player;
        _bridge = bridge;
        HideWpfPanels();
    }

    /// <summary>Hands the window over to the WebView: the WPF panels are the fallback, not a second view.</summary>
    private void HideWpfPanels()
    {
        DaysPanel.Visibility = Visibility.Collapsed;
        PlaybackPanel.Visibility = Visibility.Collapsed;
        FramePanel.Visibility = Visibility.Collapsed;
        FocusPanel.Visibility = Visibility.Collapsed;
        Settings.Visibility = Visibility.Collapsed;
        WebHost.Visibility = Visibility.Visible;
    }

    private void OnSourceInitialized(object sender, EventArgs e)
    {
        // Global pause/resume hotkey, registered on this window's handle.
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (Application.Current is App app && app.Tray is not null)
        {
            app.Tray.RegisterHotkey(handle);
            HwndSource source = HwndSource.FromHwnd(handle)!;
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // TEMP-DEBUG: wParam can be a 64-bit pointer-sized value; ToInt32() overflows on ARM64-style
        // handles. Compare full-width instead — never let a stray window message kill the dashboard.
        try
        {
            if (msg == 0x0312 && wParam.ToInt64() == 0x5C12 && Application.Current is App app)
            {
                app.Tray?.Toggle();
                SafeTick("hotkey-status", () => Settings.RefreshStatus());
                handled = true;
            }
        }
        catch (Exception)
        {
            // Never let a stray window message kill the dashboard: the pause/resume hotkey is a convenience, and
            // this window is the only way back to it.
            handled = false;
        }

        return IntPtr.Zero;
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Tray-resident: closing the window must not stop recording.
        e.Cancel = true;
        _player?.Pause();
        Hide();
        if (Application.Current is App app)
        {
            app.Tray?.ShowMinimisedNotice();
        }
    }

    private void RefreshDays()
    {
        List<DayInfo> days = _client.GetDays();
        if (days.Count == 0)
        {
            // Fall back to the on-disk store when the service is not running: recorded days are still
            // readable, they only lack today's live session.
            string root = Settings.StorageRootOrUserDefault();
            days = SessionBrowser.Days(root)
                .Select(day => SessionBrowser.Summarize(root, day))
                .Select(summary => new DayInfo
                {
                    Day = summary.Day.ToString("yyyy-MM-dd"),
                    StartTs = summary.FirstTimestampUs / 1000,
                    EndTs = summary.LastTimestampUs / 1000,
                    Bytes = summary.SessionBytes,
                    SpanMs = summary.DurationUs / 1000,
                })
                .ToList();
        }

        days.Sort((left, right) => string.CompareOrdinal(right.Day, left.Day));
        string? previous = DayList.SelectedItem is DayInfo selected ? selected.Day : null;
        DayList.ItemsSource = days;
        DayList.SelectedItem = days.FirstOrDefault(day => day.Day == previous) ?? days.FirstOrDefault();
    }

    private void OnRefreshDays(object sender, RoutedEventArgs e) => RefreshDays();

    private void OnOpenStorage(object sender, RoutedEventArgs e)
    {
        string root = Settings.StorageRootOrUserDefault();
        Directory.CreateDirectory(root);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(root) { UseShellExecute = true });
    }

    private void OnDaySelected(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DayList.SelectedItem is DayInfo info)
        {
            LoadDay(info.Day);
        }
    }

    private void LoadDay(string dayText) => OpenDayForPlayer(dayText);

    /// <summary>
    /// Opens a recorded day and makes it the one on screen. Returns the player so the web dashboard can be handed
    /// the same object the WPF shell would use, instead of opening the session twice.
    /// </summary>
    private SessionPlayer? OpenDayForPlayer(string dayText)
    {
        if (!DateOnly.TryParseExact(dayText, "yyyy-MM-dd", out DateOnly day))
        {
            return null;
        }

        try
        {
            _player?.Dispose();
            _day = day;
            _player = new SessionPlayer(Settings.StorageRootOrUserDefault(), day);
            if (_bridge is not null)
            {
                _bridge.Player = _player;
            }

            Scrubber.Maximum = Math.Max(1, _player.DurationUs);
            Scrubber.Value = 0;
            LoadFocusList();
            SeekTo(_player.FirstTimestampUs);
            _needsRender = true;
            if (_bridge is null)
            {
                PlaybackInfoText.Text = $"checkpoints {_player.Checkpoints.Count}";
                FramePlaceholder.Visibility = Visibility.Collapsed;
            }

            return _player;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            if (_bridge is null)
            {
                FramePlaceholder.Text = $"could not open {dayText}: {ex.Message}";
                FramePlaceholder.Visibility = Visibility.Visible;
            }

            return null;
        }
    }

    private void LoadFocusList()
    {
        if (_player is null)
        {
            FocusList.ItemsSource = null;
            return;
        }

        FocusList.ItemsSource = _player.Replayer.WindowSpans()
            .GroupBy(span => (span.AppName, span.WindowTitle))
            .Select(group => new FocusItem
            {
                App = group.Key.AppName,
                Title = group.Key.WindowTitle,
                StartTs = group.Min(span => span.StartTs),
            })
            .OrderBy(item => item.StartTs)
            .ToList();
    }
}
