using System.Threading;
using System.Windows;

using ScreenRecall.Dashboard.Services;

using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;

namespace ScreenRecall.Dashboard;

/// <summary>
/// Dashboard shell (spec 8): tray-resident, single instance, with a global pause/resume hotkey that
/// stays live even when the window is closed to the tray. The capture service keeps running without
/// it — the dashboard is only a window onto the store and the service's live status.
/// </summary>
public partial class App : System.Windows.Application
{
    private Mutex? _singleInstance;

    /// <summary>Tray icon that makes the recording state visible at all times (spec 14).</summary>
    public TrayHost? Tray { get; private set; }

    /// <summary>Global hotkey sender used by the tray menu and the hotkey.</summary>
    public CaptureClient Client { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstance = new Mutex(true, "ScreenRecall.Dashboard.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show("Screen Recall's dashboard is already running (look in the notification area).",
                "Screen Recall", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        base.OnStartup(e);
        Tray = new TrayHost(Client, () => MainWindow?.Show(), () => Shutdown());
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Tray?.Dispose();
        _singleInstance?.Dispose();
        base.OnExit(e);
    }
}
