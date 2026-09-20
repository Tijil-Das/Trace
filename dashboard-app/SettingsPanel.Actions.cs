using System.Windows;

using MessageBox = System.Windows.MessageBox;

namespace ScreenRecall.Dashboard;

/// <summary>Button actions on the status/settings panel.</summary>
public partial class SettingsPanel
{
    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        Services.CaptureStatus? status = _client.GetStatus();
        if (status is null)
        {
            SettingsStatusText.Text = "service not reachable";
            return;
        }

        _client.SetPaused(!status.Paused);
        RefreshStatus();
    }

    private void OnPurge(object sender, RoutedEventArgs e)
    {
        MessageBoxResult confirm = MessageBox.Show(
            "Delete the last 15 minutes of recording and reclaim its tiles?\n\nThis cannot be undone.",
            "Screen Recall — purge",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        Services.PruneInfo? report = _client.PurgeRecent(15);
        SettingsStatusText.Text = report is null ? "service not reachable" : $"purged: {report.Describe()}";
        DaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnPrune(object sender, RoutedEventArgs e)
    {
        Services.PruneInfo? report = _client.Prune();
        SettingsStatusText.Text = report is null ? "service not reachable" : $"pruned: {report.Describe()}";
        DaysChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnProbe(object sender, RoutedEventArgs e)
    {
        List<Services.MonitorDto> monitors = _client.Probe();
        SettingsStatusText.Text = monitors.Count == 0
            ? "probe found no duplicatable monitors"
            : "monitors: " + string.Join("; ", monitors.Select(monitor => monitor.Display));
    }
}
