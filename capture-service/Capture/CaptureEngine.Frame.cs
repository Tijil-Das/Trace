using System.Diagnostics;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    /// <summary>
    /// Turns one acquired frame into log entries. Returns true when the frame carried changes worth
    /// timing. Privacy and DRM skips happen here, before a single pixel is read (spec 5.7 / 13).
    /// </summary>
    private bool ProcessFrame(SourceFrame frame)
    {
        ForegroundWindowInfo foreground = _foreground.Poll();
        _stats.ForegroundApp = foreground.Display;
        _stats.Excluded = foreground.Excluded;

        if (foreground.Excluded)
        {
            // A denylisted window is in focus: record nothing at all, then rescan fully once it goes.
            _stats.FramesSkippedExcluded++;
            _forceFullRescan = true;
            UpdateWindowSpans(foreground, recordSpans: false);
            return false;
        }

        if (frame.ProtectedContent)
        {
            // HDCP-protected content is blanked by the compositor, so the frame is not the truth.
            _stats.FramesSkippedProtected++;
            _forceFullRescan = true;
            return false;
        }

        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        EnsureSessionFor(DateOnly.FromDateTime(DateTimeOffset.Now.LocalDateTime));

        ulong[] canvas = GetCanvas(frame.Monitor);
        _stats.LastDirtyRects = frame.DirtyRects.Count;
        _stats.LastMoveRects = frame.MoveRects.Count;

        // A deferred rescan is an owed rescan, never a dropped one. Until it is honoured the canvas
        // cannot be trusted, so no frame is processed (the screen would look settled while it is not),
        // and once the rate limit allows, the very next frame is rescanned in full even if DXGI
        // reported a handful of dirty rects.
        if (_owedRescan && nowMs - _lastFullRescanMs < FullRescanMinIntervalMs)
        {
            _stats.FramesRescanDeferred++;
            return false;
        }

        _cells.Clear();
        bool fullRescan;
        if (_forceFullRescan || _owedRescan)
        {
            fullRescan = true;
            _owedRescan = false;
        }
        else if (frame.FullRescan)
        {
            // Presented with no region list at all: cursor or layered-overlay driven. Rescan in full,
            // but rate limit it so a pointer-only present cannot pin the CPU.
            if (nowMs - _lastFullRescanMs < FullRescanMinIntervalMs)
            {
                _owedRescan = true;
                _stats.FramesRescanDeferred++;
                return false;
            }

            fullRescan = true;
        }
        else if (frame.DirtyRects.Count == 0 && frame.MoveRects.Count == 0)
        {
            _stats.FramesSkippedNoChange++;
            return false;
        }
        else
        {
            fullRescan = false;
        }

        _forceFullRescan = false;

        if (fullRescan)
        {
            _cells.AddAll(frame.Monitor);
            _stats.FullFrameRescans++;
            _lastFullRescanMs = nowMs;
        }
        else
        {
            foreach (IntRect rect in frame.DirtyRects)
            {
                _cells.AddRect(frame.Monitor, rect);
            }

            foreach (IntRect rect in frame.MoveRects)
            {
                _cells.AddRect(frame.Monitor, rect);
            }
        }

        long timestampUs = nowMs * 1000;
        int changes = ProcessTiles(frame, canvas, foreground.Id, timestampUs);
        _stats.LastFrameUtc = DateTimeOffset.UtcNow;

        if (_config.CaptureGroundTruth && nowMs - _lastGroundTruthMs >= 1000)
        {
            // Ground-truth frames are 4 MB apiece, so the harness samples about one per second
            // instead of one per frame — plenty to prove reconstruction fidelity (spec 12).
            _lastGroundTruthMs = nowMs;
            WriteGroundTruth(frame, timestampUs);
        }

        UpdateWindowSpans(foreground, recordSpans: true);

        if (timestampUs - _lastCheckpointUs >= _config.CheckpointSeconds * 1_000_000L)
        {
            WriteCheckpoint(timestampUs, force: false);
        }

        return changes > 0 || frame.HasChanges;
    }

    /// <summary>
    /// Hashes every touched tile, stores the ones that are new, and logs only the tiles whose content
    /// actually differs from the canvas. Dirty rects are conservative — they routinely cover tiles
    /// whose pixels never changed — so this second-level comparison is what keeps the log small.
    /// </summary>
    private int ProcessTiles(SourceFrame frame, ulong[] canvas, uint windowId, long timestampUs)
    {
        MonitorInfo monitor = frame.Monitor;
        int changes = 0;
        Span<byte> scratch = _tileScratch;
        double hashMs = 0;
        double encodeMs = 0;
        double storeMs = 0;
        double logMs = 0;

        foreach (long packed in _cells.Packed)
        {
            (int cellX, int cellY) = TileCellSet.Unpack(packed);
            int index = monitor.TileIndex(cellX, cellY);
            if (index < 0 || index >= canvas.Length)
            {
                continue;
            }

            monitor.TilePixelRect(cellX, cellY, out int x, out int y, out int width, out int height);
            if (width <= 0 || height <= 0)
            {
                continue;
            }

            long phase = Stopwatch.GetTimestamp();
            CopyTile(frame, x, y, width, height, scratch);
            int length = width * height * 4;
            ulong hash = TileHash.Compute(scratch[..length], width, height);
            hashMs += Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
            _stats.TilesHashed++;

            if (canvas[index] == hash)
            {
                continue;
            }

            if (!_dedupe.Contains(hash) && !_session.Assets.Contains(hash))
            {
                phase = Stopwatch.GetTimestamp();
                byte[] payload = _codec.Encode(scratch[..length], width, height);
                encodeMs += Stopwatch.GetElapsedTime(phase).TotalMilliseconds;

                phase = Stopwatch.GetTimestamp();
                _assetWriter.Enqueue(hash, _codec.Id, width, height, payload);
                storeMs += Stopwatch.GetElapsedTime(phase).TotalMilliseconds;
                _stats.TilesStored++;
            }
            else
            {
                _stats.TilesDeduped++;
            }

            _dedupe.Add(hash);

            phase = Stopwatch.GetTimestamp();
            _log.Append(LogEntry.Draw(timestampUs, windowId, monitor.Id, cellX, cellY, hash));
            _manifest.Add(hash);
            logMs += Stopwatch.GetElapsedTime(phase).TotalMilliseconds;

            _stats.LogEntriesWritten++;
            canvas[index] = hash;
            changes++;
        }

        _stats.LastReadbackMs = Math.Round(frame.ReadbackMs, 2);
        _stats.LastHashMs = Math.Round(hashMs, 2);
        _stats.LastEncodeMs = Math.Round(encodeMs, 2);
        _stats.LastStoreMs = Math.Round(storeMs, 2);
        _stats.LastLogMs = Math.Round(logMs, 2);
        _stats.AssetsBytesWritten = _assetWriter.BytesWritten;
        _stats.AssetQueueDepth = _assetWriter.PendingCount;
        return changes;
    }
}
