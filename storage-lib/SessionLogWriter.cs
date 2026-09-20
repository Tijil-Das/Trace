namespace ScreenRecall.Storage;

/// <summary>
/// Buffered append-only writer for a day's reference log. Entries are written in whole 27-byte
/// records, so a concurrent reader only ever sees complete records — never a torn one — which is
/// what lets the dashboard scrub today's session while the service is still recording it.
/// </summary>
public sealed class SessionLogWriter : IDisposable
{
    private const int DefaultEntryBuffer = 2048;

    private readonly FileStream _stream;
    private readonly byte[] _entries;
    private readonly object _gate = new();
    private readonly long _initialLength;
    private int _buffered;
    private bool _disposed;

    public SessionLogWriter(string path, DateTimeOffset dayStartLocal, int entryBuffer = DefaultEntryBuffer)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        bool exists = File.Exists(path);
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        _entries = new byte[entryBuffer * LogEntry.Size];
        _initialLength = _stream.Length;

        if (!exists || _stream.Length < SessionLogFormat.HeaderSize)
        {
            byte[] header = SessionLogFormat.CreateHeaderBytes(dayStartLocal);
            _stream.Write(header);
            HeaderBytesWritten = header.Length;
            IsNewFile = true;
        }

        FilePath = path;
    }

    /// <summary>Path of this log segment.</summary>
    public string FilePath { get; }

    /// <summary>True when this writer created the file rather than appending to an existing one.</summary>
    public bool IsNewFile { get; }

    /// <summary>Entries appended through this writer.</summary>
    public long EntriesWritten { get; private set; }

    /// <summary>Entry bytes appended through this writer.</summary>
    public long EntryBytesWritten { get; private set; }

    /// <summary>Header bytes this writer wrote at creation time (0 when appending to an existing file).</summary>
    private int HeaderBytesWritten { get; set; }
    /// <summary>Flushed length plus buffered entries.</summary>
    public long FileLength => _stream.Length + _buffered;

    /// <summary>
    /// Records that were all zeros when they reached the disk. A real entry always carries a non-zero
    /// timestamp, so any such record means the buffer was written before it was filled — corruption
    /// that a reader would otherwise silently treat as the end of the log.
    /// </summary>
    public long ZeroRecordFaults { get; private set; }

    /// <summary>Appends one entry; flushes to disk when the internal buffer fills.</summary>
    public void Append(in LogEntry entry)
    {
        // Flush() is reachable from control paths (pause, purge, dispose) while the capture loop is
        // appending, so writer state is serialized: a torn buffer write here would put zero records
        // into the middle of the log.
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            LogEntry.Write(_entries.AsSpan(_buffered), entry);
            _buffered += LogEntry.Size;
            EntriesWritten++;
            EntryBytesWritten += LogEntry.Size;
            if (_buffered == _entries.Length)
            {
                DrainBuffer();
            }
        }
    }

    /// <summary>Appends a batch of entries.</summary>
    public void AppendRange(ReadOnlySpan<LogEntry> entries)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            Append(entries[i]);
        }
    }

    /// <summary>Flushes buffered entries to the file, always ending on a record boundary.</summary>
    public void Flush()
    {
        lock (_gate)
        {
            DrainBuffer();
            _stream.Flush(flushToDisk: false);
            VerifyFileLength();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                DrainBuffer();
                _stream.Flush(flushToDisk: true);
            }
            finally
            {
                _stream.Dispose();
            }
        }
    }

    /// <summary>
    /// Writes the buffered records, skipping any that are entirely zero. A real entry always carries a
    /// non-zero timestamp, so an all-zero record is a hole rather than data; dropping it keeps the log
    /// self-consistent (a phantom "empty tile" record would render as a hole in playback), and the
    /// engine's next checkpoint or rescan re-references whatever tile it described.
    /// </summary>
    private void DrainBuffer()
    {
        if (_buffered == 0)
        {
            return;
        }

        int writeStart = 0;
        for (int offset = 0; offset < _buffered; offset += LogEntry.Size)
        {
            if (!IsZeroRecord(_entries.AsSpan(offset, LogEntry.Size)))
            {
                continue;
            }

            ZeroRecordFaults++;
            if (offset > writeStart)
            {
                _stream.Write(_entries, writeStart, offset - writeStart);
            }

            writeStart = offset + LogEntry.Size;
        }

        if (_buffered > writeStart)
        {
            _stream.Write(_entries, writeStart, _buffered - writeStart);
        }

        _buffered = 0;
    }

    /// <summary>True when the file grew (or shrank) by anything other than what this writer appended.</summary>
    public bool ExternalWriterDetected { get; private set; }

    private static bool IsZeroRecord(ReadOnlySpan<byte> record)
    {
        foreach (byte value in record)
        {
            if (value != 0)
            {
                return false;
            }
        }

        return true;
    }

    private void VerifyFileLength()
    {
        // Length at open + whatever this writer added. The size is read from the file system rather
        // than from the stream handle: a second handle appending to the same log would not be visible
        // in this handle's cached length. Anything unexpected here means another writer is in the file,
        // which no amount of internal locking can protect against.
        long actual;
        try
        {
            actual = new FileInfo(FilePath).Length;
        }
        catch (IOException)
        {
            return;
        }

        long expected = _initialLength + HeaderBytesWritten + EntryBytesWritten;
        if (actual != expected)
        {
            ExternalWriterDetected = true;
        }
    }

}
