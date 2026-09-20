using System.Text.RegularExpressions;

namespace ScreenRecall.Storage;

/// <summary>
/// Sane exclusion defaults (spec 14): password managers and detectable private/incognito windows are
/// skipped out of the box, because this system would otherwise capture anything visibly on screen.
/// </summary>
public static class ExclusionDefaults
{
    /// <summary>Process names skipped by default (matched case-insensitively, ".exe" optional).</summary>
    public static IReadOnlyList<string> Processes { get; } = new[]
    {
        "KeePass", "KeePassXC", "1Password", "Bitwarden", "LastPass", "Dashlane", "Keeper",
        "NordPass", "Enpass", "RoboForm", "ProtonPass", "Sysinternals", "CredentialUIBroker",
    };

    /// <summary>Window-title fragments skipped by default (private/incognito markers).</summary>
    public static IReadOnlyList<string> TitlePatterns { get; } = new[]
    {
        "incognito", "inprivate", "private browsing", "private window", "private tab", "&private",
    };
}

/// <summary>
/// Denylist matcher (spec 5.7). Process names support <c>*</c> wildcards; title patterns are plain
/// case-insensitive substrings, or a raw regular expression when wrapped in slashes ("/regex/").
/// </summary>
public sealed class ExclusionMatcher
{
    private readonly List<string> _processPatterns = new();
    private readonly List<Regex> _processRegexes = new();
    private readonly List<Regex> _titleRegexes = new();

    public ExclusionMatcher(IEnumerable<string>? processes, IEnumerable<string>? titlePatterns)
    {
        foreach (string raw in processes ?? Enumerable.Empty<string>())
        {
            string pattern = Normalize(raw);
            if (pattern.Length == 0)
            {
                continue;
            }

            _processPatterns.Add(pattern);
            string regex = "^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$";
            _processRegexes.Add(new Regex(regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
        }

        foreach (string raw in titlePatterns ?? Enumerable.Empty<string>())
        {
            string pattern = raw.Trim();
            if (pattern.Length == 0)
            {
                continue;
            }

            try
            {
                if (pattern.Length > 2 && pattern.StartsWith('/') && pattern.EndsWith('/'))
                {
                    _titleRegexes.Add(new Regex(pattern[1..^1], RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                }
                else
                {
                    _titleRegexes.Add(new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                }
            }
            catch (ArgumentException)
            {
                // Invalid user regex: treat it literally rather than crashing the capture loop.
                _titleRegexes.Add(new Regex(Regex.Escape(pattern), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
            }
        }
    }

    /// <summary>Number of configured patterns.</summary>
    public int PatternCount => _processPatterns.Count + _titleRegexes.Count;

    /// <summary>True when this window must not be recorded.</summary>
    public bool IsExcluded(string? processName, string? windowTitle)
    {
        if (PatternCount == 0)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(processName))
        {
            string normalized = Normalize(processName);
            foreach (Regex regex in _processRegexes)
            {
                if (regex.IsMatch(normalized))
                {
                    return true;
                }
            }
        }

        if (!string.IsNullOrEmpty(windowTitle))
        {
            foreach (Regex regex in _titleRegexes)
            {
                if (regex.IsMatch(windowTitle))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Human-readable description of the active rules, for the settings UI.</summary>
    public string Describe()
        => PatternCount == 0
            ? "no exclusions configured"
            : $"{_processPatterns.Count} process rule(s), {_titleRegexes.Count} title rule(s)";

    private static string Normalize(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[..^4];
        }

        return trimmed;
    }
}
