using System.Runtime.InteropServices;

namespace ScreenRecall.CaptureService.Interop;

internal static partial class NativeMethods
{
    internal const int PROCESS_MODE_BACKGROUND_BEGIN = 0x00100000;
    internal const int PROCESS_MODE_BACKGROUND_END = 0x00200000;
    internal const int THREAD_MODE_BACKGROUND_BEGIN = 0x00010000;
    internal const int THREAD_MODE_BACKGROUND_END = 0x00020000;

    internal const int MOD_ALT = 0x0001;
    internal const int MOD_CONTROL = 0x0002;
    internal const int MOD_SHIFT = 0x0004;
    internal const int MOD_WIN = 0x0008;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetPriorityClass(IntPtr process, int priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetThreadPriority(IntPtr thread, int priority);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(IntPtr hWnd, int id, int modifiers, int virtualKey);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(IntPtr hWnd, int id);

    /// <summary>
    /// Resource governance (spec 5.8): drops the process into background mode (Below Normal priority
    /// with memory and I/O priority trimmed) so capture never contends with foreground work.
    /// </summary>
    internal static bool EnterBackgroundMode()
    {
        bool process = SetPriorityClass(GetCurrentProcess(), PROCESS_MODE_BACKGROUND_BEGIN);
        SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);
        return process;
    }

    /// <summary>Restores normal priority when background mode is no longer wanted.</summary>
    internal static void LeaveBackgroundMode()
    {
        SetPriorityClass(GetCurrentProcess(), PROCESS_MODE_BACKGROUND_END);
        SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_END);
    }

    /// <summary>Puts the calling thread into background mode (used by the capture thread).</summary>
    internal static void EnterThreadBackgroundMode() => SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN);

    /// <summary>Parses a hotkey string such as "Ctrl+Alt+P" into modifiers and a virtual key.</summary>
    internal static bool TryParseHotkey(string? text, out int modifiers, out int virtualKey)
    {
        modifiers = 0;
        virtualKey = 0;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        foreach (string part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= MOD_CONTROL;
                    break;
                case "ALT":
                    modifiers |= MOD_ALT;
                    break;
                case "SHIFT":
                    modifiers |= MOD_SHIFT;
                    break;
                case "WIN":
                case "WINDOWS":
                    modifiers |= MOD_WIN;
                    break;
                default:
                    if (part.Length == 1 && char.IsLetterOrDigit(part[0]))
                    {
                        virtualKey = char.ToUpperInvariant(part[0]);
                    }
                    else if (part.StartsWith('F') && int.TryParse(part[1..], out int functionKey) && functionKey is >= 1 and <= 24)
                    {
                        virtualKey = 0x70 + functionKey - 1;
                    }
                    else
                    {
                        return false;
                    }

                    break;
            }
        }

        return modifiers != 0 && virtualKey != 0;
    }
}
