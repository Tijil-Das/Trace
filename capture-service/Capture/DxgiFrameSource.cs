using ScreenRecall.CaptureService.Dxgi;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// DXGI Desktop Duplication backend (spec 4 / 5.1): one duplication session per monitor, all sharing
/// the dirty/move rects DXGI reports so only genuinely changed regions are ever touched.
/// </summary>
internal sealed class DxgiFrameSource : IFrameSource
{
    private readonly List<DesktopDuplicator> _duplicators = new();
    private readonly List<string> _notes = new();
    private readonly int _tileSize;
    private readonly HashSet<ushort>? _filter;

    private DxgiFrameSource(int tileSize, HashSet<ushort>? filter)
    {
        _tileSize = tileSize;
        _filter = filter;
        Monitors = Array.Empty<MonitorInfo>();
    }

    public string Name => "dxgi-desktop-duplication";

    public IReadOnlyList<MonitorInfo> Monitors { get; private set; }

    /// <summary>Non-fatal diagnostics collected while building or rebuilding sessions.</summary>
    internal IReadOnlyList<string> Notes => _notes;

    /// <summary>Creates a source covering the requested monitors (null/empty filter = every monitor).</summary>
    internal static DxgiFrameSource Create(int tileSize, IReadOnlyCollection<ushort>? filter)
    {
        HashSet<ushort>? wanted = filter is { Count: > 0 } ? new HashSet<ushort>(filter) : null;
        DxgiFrameSource source = new(tileSize, wanted);
        source.TryRecreate(out _);
        return source;
    }

    public bool TryRecreate(out string? error)
    {
        error = null;
        DisposeDuplicators();
        _notes.Clear();

        List<DxgiOutputTarget> targets = DxgiOutputEnumerator.Enumerate(_tileSize);
        foreach (DxgiOutputTarget target in targets)
        {
            if (_filter is not null && !_filter.Contains(target.Id))
            {
                target.Dispose();
                continue;
            }

            DesktopDuplicator? duplicator = DesktopDuplicator.TryCreate(target, out string? createError);
            if (duplicator is null)
            {
                _notes.Add($"monitor {target.Id} ({target.Monitor.DeviceName}): {createError}");
                target.Dispose();
                continue;
            }

            _duplicators.Add(duplicator);
        }

        Monitors = _duplicators.Select(d => d.Monitor).ToArray();
        if (_duplicators.Count == 0)
        {
            error = _notes.Count > 0 ? string.Join("; ", _notes) : "no duplicatable outputs found";
            return false;
        }

        return true;
    }

    public bool TryAcquire(TimeSpan timeout, out SourceFrame frame)
    {
        frame = null!;
        DateTime deadline = DateTime.UtcNow + timeout;

        foreach (DesktopDuplicator duplicator in _duplicators)
        {
            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero)
            {
                remaining = TimeSpan.Zero;
            }

            DuplicatedFrame? acquired = duplicator.TryAcquireFrame(remaining);
            if (acquired is null)
            {
                continue;
            }

            frame = FrameConverter.Convert(acquired, duplicator.Monitor);
            return true;
        }

        return false;
    }

    public void Release(SourceFrame frame) => ReleaseCurrent();

    /// <summary>Releases whatever frame the backend currently holds.</summary>
    internal void ReleaseCurrent()
    {
        foreach (DesktopDuplicator duplicator in _duplicators)
        {
            duplicator.ReleaseFrame();
        }
    }

    public void Dispose() => DisposeDuplicators();

    private void DisposeDuplicators()
    {
        foreach (DesktopDuplicator duplicator in _duplicators)
        {
            duplicator.Dispose();
        }

        _duplicators.Clear();
    }
}
