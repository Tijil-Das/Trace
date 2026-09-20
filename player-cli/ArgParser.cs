using System.Globalization;

namespace ScreenRecall.PlayerCli;

/// <summary>Small argument parser shared by all commands.</summary>
internal static class ArgParser
{
    /// <summary>Options that consume a following value, so positionals are not misread.</summary>
    internal static readonly string[] ValueOptions =
    {
        "--at", "--from", "--to", "--out", "--monitor", "--max", "--dump", "--every", "--iterations", "--cell",
    };

    /// <summary>Parses a time argument in any of the supported forms.</summary>
    internal static long Timestamp(string value, DateOnly day, long firstTimestampUs)
    {
        if (value.StartsWith('+')
            && double.TryParse(value[1..], NumberStyles.Float, CultureInfo.InvariantCulture, out double offsetSeconds))
        {
            return firstTimestampUs + (long)(offsetSeconds * 1_000_000);
        }

        if (value.Length >= 12 && long.TryParse(value, out long epochMs))
        {
            return epochMs * 1000;
        }

        if (TimeSpan.TryParse(value, CultureInfo.InvariantCulture, out TimeSpan time) && time < TimeSpan.FromDays(1))
        {
            DateTimeOffset midnight = new(day.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            return midnight.Add(time).ToUnixTimeMilliseconds() * 1000;
        }

        throw new ArgumentException(
            $"Unrecognized time '{value}'. Use HH:mm:ss[.fff], +<seconds>, or epoch milliseconds.");
    }

    /// <summary>Reads an option value ("--flag value" or "--flag=value").</summary>
    internal static string? Option(string[] args, string name)
    {
        for (int i = 0; i < args.Length; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
            {
                return args[i + 1];
            }

            if (args[i].StartsWith(name + "=", StringComparison.OrdinalIgnoreCase))
            {
                return args[i][(name.Length + 1)..];
            }
        }

        return null;
    }

    /// <summary>True when a flag is present.</summary>
    internal static bool Flag(string[] args, string name)
        => args.Any(arg => arg.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Positional arguments, skipping options and their values.</summary>
    internal static List<string> Positionals(string[] args)
    {
        List<string> positional = new();
        for (int i = 1; i < args.Length; i++)
        {
            string arg = args[i];
            if (arg.StartsWith('-'))
            {
                if (!arg.Contains('=') && ValueOptions.Any(option => arg.Equals(option, StringComparison.OrdinalIgnoreCase)))
                {
                    i++;
                }

                continue;
            }

            positional.Add(arg);
        }

        return positional;
    }

    /// <summary>Parses a yyyy-MM-dd day argument.</summary>
    internal static DateOnly Day(string value)
        => DateOnly.TryParseExact(value, "yyyy-MM-dd", out DateOnly day)
            ? day
            : throw new ArgumentException($"'{value}' is not a day (expected yyyy-MM-dd).");

    /// <summary>Required positional argument.</summary>
    internal static string Required(List<string> positional, int index, string name)
        => positional.Count > index
            ? positional[index]
            : throw new ArgumentException($"missing required <{name}> argument");

    internal static ushort? Monitor(string? value) => ushort.TryParse(value, out ushort id) ? id : null;

    internal static int Int(string? value, int fallback) => int.TryParse(value, out int parsed) ? parsed : fallback;

    internal static double Double(string? value, double fallback)
        => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed) ? parsed : fallback;

    /// <summary>Local wall-clock label for a log timestamp.</summary>
    internal static string FormatTimestamp(long timestampUs)
        => timestampUs <= 0 ? "--:--:--" : DateTimeOffset.UnixEpoch.AddTicks(timestampUs * 10).LocalDateTime.ToString("HH:mm:ss.fff");

    /// <summary>Human-readable byte size.</summary>
    internal static string FormatBytes(long bytes)
        => bytes >= 1024L * 1024 * 1024
            ? $"{bytes / 1024.0 / 1024 / 1024:0.00} GB"
            : bytes >= 1024L * 1024
                ? $"{bytes / 1024.0 / 1024:0.00} MB"
                : $"{bytes / 1024.0:0.0} KB";

    /// <summary>File-name-safe local timestamp label.</summary>
    internal static string FileTimestamp(long timestampUs)
        => FormatTimestamp(timestampUs).Replace(':', '-');
}
