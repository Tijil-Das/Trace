namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// Test-only ground-truth dumps, kept under a hard cap. They exist so the fidelity harness can compare
/// reconstruction against truth, and they are *dumps*: at 4 MB per frame a 24-hour session would write
/// hundreds of gigabytes if nothing trimmed them. The store keeps a rolling window instead, deleting the
/// oldest files first, and enforces a byte ceiling on top of the frame count.
/// </summary>
internal static class GroundTruthStore
{
    /// <summary>Frames kept per store (~2 minutes at one dump per second).</summary>
    internal const int DefaultMaxFrames = 120;

    /// <summary>Bytes the ground-truth folder may occupy before older dumps are dropped.</summary>
    internal const long DefaultMaxBytes = 512L * 1024 * 1024;

    /// <summary>
    /// Trims a session's (and, for safety, the previous day's) ground-truth folder to the given caps.
    /// Files are named with their timestamp, so name order is chronological order.
    /// </summary>
    internal static (int Deleted, long BytesFreed) Trim(
        string groundTruthDirectory,
        int maxFrames = DefaultMaxFrames,
        long maxBytes = DefaultMaxBytes)
    {
        if (!Directory.Exists(groundTruthDirectory))
        {
            return (0, 0);
        }

        List<(string Path, long Bytes, long Timestamp)> files = new();
        long totalBytes = 0;
        foreach (string file in Directory.EnumerateFiles(groundTruthDirectory, "*.raw"))
        {
            long size;
            try
            {
                size = new FileInfo(file).Length;
            }
            catch (IOException)
            {
                continue;
            }

            long timestamp = ScreenRecall.Storage.GroundTruthFrame.TryParseTimestamp(file, out long parsed) ? parsed : 0;
            files.Add((file, size, timestamp));
            totalBytes += size;
        }

        if (files.Count <= maxFrames && totalBytes <= maxBytes)
        {
            return (0, 0);
        }

        files.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        int deleteCount = Math.Max(0, files.Count - Math.Max(1, maxFrames));
        int deleted = 0;
        long freed = 0;

        for (int i = 0; i < files.Count; i++)
        {
            bool overFrames = i < deleteCount;
            bool overBytes = totalBytes - freed > maxBytes;
            if (!overFrames && !overBytes)
            {
                break;
            }

            try
            {
                File.Delete(files[i].Path);
                deleted++;
                freed += files[i].Bytes;
            }
            catch (IOException)
            {
                // Another process is reading it (the harness): it will be trimmed next time.
            }
        }

        return (deleted, freed);
    }

    /// <summary>Removes ground-truth dumps for a session when test mode is off.</summary>
    internal static (int Deleted, long BytesFreed) Clear(string groundTruthDirectory)
    {
        if (!Directory.Exists(groundTruthDirectory))
        {
            return (0, 0);
        }

        int deleted = 0;
        long freed = 0;
        foreach (string file in Directory.EnumerateFiles(groundTruthDirectory, "*.raw"))
        {
            try
            {
                long size = new FileInfo(file).Length;
                File.Delete(file);
                deleted++;
                freed += size;
            }
            catch (IOException)
            {
            }
        }

        return (deleted, freed);
    }

    /// <summary>
    /// Clears dumps in *every* day folder of a store. Today's folder is the only one being written, but
    /// a previous run can leave dumps behind — restarting the day, switching test mode off, or a crash
    /// mid-run — and those files are bulk data that would otherwise sit on the disk forever. Deleting
    /// them is what keeps "test mode off" from meaning "previous test run's gigabytes still on disk".
    /// </summary>
    internal static (int Deleted, long BytesFreed) ClearAll(string storeRoot)
    {
        string sessions = ScreenRecall.Storage.SessionLayout.SessionsRoot(storeRoot);
        if (!Directory.Exists(sessions))
        {
            return (0, 0);
        }

        int deleted = 0;
        long freed = 0;
        foreach (string day in Directory.EnumerateDirectories(sessions))
        {
            string dumpDir = Path.Combine(day, ScreenRecall.Storage.SessionLayout.GroundTruthDirName);
            (int files, long bytes) = Clear(dumpDir);
            deleted += files;
            freed += bytes;

            if (files > 0)
            {
                // Try to remove the now-empty folder as well: an empty folder is harmless, but leaving
                // one behind for every day of the retention window is pure clutter.
                try
                {
                    if (!Directory.EnumerateFileSystemEntries(dumpDir).Any())
                    {
                        Directory.Delete(dumpDir);
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        return (deleted, freed);
    }
}
