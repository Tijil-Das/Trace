namespace ScreenRecall.Storage;

public sealed partial class RetentionPruner
{
    /// <summary>
    /// Panic purge (spec 8): drop everything recorded in the last <paramref name="window"/> and
    /// reclaim the assets that only those records referenced. The capture service must be paused
    /// first — it owns the appending writer — which the IPC `purgeRecent` command does for callers.
    /// </summary>
    public PruneReport PurgeRecent(TimeSpan window, DateTimeOffset now)
    {
        return PurgeRange(new[] { now - window, now });
    }

    /// <summary>Drops every record in an inclusive time range and reclaims orphaned assets.</summary>
    public PruneReport PurgeRange(IReadOnlyList<DateTimeOffset> range)
    {
        DateTimeOffset from = range[0];
        DateTimeOffset to = range[1];
        long fromUs = from.ToUnixTimeMilliseconds() * 1000;
        long toUs = to.ToUnixTimeMilliseconds() * 1000;

        List<DateOnly> days = SessionLayout.ListDays(_root)
            .Where(day => day >= DateOnly.FromDateTime(from.LocalDateTime).AddDays(-1))
            .ToList();

        using RecallIndex? index = TryOpenIndex();
        long removedBytes = 0;
        long removedEntries = 0;
        List<DateOnly> touched = new();

        foreach (DateOnly day in days)
        {
            string sessionDir = SessionLayout.SessionDir(_root, day);
            foreach (string segment in SessionLogFormat.FilesFor(sessionDir))
            {
                (long removed, long bytes) = RewriteLogWithoutRange(segment, fromUs, toUs);
                removedEntries += removed;
                removedBytes += bytes;
                if (removed > 0)
                {
                    touched.Add(day);
                }
            }

            foreach (string checkpoint in Directory.Exists(SessionLayout.CheckpointDir(_root, day))
                         ? Directory.EnumerateFiles(SessionLayout.CheckpointDir(_root, day), "*.ckpt").ToArray()
                         : Array.Empty<string>())
            {
                if (CheckpointFormat.TryParseFileName(checkpoint, out long ts) && ts >= fromUs && ts <= toUs)
                {
                    removedBytes += new FileInfo(checkpoint).Length;
                    File.Delete(checkpoint);
                }
            }

            foreach (string frame in Directory.Exists(SessionLayout.GroundTruthDir(_root, day))
                         ? Directory.EnumerateFiles(SessionLayout.GroundTruthDir(_root, day), "*.raw").ToArray()
                         : Array.Empty<string>())
            {
                if (GroundTruthFrame.TryParseTimestamp(frame, out long ts) && ts >= fromUs && ts <= toUs)
                {
                    removedBytes += new FileInfo(frame).Length;
                    File.Delete(frame);
                }
            }

            index?.TrimHistoryAfter(SessionLayout.DayName(day), fromUs / 1000);
        }

        (int assetsDeleted, long assetBytes) = CollectGarbage(DateTimeOffset.Now, TimeSpan.FromMinutes(2));
        return new PruneReport(touched.Distinct().Count(), removedBytes, assetsDeleted, assetBytes, touched.Distinct().ToArray());
    }

    /// <summary>Rewrites a log segment without the entries inside the range; returns (entries removed, bytes freed).</summary>
    private static (long RemovedEntries, long BytesFreed) RewriteLogWithoutRange(string segmentPath, long fromUs, long toUs)
    {
        long fileLength = new FileInfo(segmentPath).Length;
        string temp = segmentPath + ".purge";
        long removed = 0;

        using (SessionLogReader reader = new(segmentPath))
        {
            bool anyRemoved = false;
            using FileStream output = new(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
            output.Write(SessionLogFormat.CreateHeaderBytes(DateTimeOffset.FromUnixTimeMilliseconds(reader.Header?.DayStartUnixMs ?? 0)));

            byte[] buffer = new byte[LogEntry.Size * 4096];
            int buffered = 0;
            while (reader.TryReadNext(out LogEntry entry))
            {
                if (entry.TimestampUs >= fromUs && entry.TimestampUs <= toUs)
                {
                    removed++;
                    anyRemoved = true;
                    continue;
                }

                LogEntry.Write(buffer.AsSpan(buffered), entry);
                buffered += LogEntry.Size;
                if (buffered == buffer.Length)
                {
                    output.Write(buffer, 0, buffered);
                    buffered = 0;
                }
            }

            if (buffered > 0)
            {
                output.Write(buffer, 0, buffered);
            }

            output.Flush();

            if (!anyRemoved)
            {
                output.Dispose();
                TryDeleteFile(temp);
                return (0, 0);
            }
        }

        File.Move(temp, segmentPath, overwrite: true);
        long newLength = new FileInfo(segmentPath).Length;
        return (removed, Math.Max(0, fileLength - newLength));
    }
}
