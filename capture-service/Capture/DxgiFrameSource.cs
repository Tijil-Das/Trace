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

    /// <summary>
    /// Why the last (re)build failed, when it did - classified, so the retry loop and the dashboard can tell an
    /// expected "the desktop is not visible right now" from a fault worth reporting (spec 13).
    /// </summary>
    internal DxgiStatus.Failure? LastFailure { get; private set; }

    /// <summary>
    /// A failed (re)build as a block, for the status readout. A live session has monitors and therefore nothing
    /// to report; the retry policy that decides <em>when</em> to try again lives in
    /// <see cref="RecoveringDxgiSource"/>, which is what the service actually runs.
    /// </summary>
    public SourceBlock? Block => LastFailure is { } failure
        ? new SourceBlock(failure.Reason, failure.Detail, 0)
        : null;

    private bool _fullFrameRequired;

    /// <summary>Forwarded to every duplication session: a pending rescan needs the whole surface read back.</summary>
    public bool FullFrameRequired
    {
        get => _fullFrameRequired;
        set
        {
            _fullFrameRequired = value;
            foreach (DesktopDuplicator duplicator in _duplicators)
            {
                duplicator.FullFrameRequired = value;
            }
        }
    }

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
        LastFailure = null;

        List<DxgiOutputTarget> targets = DxgiOutputEnumerator.Enumerate(_tileSize);
        DxgiStatus.Failure? expected = null;
        DxgiStatus.Failure? unexpected = null;

        foreach (DxgiOutputTarget target in targets)
        {
            if (_filter is not null && !_filter.Contains(target.Id))
            {
                target.Dispose();
                continue;
            }

            DesktopDuplicator? duplicator = DesktopDuplicator.TryCreate(target, out DxgiStatus.Failure failure);
            if (duplicator is null)
            {
                _notes.Add($"monitor {target.Id} ({target.Monitor.DeviceName}): {failure.Detail}");

                // A genuine driver or API error outranks an expected one: "the session is locked" must never be
                // the reason that gets reported when a display also failed for something the user could act on.
                if (DxgiStatus.IsExpected(failure.Reason))
                {
                    expected ??= failure;
                }
                else
                {
                    unexpected ??= failure;
                }

                target.Dispose();
                continue;
            }

            duplicator.FullFrameRequired = _fullFrameRequired;
            _duplicators.Add(duplicator);
        }

        Monitors = _duplicators.Select(d => d.Monitor).ToArray();
        if (_duplicators.Count == 0)
        {
            LastFailure = unexpected ?? expected ?? new DxgiStatus.Failure(
                DxgiStatus.UnavailableReason.NoOutput,
                "no attached display output could be enumerated");
            error = LastFailure.Value.Detail;
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
