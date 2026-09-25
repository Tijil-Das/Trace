using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

internal sealed partial class CaptureEngine
{
    /// <summary>
    /// Smallest movement worth a log entry while the pointer keeps moving. A mouse sweeping the screen produces
    /// presents at the compositor's rate (60/s and up), and one 27-byte entry per present would be megabytes a day
    /// of nothing but cursor; 2 px is below what a viewer can see at these speeds, so the cursor in the recording
    /// follows the pointer closely while the log stays a rounding error next to the tiles.
    /// </summary>
    private const int PointerMoveThresholdPx = 2;

    /// <summary>
    /// Shortest spacing between two pointer entries while the pointer keeps moving (20 positions a second). A shape
    /// change or a visibility change is never delayed by it: those are what make a cursor wrong rather than merely
    /// a few milliseconds late.
    /// </summary>
    private const long PointerIntervalUs = 50_000;

    /// <summary>
    /// Records where the mouse pointer was.
    /// </summary>
    /// <remarks>
    /// The duplicated surface never contains the pointer — Windows draws it above the desktop and reports it as
    /// metadata — so without this the recording has no cursor in it at all, and every reconstructed frame is a frame
    /// nobody saw. It is also the one change DXGI reports without any dirty rects, which is why this is called for
    /// every frame the recorder is willing to look at, including frames with nothing else in them.
    ///
    /// The cost is kept at nothing by three rules: an entry is written only when the pointer moved more than
    /// <see cref="PointerMoveThresholdPx"/>, changed shape or changed visibility; the rate is capped by
    /// <see cref="PointerIntervalUs"/>; and the shape itself is stored once, through the same codec and queue as a
    /// tile, so a cursor that comes back (arrow → I-beam → arrow) costs one asset for the whole day.
    ///
    /// <paramref name="force"/> is used right after a checkpoint: a checkpoint cannot carry the pointer, so the
    /// state is re-stated there and a seek from it knows where the cursor was.
    /// </remarks>
    private void RecordPointer(SourceFrame frame, long timestampUs, bool force)
    {
        if (_log is null || (!force && !frame.HasPointerUpdate))
        {
            return;
        }

        PointerShape? shape = frame.PointerShape;
        bool shapeChanged = shape is not null && shape.Hash != _lastPointerShapeHash;
        bool visibilityChanged = frame.PointerVisible != _lastPointerVisible;
        bool moved = Math.Abs(frame.PointerX - _lastPointerX) >= PointerMoveThresholdPx
                     || Math.Abs(frame.PointerY - _lastPointerY) >= PointerMoveThresholdPx;

        if (!force && !shapeChanged && !visibilityChanged && !moved)
        {
            return;
        }

        if (!force && !shapeChanged && !visibilityChanged && timestampUs - _lastPointerUs < PointerIntervalUs)
        {
            return;
        }

        if (shape is not null)
        {
            _lastPointerShapeHash = shape.Hash;
            _lastPointerHotspotX = shape.HotspotX;
            _lastPointerHotspotY = shape.HotspotY;
            StorePointerShape(shape);
        }

        // A zero hash is the log's way of writing "the pointer was not visible": there is nothing to draw.
        ulong shapeHash = frame.PointerVisible ? _lastPointerShapeHash : 0;
        _log.Append(LogEntry.Pointer(
            timestampUs,
            frame.Monitor.Id,
            frame.PointerX,
            frame.PointerY,
            _lastPointerHotspotX,
            _lastPointerHotspotY,
            shapeHash));
        _stats.LogEntriesWritten++;

        _lastPointerX = frame.PointerX;
        _lastPointerY = frame.PointerY;
        _lastPointerVisible = frame.PointerVisible;
        _lastPointerUs = timestampUs;
    }

    /// <summary>
    /// Stores a pointer shape once, exactly like a tile: same codec, same background writer, same manifest, so
    /// retention keeps it alive for as long as the day that references it. It counts as a stored asset — a cursor
    /// shape is a payload like any other, and a day holds a handful of them.
    /// </summary>
    private void StorePointerShape(PointerShape shape)
    {
        if (_dedupe.Contains(shape.Hash) || _session.Assets.Contains(shape.Hash))
        {
            _dedupe.Add(shape.Hash);
            return;
        }

        byte[] payload = _codec.Encode(shape.Bgra, shape.Width, shape.Height);
        _assetWriter.Enqueue(shape.Hash, _codec.Id, shape.Width, shape.Height, payload);
        _manifest.Add(shape.Hash);
        _dedupe.Add(shape.Hash);
        _stats.TilesStored++;
    }
}
