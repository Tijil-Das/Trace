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

    /// <summary>
    /// Signalled when nothing is queued. Drain waits on this instead of polling: a flush used to spin on
    /// <c>Thread.Sleep(1)</c> for as long as the writer was behind, which on a saturated store was seconds of a
    /// core doing nothing but checking a counter.
    ///
    /// A kernel event rather than <c>ManualResetEventSlim</c>, and that is a measured choice too: the slim
    /// construct **spin-waits before it blocks**, and the drain loop calls it repeatedly, so every re-check paid
    /// the spin again. A 30 s <c>dotnet-trace</c> put <c>ManualResetEventSlim.Wait</c> + <c>SpinWait.SpinOnceCore</c>
    /// at 21% of capture-thread samples while the thread was doing no work at all
    /// (<c>docs/PERFORMANCE.md</c> §5a). <c>ManualResetEvent.WaitOne</c> goes straight to the kernel: no spin, and
    /// it wakes on <c>Set</c> immediately rather than at the end of a poll slice.
    ///
    /// HISTORY — do not re-litigate without the numbers below. This was reverted once when the fidelity suite
    /// started failing, then re-landed when the numbers exonerated it: with the slim wait, same binary class,
    /// the isolated fidelity test went 96.5% → 39.3% across two consecutive runs, and 96.5% → 0% before that;
    /// the runs differ only in machine load, not in which event was compiled in. Whatever is flaking in the
    /// fidelity path, it is above this layer (see the Clone/pacing note in RecallConfigTests). If this is ever
    /// doubted again, the decisive experiment is five back-to-back isolated fidelity runs on a quiet machine —
    /// a single failure proves a race, and its percentage points at *where*: 0% is empty frames (nothing
    /// written), ~40-80% is torn frames (the writer lost the ordering race), and only a stable intermediate
    /// number would implicate the wait itself.
    /// </summary>
    private readonly ManualResetEvent _idle = new(true);

    /// <summary>Upper bound on one wait, so a missed signal can delay a drain but can never hang it.</summary>
    private const int WaitSliceMs = 250;
    private long _written;
    private long _deduped;
    private long _bytes;
    private long _pending;
    private bool _disposed;

    internal AssetWriteQueue(AssetStore store, int capacity = 2048)
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

    /// <summary>Payloads handed to the writer but not yet written to disk.</summary>
    internal long PendingCount => Interlocked.Read(ref _pending);

    /// <summary>Queue capacity. Also the ceiling the flush policy measures a backlog against.</summary>
    internal int Capacity => _queue.BoundedCapacity;

    /// <summary>Most recent write error, if any.</summary>
    internal string? LastError { get; private set; }

    /// <summary>Hands a payload to the writer. Blocks only when the queue is saturated.</summary>
    internal void Enqueue(ulong hash, byte codecId, int width, int height, byte[] payload)
    {
        if (_disposed)
        {
            return;
        }

        Interlocked.Increment(ref _pending);
        _idle.Reset();
        _queue.Add(new WriteRequest(hash, codecId, width, height, payload));
    }

    /// <summary>
    /// Blocks until every payload handed over so far is on disk. Callers that are about to flush the
    /// reference log use this first: a log entry must never become durable before the tile it points
    /// at, which is what keeps the store crash-consistent.
    /// </summary>
    internal void Drain() => WaitForEmpty(TimeSpan.MaxValue);

    /// <summary>Drains with a bound, for callers that must not block indefinitely (shutdown paths).</summary>
    internal bool TryDrain(TimeSpan timeout) => WaitForEmpty(timeout);

    /// <summary>
    /// Waits for the queue to empty by blocking on the writer's signal rather than polling it, re-checking the
    /// count after every wake so a signal that arrives early (an enqueue racing the last write) cannot end the wait
    /// while work is still outstanding.
    /// </summary>
    private bool WaitForEmpty(TimeSpan timeout)
    {
        bool bounded = timeout != TimeSpan.MaxValue;
        DateTime deadline = bounded ? DateTime.UtcNow + timeout : DateTime.MaxValue;

        while (Interlocked.Read(ref _pending) > 0)
        {
            if (bounded && DateTime.UtcNow >= deadline)
            {
                break;
            }

            TimeSpan remaining = bounded ? deadline - DateTime.UtcNow : TimeSpan.MaxValue;
            int slice = remaining == TimeSpan.MaxValue || remaining.TotalMilliseconds > WaitSliceMs
                ? WaitSliceMs
                : Math.Max(1, (int)remaining.TotalMilliseconds);

            // WaitOne, not a poll: the writer Sets this event the moment the count reaches zero, so a healthy
            // drain wakes in microseconds. A missed Set still ends the wait at the slice.
            _idle.WaitOne(slice);
        }

        return Interlocked.Read(ref _pending) == 0;
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

        _idle.Set(); // release anything waiting for the queue to empty; it will find the writer stopped
        _cts.Dispose();

        // The queue is only disposed once its consumer has actually stopped: disposing it underneath a
        // running worker throws ObjectDisposedException on the worker thread, which would fault the
        // process during shutdown.
        if (!_worker.IsAlive)
        {
            _queue.Dispose();
            _idle.Dispose();
        }
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
                finally
                {
                    if (Interlocked.Decrement(ref _pending) <= 0)
                    {
                        // Wake every drain: the queue is empty now, which is the only thing they wait for.
                        _idle.Set();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
            // Disposed during shutdown; nothing left to drain.
        }
    }

    private readonly record struct WriteRequest(ulong Hash, byte CodecId, int Width, int Height, byte[] Payload);
}
