using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace ScreenRecall.Dashboard.Services;

/// <summary>
/// Tray icon plus global pause/resume hotkey (spec 5.7 / 8). The icon's tooltip and colour mirror the
/// service state, so recording is never silent — even for a single-user personal tool (spec 14).
/// </summary>
public sealed class TrayHost : IDisposable
{
    private const int HotkeyId = 0x5C12;

    private static readonly Color RecordingColor = Color.FromArgb(0x3F, 0xD1, 0x6B);
    private static readonly Color PausedColor = Color.FromArgb(0xFF, 0x9F, 0x45);

    /// <summary>
    /// Capture is running but deliberately recording nothing because no display can be duplicated (locked session,
    /// secure desktop, a second instance). Deliberately *not* the recording colour: a green light while nothing is
    /// being recorded is exactly the silent failure spec 14 forbids.
    /// </summary>
    private static readonly Color IdleColor = Color.FromArgb(0xE8, 0x5D, 0x5D);

    private static readonly Color StoppedColor = Color.FromArgb(0x88, 0x8C, 0x98);

    private readonly CaptureClient _client;
    private readonly Action _show;
    private readonly Action _quit;
    private readonly NotifyIcon _icon;
    private readonly System.Windows.Forms.Timer _timer;

    // One icon per state, built once. Rebuilding an icon on every refresh would churn GDI handles
    // (and re-draw bitmaps) 43,000 times a day for a tool that is meant to run for weeks.
    private readonly Icon _recordingIcon;
    private readonly Icon _pausedIcon;
    private readonly Icon _idleIcon;
    private readonly Icon _stoppedIcon;
    private bool _paused;
    private bool _serviceRunning;
    private bool _blocked;
    private bool _notifiedBlocked;
    private Color _shownColor;
    private string? _shownText;

    public TrayHost(CaptureClient client, Action show, Action quit)
    {
        _client = client;
        _show = show;
        _quit = quit;

        _recordingIcon = CreateIcon(RecordingColor);
        _pausedIcon = CreateIcon(PausedColor);
        _idleIcon = CreateIcon(IdleColor);
        _stoppedIcon = CreateIcon(StoppedColor);

        _icon = new NotifyIcon
        {
            Icon = _recordingIcon,
            Visible = true,
            Text = "Screen Recall — starting",
        };
        _shownColor = RecordingColor;

        ContextMenuStrip menu = new();
        menu.Items.Add(new ToolStripMenuItem("Open Screen Recall", null, (_, _) => _show()));
        menu.Items.Add(new ToolStripMenuItem("Pause / resume recording\tCtrl+Alt+P", null, (_, _) => Toggle()));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Purge last 15 minutes", null, (_, _) =>
        {
            PruneInfo? report = _client.PurgeRecent(15);
            _icon.ShowBalloonTip(3000, "Screen Recall", report?.Describe() ?? "service not reachable", ToolTipIcon.Info);
        }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("Quit dashboard", null, (_, _) => _quit()));
        _icon.ContextMenuStrip = menu;
        _icon.DoubleClick += (_, _) => _show();

        _timer = new System.Windows.Forms.Timer { Interval = 2000 };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    /// <summary>Registers the global hotkey with the given window handle.</summary>
    public bool RegisterHotkey(IntPtr windowHandle)
        => RegisterHotKey(windowHandle, HotkeyId, 0x0002 | 0x0001, 0x50); // Ctrl+Alt+P

    /// <summary>Unregisters the global hotkey.</summary>
    public void UnregisterHotkey(IntPtr windowHandle) => UnregisterHotKey(windowHandle, HotkeyId);

    /// <summary>True when the last hotkey message was ours.</summary>
    public static bool IsHotkeyMessage(int message, int wParam) => message == 0x0312 && wParam == HotkeyId;

    /// <summary>Toggles recording through the service.</summary>
    public void Toggle()
    {
        if (!_client.IsRunning())
        {
            _icon.ShowBalloonTip(3000, "Screen Recall", "The capture service is not running.", ToolTipIcon.Warning);
            return;
        }

        _client.SetPaused(!_paused);
        Refresh();
    }

    /// <summary>Updates the icon to reflect the service state.</summary>
    public void Refresh()
    {
        CaptureStatus? status = _client.GetStatus();
        _serviceRunning = status is not null;
        _paused = status?.Paused ?? false;
        _blocked = status?.IsWaitingForOutput ?? false;

        Color color = !_serviceRunning ? StoppedColor
            : _paused ? PausedColor
            : _blocked ? IdleColor
            : RecordingColor;

        // Touching the shell on every tick is wasted work (and can make the tooltip flicker): only
        // change the icon and text when the state they describe has actually changed.
        if (color != _shownColor)
        {
            _shownColor = color;
            _icon.Icon = color == StoppedColor ? _stoppedIcon
                : color == PausedColor ? _pausedIcon
                : color == IdleColor ? _idleIcon
                : _recordingIcon;
        }

        string text = !_serviceRunning
            ? "Screen Recall — capture service not running"
            : _paused
                ? "Screen Recall — paused"
                : _blocked
                    ? "Screen Recall — not recording: no capturable output"
                    : $"Screen Recall — recording ({status!.TilesStored} tiles stored today)";
        if (text != _shownText)
        {
            _shownText = text;
            _icon.Text = text;
        }

        // Say it out loud once when capture stops being possible: on a locked workstation nothing else about the
        // service looks different, and the whole point is that a recorder which is not recording says so (spec 14).
        if (_blocked && !_notifiedBlocked)
        {
            _notifiedBlocked = true;
            _icon.ShowBalloonTip(
                5000,
                "Screen Recall — capture is idle",
                $"No capturable output: {status!.UnavailableDetail}. Nothing is being recorded. "
                + $"The service keeps retrying every {(int)Math.Max(1, status.RetryInSeconds)}s.",
                ToolTipIcon.Warning);
        }
        else if (!_blocked)
        {
            _notifiedBlocked = false;
        }
    }

    /// <summary>Balloon notice shown when the window is closed to the tray.</summary>
    public void ShowMinimisedNotice()
        => _icon.ShowBalloonTip(2500, "Screen Recall", "Still running in the notification area. Recording continues.", ToolTipIcon.Info);

    public void Dispose()
    {
        _timer.Stop();
        _timer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _recordingIcon.Dispose();
        _pausedIcon.Dispose();
        _idleIcon.Dispose();
        _stoppedIcon.Dispose();
    }

    /// <summary>
    /// Draws a simple coloured dot as the tray icon (no image assets required). The returned icon owns
    /// its handle: <see cref="Bitmap.GetHicon"/> creates a GDI icon that this process must destroy, and
    /// <see cref="Icon.FromHandle"/> deliberately does not take ownership of it. Cloning produces an
    /// icon that owns a private copy, so the borrowed handle is released immediately — otherwise every
    /// call would leak one GDI object for the lifetime of the dashboard.
    /// </summary>
    private static Icon CreateIcon(Color color)
    {
        using Bitmap bitmap = new(16, 16);
        using (Graphics graphics = Graphics.FromImage(bitmap))
        {
            graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using SolidBrush brush = new(color);
            graphics.FillEllipse(brush, 1, 1, 14, 14);
            using Pen pen = new(Color.FromArgb(0xC0, 0x10, 0x14, 0x1A), 1.4f);
            graphics.DrawEllipse(pen, 1, 1, 14, 14);
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, int fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
