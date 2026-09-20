using Microsoft.Data.Sqlite;

namespace ScreenRecall.Storage;

public sealed partial class RetentionPruner
{
    /// <summary>
    /// Deletes assets no retained day references. Referenced hashes are gathered from the per-day
    /// manifests (falling back to a log scan for days without one) into a disposable scratch database
    /// so memory stays flat no matter how many days are retained, then merged against the
    /// hash-sorted asset listing. Assets younger than <paramref name="grace"/> are always kept: a live
    /// capture session may not have flushed its manifest yet.
    /// </summary>
    public (int Deleted, long Bytes) CollectGarbage(DateTimeOffset now, TimeSpan? grace = null)
    {
        TimeSpan gracePeriod = grace ?? DefaultAssetGrace;
        string scratchPath = Path.Combine(_root, $"gc-{Guid.NewGuid():n}.db");
        DateTime youngCutoff = (now - gracePeriod).UtcDateTime;

        try
        {
            using SqliteConnection scratch = new(new SqliteConnectionStringBuilder
            {
                DataSource = scratchPath,
                Mode = SqliteOpenMode.ReadWriteCreate,
            }.ToString());
            scratch.Open();
            ExecSql(scratch, "PRAGMA synchronous=OFF;");
            ExecSql(scratch, "PRAGMA journal_mode=OFF;");
            ExecSql(scratch, "CREATE TABLE used (hash INTEGER PRIMARY KEY);");

            using (SqliteTransaction transaction = scratch.BeginTransaction())
            {
                using SqliteCommand insert = scratch.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText = "INSERT OR IGNORE INTO used (hash) VALUES ($h);";
                SqliteParameter parameter = insert.Parameters.Add("$h", SqliteType.Integer);

                foreach (DateOnly day in SessionLayout.ListDays(_root))
                {
                    AddDayReferences(insert, parameter, day);
                }

                transaction.Commit();
            }

            return MergeDelete(scratch, youngCutoff);
        }
        finally
        {
            TryDeleteFile(scratchPath);
        }
    }

    /// <summary>Feeds one day's manifests, checkpoints (and, if needed, its log) into the scratch table.</summary>
    private void AddDayReferences(SqliteCommand insert, SqliteParameter parameter, DateOnly day)
    {
        string sessionDir = SessionLayout.SessionDir(_root, day);
        IReadOnlyList<string> manifests = AssetManifest.FilesFor(sessionDir);

        if (manifests.Count == 0)
        {
            // Pre-manifest or interrupted session: read the log itself so nothing live is collected.
            foreach (string segment in SessionLogFormat.FilesFor(sessionDir))
            {
                foreach (ulong hash in SessionLogReader.CollectHashes(segment))
                {
                    parameter.Value = ToSqlHash(hash);
                    insert.ExecuteNonQuery();
                }
            }
        }
        else
        {
            foreach (string manifest in manifests)
            {
                foreach (ulong hash in AssetManifest.ReadHashes(manifest))
                {
                    parameter.Value = ToSqlHash(hash);
                    insert.ExecuteNonQuery();
                }
            }
        }

        string checkpointDir = SessionLayout.CheckpointDir(_root, day);
        if (!Directory.Exists(checkpointDir))
        {
            return;
        }

        foreach (string checkpoint in Directory.EnumerateFiles(checkpointDir, "*.ckpt"))
        {
            try
            {
                CheckpointData data = CheckpointFormat.Read(checkpoint);
                foreach (CheckpointMonitorState state in data.Monitors)
                {
                    foreach (ulong hash in state.Tiles)
                    {
                        if (hash == TileHash.None)
                        {
                            continue;
                        }

                        parameter.Value = ToSqlHash(hash);
                        insert.ExecuteNonQuery();
                    }
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException)
            {
                // A damaged checkpoint must not block pruning.
            }
        }
    }

    private static void ExecSql(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
    }
}
