using System.Text.RegularExpressions;

namespace Daxter.Core.Configuration;

/// <summary>Pure glob matcher used by the writes-gate (DaxterConfig.IsReadOnlyTarget) and the
/// MCP / CLI refuse paths. One pattern per entry:
/// <list type="bullet">
/// <item><c>*</c> matches zero or more characters.</item>
/// <item>All other chars match literally.</item>
/// <item>Case-insensitive (Ordinal-IgnoreCase) — workspace names from the Power BI Service are not
/// case-sensitive.</item>
/// <item>The pattern is anchored to the whole name (no implicit prefix/suffix wildcards) — write
/// <c>Data*</c> not <c>Data</c> to match by prefix.</item>
/// </list>
/// Examples (workspace name on the left, patterns that match on the right):
/// <list type="table">
/// <item><c>Data Hub - Dev</c></item> → <c>Data Hub - Dev</c>, <c>Data*Dev</c>, <c>*Dev</c>, <c>*Hub*</c>, <c>*</c>
/// <item><c>Sales Analytics</c></item> → <c>Sales*</c>, <c>*Analytics</c>, <c>*Sales*</c>, <c>*</c>
/// </list></summary>
public static class WorkspaceMatcher
{
    /// <summary>True when <paramref name="name"/> matches at least one of the <paramref name="patterns"/>.
    /// An empty / null pattern list returns false (no match — "the rule doesn't apply" rather than
    /// "matches everything", which is what every caller actually wants).</summary>
    public static bool MatchesAny(string? name, IReadOnlyCollection<string>? patterns)
        => MatchedPattern(name, patterns) is not null;

    /// <summary>Returns the FIRST pattern from <paramref name="patterns"/> that matches
    /// <paramref name="name"/>, or null if none does. Useful for refuse messages so the user can see
    /// why a workspace was locked.</summary>
    public static string? MatchedPattern(string? name, IReadOnlyCollection<string>? patterns)
    {
        if (string.IsNullOrWhiteSpace(name) || patterns is null || patterns.Count == 0) return null;
        foreach (var p in patterns)
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            if (Matches(name, p)) return p;
        }
        return null;
    }

    /// <summary>True when <paramref name="name"/> matches the single glob <paramref name="pattern"/>.</summary>
    public static bool Matches(string name, string pattern)
    {
        var regex = CompilePattern(pattern);
        return regex.IsMatch(name);
    }

    // Cache: identical patterns reach the gate constantly (every refresh, every model edit, every
    // SQL query). Compiling the regex once and reusing the Regex instance is a meaningful saving.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Regex> _cache = new();

    private static Regex CompilePattern(string pattern)
        => _cache.GetOrAdd(pattern, p =>
        {
            // Escape EVERYTHING regex-special EXCEPT the '*' wildcard, then map '*' → '.*'.
            // Anchored start-to-end so a partial substring doesn't accidentally match.
            var escaped = Regex.Escape(p).Replace("\\*", ".*", StringComparison.Ordinal);
            return new Regex("^" + escaped + "$",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
        });

    /// <summary>Parses a comma-separated pattern string into a trimmed, deduplicated list — same
    /// convention the env vars and the Configure-page inputs use. Empty / null returns an empty list.</summary>
    public static List<string> Parse(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return new();
        return csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
