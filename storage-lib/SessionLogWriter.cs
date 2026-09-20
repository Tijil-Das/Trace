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
    private int _buffered;
    private bool _disposed;

    public SessionLogWriter(string path, DateTimeOffset dayStartLocal, int entryBuffer = DefaultEntryBuffer)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        bool exists = File.Exists(path);
        _stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        _entries = new byte[entryBuffer * LogEntry.Size];

        if (!exists || _stream.Length < SessionLogFormat.HeaderSize)
        {
            _stream.Write(SessionLogFormat.CreateHeaderBytes(dayStartLocal));
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

    /// <summary>Flushed length plus buffered entries.</summary>
    public long FileLength => _stream.Length + _buffered;

    /// <summary>Appends one entry; flushes to disk when the internal buffer fills.</summary>
    public void Append(in LogEntry entry)
    {
        LogEntry.Write(_entries.AsSpan(_buffered), entry);
        _buffered += LogEntry.Size;
        EntriesWritten++;
        EntryBytesWritten += LogEntry.Size;
        if (_buffered == _entries.Length)
        {
            DrainBuffer();
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
        DrainBuffer();
        _stream.Flush(flushToDisk: false);
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
            Flush();
        }
        finally
        {
            _stream.Dispose();
        }
    }

    private void DrainBuffer()
    {
        if (_buffered == 0)
        {
            return;
        }

        _stream.Write(_entries, 0, _buffered);
        _buffered = 0;
    }
}
