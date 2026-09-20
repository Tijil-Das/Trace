using System.Runtime.InteropServices;

namespace ScreenRecall.CaptureService.Interop;

/// <summary>Raw Win32 surface the capture service needs beyond DXGI.</summary>
internal static partial class NativeMethods
{
    internal const int PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int GetWindowText(IntPtr hWnd, [Out] char[] text, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW", SetLastError = true)]
    internal static partial int GetWindowTextLength(IntPtr hWnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "GetForegroundWindow")]
    internal static partial IntPtr GetForegroundWindowRaw();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(IntPtr hWnd);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentProcess")]
    internal static partial IntPtr GetCurrentProcess();

    /// <summary>
    /// Kernel handle count for a process. Used instead of <see cref="System.Diagnostics.Process.HandleCount"/>
    /// because that property was measured reporting thousands of handles for a process holding a few hundred:
    /// a measurement tool has to measure with the system call directly.
    /// </summary>
    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetProcessHandleCount(IntPtr process, out int handleCount);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThread")]
    internal static partial IntPtr GetCurrentThread();

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageName(IntPtr process, int flags, [Out] char[] exeName, ref int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial IntPtr OpenProcess(int desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", EntryPoint = "GetDiskFreeSpaceExW", SetLastError = true,
        StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetDiskFreeSpaceEx(string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SystemPowerStatus
    {
        internal byte ACLineStatus;
        internal byte BatteryFlag;
        internal byte BatteryLifePercent;
        internal byte SystemStatusFlag;
        internal uint BatteryLifeTime;
        internal uint BatteryFullLifeTime;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SystemPowerStatus status);

    /// <summary>Window the user is currently working in.</summary>
    internal static IntPtr GetForegroundWindow() => GetForegroundWindowRaw();

    /// <summary>Window title, empty when unavailable.</summary>
    internal static string GetWindowTitle(IntPtr hWnd)
    {
        int length = GetWindowTextLength(hWnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        char[] buffer = new char[length + 2];
        int copied = GetWindowText(hWnd, buffer, buffer.Length);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    /// <summary>Executable name (without extension) owning a window, empty when unavailable.</summary>
    internal static string GetProcessNameForWindow(IntPtr hWnd)
    {
        _ = GetWindowThreadProcessId(hWnd, out uint processId);
        if (processId == 0)
        {
            return string.Empty;
        }

        IntPtr process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == IntPtr.Zero)
        {
            return string.Empty;
        }

        try
        {
            int size = 1024;
            char[] buffer = new char[size];
            if (!QueryFullProcessImageName(process, 0, buffer, ref size))
            {
                return string.Empty;
            }

            return Path.GetFileNameWithoutExtension(new string(buffer, 0, size));
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Free bytes on the volume hosting a path, or 0 when unknown.</summary>
    internal static ulong GetFreeDiskBytes(string path)
    {
        string? root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root))
        {
            return 0;
        }

        return GetDiskFreeSpaceEx(root, out ulong available, out _, out _) ? available : 0;
    }

    /// <summary>True when the machine is currently running on battery.</summary>
    internal static bool IsOnBattery()
        => GetSystemPowerStatus(out SystemPowerStatus status) && status.ACLineStatus == 0;
}
