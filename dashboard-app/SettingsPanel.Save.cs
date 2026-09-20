using System.Windows;

namespace ScreenRecall.Dashboard;

/// <summary>Saving settings back through the service.</summary>
public partial class SettingsPanel
{
    private void OnSaveSettings(object sender, RoutedEventArgs e)
    {
        Storage.RecallConfig current = _loaded
                                       ?? _client.GetConfig()
                                       ?? Storage.ConfigStore.Load(Storage.RecallConfig.UserConfigPath());
        Storage.RecallConfig updated = current.Clone();

        updated.StoragePath = StoragePathBox.Text.Trim();
        updated.RetentionDays = int.TryParse(RetentionBox.Text, out int retention) ? retention : current.RetentionDays;
        updated.CheckpointSeconds = int.TryParse(CheckpointBox.Text, out int checkpoint) ? checkpoint : current.CheckpointSeconds;
        updated.FidelityMode = FidelityBox.SelectedIndex == 1 ? "balanced" : "lossless";
        updated.CaptureAllMonitors = CaptureAllMonitorsBox.IsChecked ?? true;
        updated.ExcludedProcesses = SplitLines(ExclusionsBox.Text);
        updated.ExcludedTitlePatterns = SplitLines(TitleExclusionsBox.Text);

        Storage.RecallConfig? saved = _client.SetConfig(updated);
        if (saved is null)
        {
            Storage.ConfigStore.Save(Storage.RecallConfig.UserConfigPath(), updated.Normalize());
            _loaded = updated.Normalize();
            SettingsStatusText.Text = "service not reachable — saved for this user; it applies when the service starts";
            return;
        }

        _loaded = saved;
        SettingsStatusText.Text = "saved and applied"
                                  + (string.Equals(saved.StoragePath, current.StoragePath, StringComparison.OrdinalIgnoreCase)
                                      ? string.Empty
                                      : " — new storage folder in effect; existing data was not moved");
        RefreshStatus();
    }
}
