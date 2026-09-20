using Microsoft.Data.Sqlite;

namespace ScreenRecall.Storage;

/// <summary>One focus span of a window, used purely for player navigation (spec 6).</summary>
public sealed record WindowSpanRow(long Id, string Day, string AppName, string WindowTitle, long StartTs, long EndTs);

/// <summary>Recorded day summary.</summary>
public sealed record SessionRow(string Day, long StartTs, long EndTs, string LogPath);

/// <summary>Checkpoint index row.</summary>
public sealed record CheckpointRow(string Day, long Ts, string Path);

/// <summary>
/// SQLite navigation index (spec 6, WAL mode). This layer only ever stores boundaries — which days
/// exist, when a window had focus, where the checkpoints are — so the player can jump around. It is
/// not a usage-analytics layer, exactly as a video keyframe index is not analytics.
/// </summary>
public sealed partial class RecallIndex : IDisposable
{
    private readonly SqliteConnection _connection;
    private bool _disposed;

    public RecallIndex(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
        }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("PRAGMA busy_timeout=5000;");
        EnsureIncrementalAutoVacuum();
        CreateSchema();
        DbPath = dbPath;
    }

    /// <summary>Path of the index database.</summary>
    public string DbPath { get; }

    /// <summary>Creates tables when they do not exist.</summary>
    public void CreateSchema()
    {
        Execute("""
                CREATE TABLE IF NOT EXISTS sessions (
                  day TEXT PRIMARY KEY,
                  start_ts INTEGER,
                  end_ts INTEGER,
                  log_path TEXT
                );
                """);
        Execute("""
                CREATE TABLE IF NOT EXISTS window_spans (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  day TEXT,
                  app_name TEXT,
                  window_title TEXT,
                  start_ts INTEGER,
                  end_ts INTEGER
                );
                """);
        Execute("CREATE INDEX IF NOT EXISTS ix_window_spans_day ON window_spans(day, start_ts);");
        Execute("""
                CREATE TABLE IF NOT EXISTS checkpoints (
                  day TEXT,
                  ts INTEGER,
                  path TEXT
                );
                """);
        Execute("CREATE INDEX IF NOT EXISTS ix_checkpoints_day ON checkpoints(day, ts);");
    }

    /// <summary>Inserts or updates the summary row for a day.</summary>
    public void UpsertSession(string day, long startTs, long endTs, string logPath)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sessions (day, start_ts, end_ts, log_path) VALUES ($day, $start, $end, $path)
            ON CONFLICT(day) DO UPDATE SET
              start_ts = MIN(sessions.start_ts, excluded.start_ts),
              end_ts   = MAX(sessions.end_ts, excluded.end_ts),
              log_path = excluded.log_path;
            """;
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$start", startTs);
        command.Parameters.AddWithValue("$end", endTs);
        command.Parameters.AddWithValue("$path", logPath);
        command.ExecuteNonQuery();
    }

    /// <summary>Registers a checkpoint file.</summary>
    public void InsertCheckpoint(string day, long ts, string path)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = "INSERT INTO checkpoints (day, ts, path) VALUES ($day, $ts, $path);";
        command.Parameters.AddWithValue("$day", day);
        command.Parameters.AddWithValue("$ts", ts);
        command.Parameters.AddWithValue("$path", path);
        command.ExecuteNonQuery();
    }

    /// <summary>Drops every row belonging to a day (retention pruning).</summary>
    public void DeleteDay(string day)
    {
        foreach (string table in new[] { "window_spans", "checkpoints", "sessions" })
        {
            using SqliteCommand command = _connection.CreateCommand();
            command.CommandText = $"DELETE FROM {table} WHERE day = $day;";
            command.Parameters.AddWithValue("$day", day);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _connection.Dispose();
    }

    /// <summary>
    /// Makes SQLite hand pages freed by DELETEs back to the file system. Without it the index only ever
    /// grows: retention pruning and panic purges delete rows, but the file keeps their pages, so a store
    /// recording for months carries every row it has ever deleted.
    /// </summary>
    private void EnsureIncrementalAutoVacuum()
    {
        if (ScalarLong("PRAGMA auto_vacuum;") == 2)
        {
            return;
        }

        // The pragma on its own is silently ignored for any database that already has a header — which
        // includes a brand-new file once a journal mode has been set on it — so the file is rebuilt with
        // VACUUM immediately after. This layer stores boundaries rather than pixel data, so the rebuild is
        // cheap. Both statements were verified against SQLite directly: auto_vacuum then reads 2
        // (incremental) from a live and from a fresh connection, and pages freed by later deletes are
        // returned to the file system by PRAGMA incremental_vacuum.
        Execute("PRAGMA auto_vacuum=INCREMENTAL;");
        Execute("VACUUM;");
    }

    private long ScalarLong(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? 0 : Convert.ToInt64(value);
    }

    private void Execute(string sql)
    {
        using SqliteCommand command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
