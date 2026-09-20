using Microsoft.Data.Sqlite;

namespace ScreenRecall.Storage;

public sealed partial class RecallIndex
{
    /// <summary>
    /// Truncates the write-ahead log. WAL files grow with every transaction until a checkpoint runs, so a
    /// long session that updates window spans constantly would otherwise leave a large `index.db-wal`.
    /// Free pages left by deletes are returned to the file system in the same pass, which is what keeps
    /// the database itself from growing all day. Safe to call at any time; cheap when there is nothing
    /// to reclaim.
    /// </summary>
    public void CheckpointWal()
    {
        using (SqliteCommand command = _connection.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            command.ExecuteNonQuery();
        }

        // Bounded, incremental work (SQLite's own page budget), and a no-op while the free list is empty.
        Execute("PRAGMA incremental_vacuum;");
    }

    /// <summary>Removes checkpoints recorded at or after a timestamp (used by the panic purge).</summary>
    public int DeleteCheckpointsAfter(string day, long cutOffTs)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "DELETE FROM checkpoints WHERE day = $day AND ts >= $ts;";
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$ts", cutOffTs);
        return command.ExecuteNonQuery();
    }

    /// <summary>
    /// Drops focus spans recorded at or after a timestamp, trimming a span that straddles the cut
    /// instead of removing it outright (used by the panic purge).
    /// </summary>
    public void TrimHistoryAfter(string day, long cutOffTs)
    {
        using (SqliteCommand trim = _connection.CreateCommand())
        {
            trim.CommandText =
                "UPDATE window_spans SET end_ts = $ts WHERE day = $day AND start_ts < $ts AND end_ts > $ts;";
            trim.Parameters.AddWithValue("$day", day);
            trim.Parameters.AddWithValue("$ts", cutOffTs);
            trim.ExecuteNonQuery();
        }

        using SqliteCommand delete = _connection.CreateCommand();
        delete.CommandText = "DELETE FROM window_spans WHERE day = $day AND start_ts >= $ts;";
        delete.Parameters.AddWithValue("$day", day);
        delete.Parameters.AddWithValue("$ts", cutOffTs);
        delete.ExecuteNonQuery();

        using SqliteCommand session = _connection.CreateCommand();
        session.CommandText = "UPDATE sessions SET end_ts = $ts WHERE day = $day AND end_ts > $ts;";
        session.Parameters.AddWithValue("$day", day);
        session.Parameters.AddWithValue("$ts", cutOffTs);
        session.ExecuteNonQuery();
    }
}
