using System.Diagnostics;
using System.Reflection;

namespace ScreenRecall.CaptureService.Cli;

/// <summary>
/// Which way this binary was compiled, and whether its timings may be trusted at all (spec 3: steady-state
/// CPU below 2%).
///
/// This exists because a Debug run is roughly an order of magnitude more expensive than a Release one, and a
/// Debug number is therefore not a measurement of the design. Rather than leaving that as a note in a
/// document, every performance-facing verb asks this class first: a run that is not Release says so in its
/// output, and <c>--cpu-bench</c> refuses to certify it.
///
/// The answer comes from the assembly's <see cref="DebuggableAttribute"/> - the same flag the runtime and the
/// JIT read - rather than from a compile-time constant, so it describes the binary that is actually running
/// (a Debug-built storage-lib inside a Release service reports optimizations on, because the IL that matters
/// was optimized: the attribute is per assembly, and this one is the entry point).
/// </summary>
internal static class BuildConfiguration
{
    private static readonly bool Optimized = Detect();

    /// <summary>True when the JIT is allowed to optimize this binary.</summary>
    internal static bool IsOptimized => Optimized;

    /// <summary>Human-readable build configuration, for run headers.</summary>
    internal static string Description => Optimized
        ? "Release (JIT optimizations on)"
        : "Debug (JIT optimizations disabled)";

    /// <summary>
    /// Prints a loud warning when timings from this build cannot be used as evidence. Silent on a Release
    /// build, so documented output is unchanged where it was already valid.
    /// </summary>
    internal static void WarnIfNotOptimized(string subject)
    {
        if (Optimized)
        {
            return;
        }

        Console.WriteLine();
        Console.WriteLine($"[ !! ] build configuration  : {Description}");
        Console.WriteLine($"[ !! ]                       : {subject} from a Debug build is not valid evidence - "
                          + "rebuild Release (Trace.cmd build) and re-measure before drawing any conclusion.");
        Console.WriteLine();
    }

    private static bool Detect()
    {
        // Release emits Debuggable(IgnoreSymbolStoreSequencePoints) => optimizations on.
        // Debug emits Debuggable(... | DisableOptimizations | ...) => IsJITOptimizerDisabled = true.
        // No attribute at all means nothing asked for the optimizer to be turned off.
        return typeof(BuildConfiguration).Assembly.GetCustomAttribute<DebuggableAttribute>() is not { IsJITOptimizerDisabled: true };
    }
}
