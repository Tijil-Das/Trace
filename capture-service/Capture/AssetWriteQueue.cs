using System.Collections.Concurrent;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// Writes new tiles to the content-addressable store on a dedicated low-priority thread. Small-file
/// creation is the most expensive step on the steady-state path — measured at ~5 ms per new asset on a
/// machine with real-time scanning enabled, versus ~90 µs to compress a tile — so the capture thread
/// hands payloads over and never waits for the disk. The queue is bounded, which gives natural
/// backpressure if the volume cannot keep up instead of growing memory without limit.
/// </summary>
internal sealed class AssetWriteQueue : IDisposable
{
    private readonly BlockingCollection<WriteRequest> _queue;
    private readonly Thread _worker;
    private readonly CancellationTokenSource _cts = new();
    private readonly AssetStore _store;
    private long _written;
    private long _deduped;
    private long _bytes;
    private bool _disposed;

    internal AssetWriteQueue(AssetStore store, int capacity = 8192)
    {
        _store = store;
        _queue = new BlockingCollection<WriteRequest>(Math.Max(256, capacity));
        _worker = new Thread(ProcessLoop)
        {
            Name = "screenrecall-asset-writer",
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
        };
        _worker.Start();
    }

    /// <summary>Assets actually written to disk by this queue.</summary>
    internal long WrittenCount => Interlocked.Read(ref _written);

    /// <summary>Payloads that turned out to already exist (raced with another writer).</summary>
    internal long DedupedCount => Interlocked.Read(ref _deduped);

    /// <summary>Payload bytes written.</summary>
    internal long BytesWritten => Interlocked.Read(ref _bytes);

    /// <summary>Payloads waiting to be written.</summary>
    internal int PendingCount => _queue.Count;

    /// <summary>Most recent write error, if any.</summary>
    internal string? LastError { get; private set; }

    /// <summary>Hands a payload to the writer. Blocks only when the queue is saturated.</summary>
    internal void Enqueue(ulong hash, byte codecId, int width, int height, byte[] payload)
    {
        if (_disposed)
        {
            return;
        }

        _queue.Add(new WriteRequest(hash, codecId, width, height, payload));
    }

    /// <summary>Waits until everything enqueued so far has reached the disk.</summary>
    internal bool Drain(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (_queue.Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(2);
        }

        return _queue.Count == 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            _queue.CompleteAdding();
            _worker.Join(TimeSpan.FromSeconds(10));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ThreadStateException)
        {
            LastError = ex.Message;
        }

        _cts.Dispose();
        _queue.Dispose();
    }

    private void ProcessLoop()
    {
        try
        {
            foreach (WriteRequest request in _queue.GetConsumingEnumerable(_cts.Token))
            {
                try
                {
                    if (_store.Store(request.Hash, request.CodecId, request.Width, request.Height, request.Payload, assumeMissing: true))
                    {
                        Interlocked.Increment(ref _written);
                        Interlocked.Add(ref _bytes, request.Payload.LongLength);
                    }
                    else
                    {
                        Interlocked.Increment(ref _deduped);
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    LastError = ex.Message;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _queue.CompleteAdding();
        }
    }

    private readonly record struct WriteRequest(ulong Hash, byte CodecId, int Width, int Height, byte[] Payload);
}
