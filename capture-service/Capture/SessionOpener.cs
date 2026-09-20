using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>Opens the writing end of a day's session: log segment, manifest part and index connection.</summary>
internal static class SessionOpener
{
    internal static (SessionStore Session, SessionLogWriter Log, AssetManifestWriter Manifest, RecallIndex? Index) Open(
        RecallConfig config,
        DateOnly day,
        IReadOnlyList<MonitorInfo>? monitors = null)
    {
        SessionStore session = SessionStore.Open(config.StoragePath, day, config.TileSize);
        if (monitors is { Count: > 0 })
        {
            session.UpdateMonitors(monitors, DateTimeOffset.Now);
        }

        int segment = session.NextSegmentIndex();
        SessionLogWriter log = session.OpenLog(DayStart(day), segment);

        int part = AssetManifest.FilesFor(session.SessionDir).Count;
        AssetManifestWriter manifest = session.OpenManifest(part == 0 ? 0 : part);
        RecallIndex? index = TryOpenIndex(session.Root);

        index?.UpsertSession(
            SessionLayout.DayName(day),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            session.LogPath);

        return (session, log, manifest, index);
    }

    /// <summary>Local midnight that a day's log belongs to.</summary>
    internal static DateTimeOffset DayStart(DateOnly day)
        => DateTimeOffset.Parse(
            $"{SessionLayout.DayName(day)}T00:00:00",
            System.Globalization.CultureInfo.InvariantCulture);

    internal static RecallIndex? TryOpenIndex(string root)
    {
        try
        {
            return new RecallIndex(SessionLayout.IndexPath(root));
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            // Recording matters more than the navigation index; it rebuilds on the next run.
            return null;
        }
    }
}
