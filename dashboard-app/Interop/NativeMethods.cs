using System.Runtime.InteropServices;

namespace ScreenRecall.Dashboard.Interop;

/// <summary>
/// The piece of Win32 the shell needs to go fullscreen: which monitor the window is on, and how to put the window
/// exactly there.
/// </summary>
/// <remarks>
/// <c>Maximizing</c> a borderless WPF window is not the same promise — the geometry it picks depends on the style
/// the window had when it was maximised, which is how a "fullscreen" player ends up with a strip of desktop (or of
/// app background) around the picture. The monitor rectangle is exact, and it is the whole monitor: the work area
/// excludes the taskbar, and the taskbar is not part of the recording.
/// </remarks>
internal static partial class NativeMethods
{
    internal const uint MonitorDefaultToNearest = 2;
    internal const uint SwpNoZOrder = 0x0004;
    internal const uint SwpNoActivate = 0x0010;
    internal const uint SwpFrameChanged = 0x0020;

    /// <summary>A Win32 rectangle, in physical pixels.</summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly int Width => Right - Left;

        internal readonly int Height => Bottom - Top;
    }

    /// <summary>MONITORINFO: <c>Size</c> must be filled in before the call, as <c>cbSize</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        internal int Size;
        internal Rect Monitor;
        internal Rect Work;
        internal uint Flags;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    /// <summary>
    /// Full bounds of the monitor the window is on, including the taskbar area — or false when there is no
    /// monitor to ask (the window is going away).
    /// </summary>
    internal static bool TryGetMonitorBounds(IntPtr hwnd, out Rect bounds)
    {
        bounds = default;
        IntPtr monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        MonitorInfo info = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        bounds = info.Monitor;
        return bounds.Width > 0 && bounds.Height > 0;
    }
}