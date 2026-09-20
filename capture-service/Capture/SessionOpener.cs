using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>Opens the writing end of a day's session: log segment, manifest part and index connection.</summary>
internal static class SessionOpener
{
    internal static (SessionStore Session, SessionLogWriter Log, AssetManifestWriter Manifest, RecallIndex? Index) Open(
        RecallConfig config,
        DateOnly day)
    {
        SessionStore session = SessionStore.Open(config.StoragePath, day, config.TileSize);
        session.UpdateMonitors(MonitorsFor(session), DateTimeOffset.Now);

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
        => DateTimeOffset.Parse($"{SessionLayout.DayName(day)}T00:00:00", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Monitor geometry recorded in the session meta (empty on a brand new session).</summary>
    private static IReadOnlyList<MonitorInfo> MonitorsFor(SessionStore session)
        => session.Meta.AllMonitors();

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
