using ScreenRecall.Storage;

namespace ScreenRecall.Player;

/// <summary>
/// Merged, forward-only cursor over a day's log segments. A service restart can leave more than one
/// segment for a day, so the cursor peeks one entry per segment and always yields the oldest, giving a
/// single time-ordered stream without loading the log into memory.
/// </summary>
public sealed partial class SessionReplayer
{
    private readonly List<SessionLogReader> _readers = new();
    private readonly List<long> _checkpoints;
    private readonly List<CheckpointData> _checkpointCache = new(4);
    private LogEntry?[] _peek = Array.Empty<LogEntry?>();
    private bool[] _exhausted = Array.Empty<bool>();
    private LogEntry? _lookahead;
    private bool _lookaheadLoaded;

    private LogEntry? Peek()
    {
        if (!_lookaheadLoaded)
        {
            _lookahead = ReadNext();
            _lookaheadLoaded = true;
        }

        return _lookahead;
    }

    private void Consume()
    {
        _lookahead = null;
        _lookaheadLoaded = false;
    }

    private LogEntry? ReadNext()
    {
        int best = -1;
        LogEntry candidate = default;

        for (int i = 0; i < _readers.Count; i++)
        {
            if (_peek[i] is null && !_exhausted[i])
            {
                if (_readers[i].TryReadNext(out LogEntry next))
                {
                    _peek[i] = next;
                }
                else
                {
                    _exhausted[i] = true;
                }
            }

            if (_peek[i] is { } entry && (best < 0 || entry.TimestampUs < candidate.TimestampUs))
            {
                best = i;
                candidate = entry;
            }
        }

        if (best < 0)
        {
            return null;
        }

        _peek[best] = null;
        return candidate;
    }

    private void RewindReaders()
    {
        for (int i = 0; i < _readers.Count; i++)
        {
            _readers[i].Rewind();
            _peek[i] = null;
            _exhausted[i] = false;
        }

        _lookahead = null;
        _lookaheadLoaded = false;
    }

    private (long First, long Last) ScanBounds()
    {
        long first = 0;
        long last = 0;
        foreach (SessionLogReader reader in _readers)
        {
            reader.Rewind();
            while (reader.TryReadNext(out LogEntry entry))
            {
                if (first == 0 || entry.TimestampUs < first)
                {
                    first = entry.TimestampUs;
                }

                if (entry.TimestampUs > last)
                {
                    last = entry.TimestampUs;
                }
            }

            reader.Rewind();
        }

        return (first, last);
    }
}
