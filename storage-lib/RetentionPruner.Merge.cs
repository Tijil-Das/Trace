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
                // Packs never carry per-tile timestamps — one constantly-appended file holds thousands of tiles of
                // mixed ages — so the grace rule cannot be applied tile by tile. The walk still needs a cutoff for
                // packs, though: a freshly written pack on a live session may hold tiles the flusher has not named
                // in a manifest yet, and collecting them now would delete tiles that are about to be referenced.
                // The pack's own mtime is the only clock available, and it answers conservatively: a live session
                // appends constantly, so its pack is always young and always skipped; only a sealed, untouched pack
                // is old enough to judge, and there every unreferenced tile in it is genuinely orphaned. Retention
                // aging in the test backdates that mtime past the cutoff.
                DateTime lastWrite = File.GetLastWriteTimeUtc(path);
                if (lastWrite > youngCutoff)
                {
                    continue; // too fresh to judge: a live session may still be writing its manifest
                }

                // Delete by hash, not by path: a packed tile's bytes live inside a shared pack, and removing the
                // record from the index is what makes it unreachable. The pack itself is reclaimed whole later by
                // ReclaimEmptyPacks once its last live tile goes (no compaction — see AssetPackStore).
                if (!assets.Delete(hash))
                {
                    continue;
                }

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

        int packsDeleted = 0;
        long packBytes = 0;
        try
        {
            (packsDeleted, packBytes) = assets.ReclaimEmptyPacks();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        return (deleted, bytes + packBytes);
    }

    private static long ToSqlHash(ulong hash) => unchecked((long)(hash ^ HashBias));
}
