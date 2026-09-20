using Microsoft.Data.Sqlite;

namespace ScreenRecall.Storage;

public sealed partial class RecallIndex
{
    /// <summary>
    /// Records a focus span, coalescing with the previous span when the same window keeps focus so
    /// the table stays a compact list of boundaries instead of one row per poll.
    /// </summary>
    public void AddWindowSpan(
        string day,
        string appName,
        string windowTitle,
        long startTs,
        long endTs,
        long coalesceToleranceMs = 2000)
    {
        using (SqliteCommand last = _connection.CreateCommand())
        {
            last.CommandText =
                "SELECT id, app_name, window_title, end_ts FROM window_spans WHERE day = $day ORDER BY end_ts DESC LIMIT 1;";
            last.Parameters.AddWithValue("$day", day);
            using SqliteDataReader reader = last.ExecuteReader();
            if (reader.Read())
            {
                long id = reader.GetInt64(0);
                string lastApp = reader.IsDBNull(1) ? string.Empty : reader.GetString(1);
                string lastTitle = reader.IsDBNull(2) ? string.Empty : reader.GetString(2);
                long lastEnd = reader.GetInt64(3);
                if (lastApp == appName && lastTitle == windowTitle && startTs - lastEnd <= coalesceToleranceMs)
                {
                    reader.Close();
                    using SqliteCommand update = _connection.CreateCommand();
                    update.CommandText = "UPDATE window_spans SET end_ts = $end WHERE id = $id;";
                    update.Parameters.AddWithValue("$end", endTs);
                    update.Parameters.AddWithValue("$id", id);
                    update.ExecuteNonQuery();
                    return;
                }
            }
        }

        using SqliteCommand insert = _connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO window_spans (day, app_name, window_title, start_ts, end_ts)
            VALUES ($day, $app, $title, $start, $end);
            """;
        insert.Parameters.AddWithValue("$day", day);
        insert.Parameters.AddWithValue("$app", appName);
        insert.Parameters.AddWithValue("$title", windowTitle);
        insert.Parameters.AddWithValue("$start", startTs);
        insert.Parameters.AddWithValue("$end", endTs);
        insert.ExecuteNonQuery();
    }

    /// <summary>Focus spans of a day in chronological order (navigation only, never analytics).</summary>
    public IReadOnlyList<WindowSpanRow> GetWindowSpans(string day)
    {
        List<WindowSpanRow> spans = new();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT id, day, app_name, window_title, start_ts, end_ts
            FROM window_spans WHERE day = $day ORDER BY start_ts;
            """;
        command.Parameters.AddWithValue("$day", day);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            spans.Add(new WindowSpanRow(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? string.Empty : reader.GetString(2),
                reader.IsDBNull(3) ? string.Empty : reader.GetString(3),
                reader.GetInt64(4),
                reader.GetInt64(5)));
        }

        return spans;
    }

    /// <summary>Distinct windows seen during a day, with when each was first focused.</summary>
    public IReadOnlyList<(string AppName, string WindowTitle, long StartTs)> GetDistinctWindows(string day)
    {
        List<(string, string, long)> results = new();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT app_name, window_title, MIN(start_ts) AS first_seen
            FROM window_spans WHERE day = $day
            GROUP BY app_name, window_title ORDER BY first_seen;
            """;
        command.Parameters.AddWithValue("$day", day);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            results.Add((
                reader.IsDBNull(0) ? string.Empty : reader.GetString(0),
                reader.IsDBNull(1) ? string.Empty : reader.GetString(1),
                reader.GetInt64(2)));
        }

        return results;
    }
}
