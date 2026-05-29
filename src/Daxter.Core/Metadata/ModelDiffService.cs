using Daxter.Core.Connection;
using Daxter.Core.Query;

namespace Daxter.Core.Metadata;

/// <summary>
/// Compares two models' measures (via DMV) and reports the differences:
/// added, removed, or changed expression. Useful for validating promotion
/// between environments. Pure given two sessions — no TOM required.
/// </summary>
public static class ModelDiffService
{
    /// <summary>Diffs measures between two open sessions, returning only the differences.</summary>
    public static QueryResult DiffMeasures(IXmlaSession left, IXmlaSession right)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);

        var a = ReadMeasures(left);
        var b = ReadMeasures(right);

        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        names.UnionWith(a.Keys);
        names.UnionWith(b.Keys);

        var rows = new List<object?[]>();
        foreach (var name in names)
        {
            var inA = a.TryGetValue(name, out var exprA);
            var inB = b.TryGetValue(name, out var exprB);

            string? status = (inA, inB) switch
            {
                (true, false) => "removed",
                (false, true) => "added",
                (true, true) when !string.Equals(exprA, exprB, StringComparison.Ordinal) => "changed",
                _ => null, // identical → omit
            };

            if (status is not null)
            {
                rows.Add([status, name]);
            }
        }

        return new QueryResult(["Status", "Measure"], rows);
    }

    private static Dictionary<string, string> ReadMeasures(IXmlaSession session)
    {
        var result = session.Execute("SELECT [Name], [Expression] FROM $SYSTEM.TMSCHEMA_MEASURES");
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in result.Rows)
        {
            var name = row[0]?.ToString();
            if (!string.IsNullOrEmpty(name))
            {
                map[name] = row.Length > 1 ? row[1]?.ToString() ?? string.Empty : string.Empty;
            }
        }

        return map;
    }
}
