namespace Daxter.Core.Sql;

/// <summary>Light-touch SQL classification for the DAXter write-gate.
/// We do NOT parse T-SQL — we look at the first meaningful keyword of each non-comment statement.
/// Read = SELECT, EXPLAIN, SHOW, WITH (CTE feeding a final SELECT), USE, SET (session option).
/// Write = anything else (INSERT, UPDATE, DELETE, MERGE, TRUNCATE, DROP, ALTER, CREATE, GRANT, REVOKE, EXEC, …).
/// The class lives here (not in the Web page) so the same gate applies in the CLI and MCP tools.</summary>
public static class SqlWriteGate
{
    private static readonly HashSet<string> ReadOnlyKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "SELECT", "WITH", "EXPLAIN", "SHOW", "USE", "SET", "DECLARE", "PRINT",
    };

    /// <summary>Returns <c>true</c> if every statement in <paramref name="sql"/> begins with a
    /// read-only keyword. A trivially mixed batch (e.g. a CTE feeding an INSERT) returns <c>false</c>:
    /// WITH followed by anything other than a final SELECT is treated as a write.</summary>
    public static bool IsReadOnly(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql)) return true;

        var stripped = StripComments(sql);
        foreach (var stmt in stripped.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var first = FirstWord(stmt);
            if (string.IsNullOrEmpty(first)) continue;
            if (!ReadOnlyKeywords.Contains(first)) return false;

            // A CTE (WITH) is read-only only if its body ends in SELECT. Cheap check: the last word
            // of the statement is SELECT, or the statement contains ") SELECT" / ") select".
            if (string.Equals(first, "WITH", StringComparison.OrdinalIgnoreCase))
            {
                var last = LastWord(stmt);
                var hasSelectAfterCte = ContainsSelectAfterCte(stmt);
                if (!string.Equals(last, "SELECT", StringComparison.OrdinalIgnoreCase) && !hasSelectAfterCte)
                    return false;
            }
        }
        return true;
    }

    private static string StripComments(string sql)
    {
        // -- line comments
        var noLine = System.Text.RegularExpressions.Regex.Replace(sql, "--[^\n]*", "");
        // /* block comments */
        var noBlock = System.Text.RegularExpressions.Regex.Replace(noLine, @"/\*.*?\*/", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return noBlock;
    }

    private static string FirstWord(string s)
    {
        var trimmed = s.TrimStart();
        var idx = 0;
        while (idx < trimmed.Length && (char.IsLetter(trimmed[idx]) || trimmed[idx] == '_')) idx++;
        return idx == 0 ? "" : trimmed[..idx];
    }

    private static string LastWord(string s)
    {
        var trimmed = s.TrimEnd();
        var end = trimmed.Length;
        while (end > 0 && (char.IsLetter(trimmed[end - 1]) || trimmed[end - 1] == '_')) end--;
        return end == trimmed.Length ? "" : trimmed[end..];
    }

    private static bool ContainsSelectAfterCte(string stmt)
    {
        // Find the matching close-paren that ends the LAST CTE definition, then look for SELECT after it.
        // This is intentionally cheap: WITH foo AS (…), bar AS (…) SELECT … FROM bar
        var lastClose = stmt.LastIndexOf(')');
        if (lastClose < 0) return false;
        var tail = stmt[(lastClose + 1)..].TrimStart();
        return tail.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase);
    }
}
