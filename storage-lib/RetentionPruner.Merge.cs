using Microsoft.Data.Sqlite;

namespace ScreenRecall.Storage;

public sealed partial class RetentionPruner
{
    /// <summary>SQLite INTEGER is signed; flipping the sign bit makes signed order equal unsigned hash order.</summary>
    private const ulong HashBias = 0x8000000000000000UL;

    /// <summary>
    /// Single merge-walk over the hash-sorted referenced set and the hash-sorted asset listing:
    /// linear, allocation-free per asset, and safe to run while capture continues.
    /// </summary>
    private (int Deleted, long Bytes) MergeDelete(SqliteConnection scratch, DateTime youngCutoff)
    {
        AssetStore assets = new(SessionLayout.AssetsRoot(_root));
        int deleted = 0;
        long bytes = 0;

        using SqliteCommand command = scratch.CreateCommand();
        command.CommandText = "SELECT hash FROM used ORDER BY hash;";
        using SqliteDataReader reader = command.ExecuteReader();

        bool hasReferenced = reader.Read();
        long current = hasReferenced ? reader.GetInt64(0) : 0;

        foreach ((ulong hash, string path, long size) in assets.EnumerateFilesSorted())
        {
            long key = ToSqlHash(hash);
            while (hasReferenced && current < key)
            {
                hasReferenced = reader.Read();
                current = hasReferenced ? reader.GetInt64(0) : 0;
            }

            if (hasReferenced && current == key)
            {
                continue; // still referenced by a retained day
            }

            try
            {
                if (File.GetLastWriteTimeUtc(path) > youngCutoff)
                {
                    continue; // too fresh to judge: a live session may still be writing its manifest
                }

                File.Delete(path);
                deleted++;
                bytes += size;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return (deleted, bytes);
    }

    private static long ToSqlHash(ulong hash) => unchecked((long)(hash ^ HashBias));
}
