using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>A reconstructed segment's window spans, for the dashboard's jump-to-focus list.</summary>
public sealed record FocusSpan(string AppName, string WindowTitle, long StartTs, long EndTs, int Count);

/// <summary>Summary of one recorded day, for the timeline/calendar strip.</summary>
public sealed record DaySummary(
    DateOnly Day,
    long FirstTimestampUs,
    long LastTimestampUs,
    long LogBytes,
    long AssetCount,
    long SessionBytes,
    int Checkpoints,
    int Monitors,
    long DurationUs);

/// <summary>Cheap day-level queries the dashboard needs before it opens a player.</summary>
public static class SessionBrowser
{
    /// <summary>Every recorded day under a root, newest first.</summary>
    public static IReadOnlyList<DateOnly> Days(string root) => SessionLayout.ListDays(root).Reverse().ToArray();

    /// <summary>Summarizes one day without loading any tiles.</summary>
    public static DaySummary Summarize(string root, DateOnly day)
    {
        SessionStore store = SessionStore.Open(root, day);
        store.ReloadMeta();

        long first = 0;
        long last = 0;
        long logBytes = 0;
        foreach (string segment in store.LogSegments())
        {
            SessionLogStats stats = SessionLogReader.Scan(segment);
            logBytes += stats.FileBytes;
            if (stats.FirstTimestampUs > 0 && (first == 0 || stats.FirstTimestampUs < first))
            {
                first = stats.FirstTimestampUs;
            }

            if (stats.LastTimestampUs > last)
            {
                last = stats.LastTimestampUs;
            }
        }

        (long assetCount, long assetBytes) = store.Assets.ComputeStats();
        return new DaySummary(
            day,
            first,
            last,
            logBytes,
            assetCount,
            SessionLayout.SessionBytes(root, day) + assetBytes,
            CheckpointFormat.List(store.SessionDir).Count,
            store.Meta.AllMonitors().Count,
            last > first ? last - first : 0);
    }

    /// <summary>Focus spans of a day, grouped per window (navigation metadata only).</summary>
    public static IReadOnlyList<FocusSpan> FocusSpans(string root, DateOnly day)
    {
        try
        {
            using RecallIndex index = new(SessionLayout.IndexPath(root));
            return index.GetWindowSpans(SessionLayout.DayName(day))
                .GroupBy(span => (span.AppName, span.WindowTitle))
                .Select(group => new FocusSpan(
                    group.Key.AppName,
                    group.Key.WindowTitle,
                    group.Min(span => span.StartTs),
                    group.Max(span => span.EndTs),
                    group.Count()))
                .OrderBy(span => span.StartTs)
                .ToArray();
        }
        catch (Exception ex) when (ex is Microsoft.Data.Sqlite.SqliteException or IOException)
        {
            return Array.Empty<FocusSpan>();
        }
    }
}
