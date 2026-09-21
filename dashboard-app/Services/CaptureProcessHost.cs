using System.ComponentModel;
using System.Diagnostics;
using System.IO;

using Microsoft.Win32;

namespace ScreenRecall.Dashboard.Services;

/// <summary>
/// Starts and stops the recorder process, and owns the "start the recorder with Windows" registration.
/// </summary>
/// <remarks>
/// The dashboard deliberately does not host capture. The recorder is a separate process so that it can outlive
/// the window, keep recording while the dashboard sits in the tray, and be restarted without touching a session
/// in progress. What lives here is only the user's ability to start and stop it from the UI.
/// </remarks>
public sealed class CaptureProcessHost
{
    /// <summary>Process name of the recorder (no extension).</summary>
    public const string ProcessName = "ScreenRecall.CaptureService";

    /// <summary>Name of the per-user Run value that starts the recorder at sign-in.</summary>
    public const string RunValueName = "ScreenRecall.Capture";

    /// <summary>Environment variable that overrides where the recorder is looked for.</summary>
    public const string ExecutableVariable = "SCREENRECALL_CAPTURE_EXE";

    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ConsoleArgument = "--console";

    /// <summary>
    /// Path of the recorder executable, or null when it cannot be found. Looks beside the dashboard first (a
    /// published layout), then walks up looking for the repository's build output (a development layout).
    /// </summary>
    public static string? FindExecutable()
    {
        string? overridden = Environment.GetEnvironmentVariable(ExecutableVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && File.Exists(overridden))
        {
            return overridden;
        }

        string beside = Path.Combine(AppContext.BaseDirectory, ProcessName + ".exe");
        if (File.Exists(beside))
        {
            return beside;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            foreach (string configuration in new[] { "Release", "Debug" })
            {
                string candidate = Path.Combine(
                    directory.FullName, "capture-service", "bin", configuration, "net8.0-windows", ProcessName + ".exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>True when a recorder process is alive.</summary>
    public static bool IsRunning() => Process.GetProcessesByName(ProcessName).Length > 0;

    /// <summary>
    /// Launches the recorder in console mode, detached from this process: closing the dashboard must not stop
    /// recording, which is also why the window is minimised rather than owned.
    /// </summary>
    public static bool Start(out string message)
    {
        if (IsRunning())
        {
            message = "the recorder is already running";
            return true;
        }

        string? executable = FindExecutable();
        if (executable is null)
        {
            message = "could not find ScreenRecall.CaptureService.exe — build the solution first";
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(executable, ConsoleArgument)
            {
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized,
            });

            message = "recorder started";
            return true;
        }
        catch (Exception ex) when (ex is IOException or Win32Exception or InvalidOperationException)
        {
            message = $"could not start the recorder: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Asks the recorder to stop, and waits for it. Termination is the fallback, never the first move: a killed
    /// recorder loses everything since its last flush, while a shutdown drains the tile queue before flushing the
    /// log, so the entries it already recorded stay readable.
    /// </summary>
    public static bool Stop(CaptureClient client, TimeSpan grace, out string message)
    {
        if (!IsRunning())
        {
            message = "the recorder is not running";
            return true;
        }

        bool asked = client.Shutdown();
        if (WaitUntilStopped(grace))
        {
            message = asked ? "recorder stopped" : "recorder stopped";
            return true;
        }

        int killed = 0;
        foreach (Process process in Process.GetProcessesByName(ProcessName))
        {
            try
            {
                process.Kill();
                killed++;
            }
            catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                // Already gone between the query and the kill.
            }
            finally
            {
                process.Dispose();
            }
        }

        WaitUntilStopped(TimeSpan.FromSeconds(2));
        message = killed > 0 && !IsRunning()
            ? "recorder did not answer, so it was terminated — up to the last flush interval may be missing"
            : "could not stop the recorder";
        return !IsRunning();
    }

    private static bool WaitUntilStopped(TimeSpan timeout)
    {
        long deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (!IsRunning())
            {
                return true;
            }

            Thread.Sleep(150);
        }

        return !IsRunning();
    }

    /* ---------------------------------------------------------------------- */
    /* Start with Windows                                                     */
    /* ---------------------------------------------------------------------- */

    /// <summary>
    /// True when a per-user Run entry exists. The registry is the source of truth rather than the config file,
    /// because it is what Windows actually reads at sign-in — so the checkbox can never disagree with reality.
    /// </summary>
    public static bool StartsWithWindows()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) is string command && command.Length > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>The command registered for sign-in, for display. Null when nothing is registered.</summary>
    public static string? StartupCommand()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(RunValueName) as string;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Registers or removes the sign-in entry. Per-user (HKCU), so this needs no elevation and changes nothing
    /// machine-wide — which also means it works from a development build with no installer.
    /// </summary>
    public static bool SetStartsWithWindows(bool enabled, out string message)
    {
        string? executable = FindExecutable();
        if (enabled && executable is null)
        {
            message = "could not find the recorder executable to register";
            return false;
        }

        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)!;
            if (enabled)
            {
                key.SetValue(RunValueName, $"\"{executable}\" {ConsoleArgument}", RegistryValueKind.String);
                message = "the recorder will start when you sign in";
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
                message = "the recorder will no longer start by itself";
            }

            return true;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            message = $"could not change the sign-in entry: {ex.Message}";
            return false;
        }
    }
}
