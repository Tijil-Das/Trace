using System.Buffers;

namespace ScreenRecall.Storage;

/// <summary>Aggregate facts about a log segment.</summary>
public sealed record SessionLogStats(
    long EntryCount,
    long FirstTimestampUs,
    long LastTimestampUs,
    int DistinctWindows,
    long FileBytes);

/// <summary>
/// Sequential reader for a reference log. Safe against a log that is still being appended: it only
/// ever surfaces complete 27-byte records, so the live session can be scrubbed while recording.
/// </summary>
public sealed class SessionLogReader : IDisposable
{
    private readonly FileStream _stream;
    private readonly byte[] _buffer;
    private int _buffered;
    private int _position;
    private bool _disposed;

    public SessionLogReader(string path, bool readHeader = true)
    {
        FilePath = path;
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        _buffer = new byte[BlockSize];
        LastCompleteOffset = SessionLogFormat.HeaderSize;

        if (readHeader)
        {
            Span<byte> header = stackalloc byte[SessionLogFormat.HeaderSize];
            if (_stream.Length >= SessionLogFormat.HeaderSize)
            {
                _stream.ReadExactly(header);
                if (!SessionLogFormat.TryReadHeader(header, out SessionLogHeader parsed))
                {
                    throw new InvalidDataException($"'{path}' is not a Screen Recall log (bad header).");
                }

                Header = parsed;
            }
        }
    }

    private static int BlockSize { get; } = (64 * 1024) - ((64 * 1024) % LogEntry.Size);

    /// <summary>Path being read.</summary>
    public string FilePath { get; }

    /// <summary>Parsed header, when present.</summary>
    public SessionLogHeader? Header { get; private set; }

    /// <summary>Offset just past the last complete record seen.</summary>
    public long LastCompleteOffset { get; private set; }

    /// <summary>File length at open time.</summary>
    public long Length => _stream.Length;

    /// <summary>Reads the next complete entry; false at end of file (or on a torn trailing record).</summary>
    public bool TryReadNext(out LogEntry entry)
    {
        entry = default;
        if (_position + LogEntry.Size > _buffered && !Fill())
        {
            return false;
        }

        entry = LogEntry.Read(_buffer.AsSpan(_position, LogEntry.Size));
        _position += LogEntry.Size;
        LastCompleteOffset += LogEntry.Size;
        return true;
    }

    /// <summary>Reads from the current position to the current end of the log.</summary>
    public IEnumerable<LogEntry> ReadAll()
    {
        while (TryReadNext(out LogEntry entry))
        {
            yield return entry;
        }
    }

    /// <summary>Reads every entry whose timestamp falls inside [fromUs, toUs].</summary>
    public IEnumerable<LogEntry> ReadRange(long fromUs, long toUs)
    {
        foreach (LogEntry entry in ReadAll())
        {
            if (entry.TimestampUs > toUs)
            {
                yield break;
            }

            if (entry.TimestampUs >= fromUs)
            {
                yield return entry;
            }
        }
    }

    /// <summary>Reads every entry at or after a timestamp.</summary>
    public IEnumerable<LogEntry> ReadFrom(long fromUs)
    {
        foreach (LogEntry entry in ReadAll())
        {
            if (entry.TimestampUs >= fromUs)
            {
                yield return entry;
            }
        }
    }

    /// <summary>Rewinds to the first record.</summary>
    public void Rewind()
    {
        _position = 0;
        _buffered = 0;
        _stream.Seek(SessionLogFormat.HeaderSize, SeekOrigin.Begin);
        LastCompleteOffset = SessionLogFormat.HeaderSize;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _stream.Dispose();
    }

    /// <summary>Scans a log segment without keeping it open.</summary>
    public static SessionLogStats Scan(string path)
    {
        using SessionLogReader reader = new(path);
        long count = 0;
        long first = long.MaxValue;
        long last = 0;
        HashSet<uint> windows = new();
        while (reader.TryReadNext(out LogEntry entry))
        {
            count++;
            if (entry.TimestampUs < first)
            {
                first = entry.TimestampUs;
            }

            if (entry.TimestampUs > last)
            {
                last = entry.TimestampUs;
            }

            windows.Add(entry.WindowId);
        }

        return new SessionLogStats(
            count,
            count == 0 ? 0 : first,
            last,
            windows.Count,
            new FileInfo(path).Length);
    }

    /// <summary>Collects every distinct asset hash referenced by a log segment.</summary>
    public static HashSet<ulong> CollectHashes(string path)
    {
        HashSet<ulong> hashes = new();
        using SessionLogReader reader = new(path);
        while (reader.TryReadNext(out LogEntry entry))
        {
            if (entry.AssetHash != TileHash.None)
            {
                hashes.Add(entry.AssetHash);
            }
        }

        return hashes;
    }

    private bool Fill()
    {
        _position = 0;
        _buffered = 0;
        int read;
        while (_buffered < _buffer.Length
               && (read = _stream.Read(_buffer, _buffered, _buffer.Length - _buffered)) > 0)
        {
            _buffered += read;
        }

        if (_buffered < LogEntry.Size)
        {
            _buffered = 0;
            return false;
        }

        // A live writer may be mid-record: ignore the torn tail, it becomes visible on the next read.
        _buffered -= _buffered % LogEntry.Size;
        return true;
    }
}
