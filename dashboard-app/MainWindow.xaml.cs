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
    private SessionReplayer? _replayer;
    private DateOnly _day;
    private WriteableBitmap? _bitmap;
    private System.Windows.Threading.DispatcherTimer? _playbackTimer;
    private System.Windows.Threading.DispatcherTimer? _statusTimer;
    private bool _isPlaying;
    private bool _scrubbing;
    private double _speed = 1.0;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _playbackTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _playbackTimer.Tick += (_, _) => AdvancePlayback();

        _statusTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => Settings.RefreshStatus();
        _statusTimer.Start();

        Settings.DaysChanged += (_, _) => RefreshDays();
        Settings.RefreshStatus();
        RefreshDays();
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
        if (TrayHost.IsHotkeyMessage(msg, wParam.ToInt32()) && Application.Current is App app)
        {
            app.Tray?.Toggle();
            Settings.RefreshStatus();
            handled = true;
        }

        return IntPtr.Zero;
    }

    private void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Tray-resident: closing the window must not stop recording.
        e.Cancel = true;
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

    private void LoadDay(string dayText)
    {
        if (!DateOnly.TryParseExact(dayText, "yyyy-MM-dd", out DateOnly day))
        {
            return;
        }

        try
        {
            _replayer?.Dispose();
            _day = day;
            _replayer = new SessionReplayer(Settings.StorageRootOrUserDefault(), day);
            Scrubber.Maximum = Math.Max(1, _replayer.DurationUs);
            Scrubber.Value = 0;
            LoadFocusList();
            SeekTo(_replayer.FirstTimestampUs);
            PlaybackInfoText.Text = $"checkpoints {_replayer.Checkpoints.Count}";
            FramePlaceholder.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            FramePlaceholder.Text = $"could not open {dayText}: {ex.Message}";
            FramePlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void LoadFocusList()
    {
        if (_replayer is null)
        {
            FocusList.ItemsSource = null;
            return;
        }

        FocusList.ItemsSource = _replayer.WindowSpans()
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
