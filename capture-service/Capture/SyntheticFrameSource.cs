using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>
/// Deterministic software frame source: an animated desktop drawn by this process. It exists so the
/// whole pipeline — tiling, hashing, dedupe, log, checkpoints, player — can be exercised and
/// pixel-verified on machines without a duplicatable output (CI, headless validation, and the
/// fidelity harness in spec 12), and so tests can assert exact tile-level behaviour.
/// </summary>
internal sealed partial class SyntheticFrameSource : IFrameSource
{
    private const int CursorSize = 24;
    private const int TypingWidth = 208;
    private const int TypingHeight = 24;
    private const int BandHeight = 96;

    private readonly int _frameIntervalMs;
    private readonly MonitorInfo _monitor;
    private readonly byte[] _pixels;
    private readonly Dictionary<uint, uint> _colors = new();

    private int _frameIndex;
    private int _cursorX;
    private int _cursorY;
    private int _scrollOffset;
    private int _typingPhase;

    internal SyntheticFrameSource(
        int width = 640,
        int height = 480,
        int frameIntervalMs = 40,
        int tileSize = TileGrid.DefaultTileSize,
        ushort monitorId = 0,
        int startX = 0,
        int startY = 0)
    {
        _frameIntervalMs = Math.Max(1, frameIntervalMs);
        _monitor = new MonitorInfo(monitorId, "synthetic", startX, startY, width, height, tileSize);
        _pixels = new byte[width * height * 4];
        Monitors = new[] { _monitor };
        DrawStaticBackground();
    }

    public string Name => "synthetic";

    public IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>Frames handed out so far.</summary>
    internal int FramesGenerated => _frameIndex;

    public bool TryRecreate(out string? error)
    {
        error = null;
        return true;
    }

    public bool TryAcquire(TimeSpan timeout, out SourceFrame frame)
    {
        frame = null!;
        if (timeout.TotalMilliseconds < _frameIntervalMs)
        {
            int wait = Math.Min(_frameIntervalMs, (int)Math.Max(0, timeout.TotalMilliseconds));
            if (wait > 0)
            {
                Thread.Sleep(wait);
            }

            return false;
        }

        List<IntRect> dirty = new(4);
        List<IntRect> moves = new(1);
        _frameIndex++;

        // 1. A little "cursor" wanders the screen: two small dirty rects per frame.
        dirty.Add(new IntRect(_cursorX, _cursorY, CursorSize, CursorSize));
        _cursorX = (_cursorX + 7) % Math.Max(1, _monitor.Width - CursorSize);
        _cursorY = (_cursorY + 5) % Math.Max(1, _monitor.Height - CursorSize);
        FillRect(_cursorX, _cursorY, CursorSize, CursorSize, Color(0x00, 0x60, 0xFF, 0xFF), 1);
        dirty.Add(new IntRect(_cursorX, _cursorY, CursorSize, CursorSize));

        // 2. A "typing" strip advances every third frame.
        if (_frameIndex % 3 == 0)
        {
            _typingPhase++;
            FillRect(24, 40, TypingWidth, TypingHeight, Color(0x20, 0x20, 0x20, 0xFF), 2);
            DrawTypingBlocks();
            dirty.Add(new IntRect(24, 40, TypingWidth, TypingHeight));
        }

        // 3. Every tenth frame a band scrolls: a real move rect plus dirty areas.
        if (_frameIndex % 10 == 0)
        {
            ScrollBand();
            moves.Add(new IntRect(24, 120, _monitor.Width - 48, BandHeight));
            dirty.Add(new IntRect(24, 120, _monitor.Width - 48, BandHeight));
        }

        frame = new SourceFrame(
            _monitor,
            _monitor.Width,
            _monitor.Height,
            _monitor.Width * 4,
            _pixels,
            dirty,
            moves,
            false,
            false,
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() * 1000);
        return true;
    }

    public void Release(SourceFrame frame)
    {
        // Nothing to release: the synthetic source owns its buffer outright.
    }

    public void Dispose()
    {
    }
}
