using Microsoft.Data.Sqlite;

namespace ScreenRecall.Storage;

public sealed partial class RecallIndex
{
    /// <summary>Summary row of a day, when indexed.</summary>
    public SessionRow? GetSession(string day)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT day, start_ts, end_ts, log_path FROM sessions WHERE day = $day;";
        command.Parameters.AddWithValue("$day", day);
        using SqliteDataReader reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new SessionRow(
            reader.GetString(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.IsDBNull(3) ? string.Empty : reader.GetString(3));
    }

    /// <summary>Checkpoints of a day at or before a timestamp, newest first.</summary>
    public IReadOnlyList<CheckpointRow> GetCheckpoints(string day, long atOrBeforeTs)
    {
        List<CheckpointRow> rows = new();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            SELECT day, ts, path FROM checkpoints
            WHERE day = $day AND ts <= $ts ORDER BY ts DESC;
            """;
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$ts", atOrBeforeTs);
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            rows.Add(new CheckpointRow(reader.GetString(0), reader.GetInt64(1), reader.GetString(2)));
        }

        return rows;
    }

    /// <summary>Days present in the index, oldest first.</summary>
    public IReadOnlyList<string> GetDays()
    {
        List<string> days = new();
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT day FROM sessions ORDER BY day;";
        using SqliteDataReader reader = command.ExecuteReader();
        while (reader.Read())
        {
            days.Add(reader.GetString(0));
        }

        return days;
    }

    /// <summary>Recorded focus time of a day, summed from its boundary spans.</summary>
    public long TotalSpanMs(string day)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(SUM(end_ts - start_ts), 0) FROM window_spans WHERE day = $day;";
        command.Parameters.AddWithValue("$day", day);
        object? result = command.ExecuteScalar();
        return result is null or DBNull ? 0 : Convert.ToInt64(result);
    }
}
