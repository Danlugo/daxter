using System.Text.Json;
using Daxter.Core.Query;

namespace Daxter.Core.Formatting;

/// <summary>Renders a result as a JSON array of row objects keyed by column name.</summary>
public sealed class JsonResultFormatter : IResultFormatter
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public string Format(QueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var rows = new List<Dictionary<string, object?>>(result.RowCount);
        foreach (var row in result.Rows)
        {
            var obj = new Dictionary<string, object?>(result.ColumnCount, StringComparer.Ordinal);
            for (var i = 0; i < result.Columns.Count; i++)
            {
                obj[result.Columns[i]] = i < row.Length ? row[i] : null;
            }

            rows.Add(obj);
        }

        return JsonSerializer.Serialize(rows, Options);
    }
}
