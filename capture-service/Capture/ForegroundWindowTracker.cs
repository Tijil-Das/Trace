using System.IO.Hashing;
using ScreenRecall.CaptureService.Interop;
using ScreenRecall.Storage;

namespace ScreenRecall.CaptureService.Capture;

/// <summary>What the user is currently looking at, and whether it is denylisted.</summary>
internal sealed record ForegroundWindowInfo(uint Id, string Process, string Title, bool Excluded, IntPtr Handle)
{
    internal string Display => Title.Length > 0 ? $"{Process} — {Title}" : Process;

    internal static ForegroundWindowInfo Empty { get; } = new(0, string.Empty, string.Empty, false, IntPtr.Zero);
}

/// <summary>
/// Tracks the foreground window (spec 5.7). Two jobs: assign the stable window id that log entries
/// carry, and decide whether the window is on the exclusion list so its content is never recorded.
/// </summary>
internal sealed class ForegroundWindowTracker
{
    private readonly ExclusionMatcher _exclusions;
    private ForegroundWindowInfo _current = ForegroundWindowInfo.Empty;

    internal ForegroundWindowTracker(ExclusionMatcher exclusions)
    {
        _exclusions = exclusions;
    }

    /// <summary>Most recently observed foreground window.</summary>
    internal ForegroundWindowInfo Current => _current;

    /// <summary>True when the current window must not be recorded.</summary>
    internal bool IsExcluded => _current.Excluded;

    /// <summary>Re-reads the foreground window and re-evaluates the denylist.</summary>
    internal ForegroundWindowInfo Poll()
    {
        IntPtr handle = NativeMethods.GetForegroundWindow();
        if (handle == IntPtr.Zero)
        {
            _current = ForegroundWindowInfo.Empty;
            return _current;
        }

        if (handle == _current.Handle)
        {
            return _current;
        }

        string process = NativeMethods.GetProcessNameForWindow(handle);
        string title = NativeMethods.GetWindowTitle(handle);
        bool excluded = _exclusions.IsExcluded(process, title);
        _current = new ForegroundWindowInfo(StableWindowId(process, title), process, title, excluded, handle);
        return _current;
    }

    /// <summary>
    /// Window id is a hash of process + title, so it is stable across restarts (and across days for
    /// the same document), which is what lets the player list "the next time Chrome was focused".
    /// </summary>
    internal static uint StableWindowId(string process, string title)
    {
        if (process.Length == 0 && title.Length == 0)
        {
            return 0;
        }

        byte[] buffer = System.Text.Encoding.UTF8.GetBytes($"{process.ToLowerInvariant()}|{title}");
        uint hash = XxHash32.HashToUInt32(buffer);
        return hash == 0 ? 1u : hash;
    }
}
