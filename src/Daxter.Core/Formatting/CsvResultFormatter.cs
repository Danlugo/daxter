using System.Globalization;
using System.Text;
using Daxter.Core.Query;

namespace Daxter.Core.Formatting;

/// <summary>Renders a result as RFC 4180 CSV. Pure and culture-invariant.</summary>
public sealed class CsvResultFormatter : IResultFormatter
{
    public string Format(QueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var builder = new StringBuilder();
        builder.AppendLine(string.Join(",", result.Columns.Select(Escape)));

        foreach (var row in result.Rows)
        {
            builder.AppendLine(string.Join(",", row.Select(cell => Escape(Render(cell)))));
        }

        return builder.ToString();
    }

    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static string Escape(string field)
    {
        var needsQuoting = field.Contains('"', StringComparison.Ordinal)
            || field.Contains(',', StringComparison.Ordinal)
            || field.Contains('\n', StringComparison.Ordinal)
            || field.Contains('\r', StringComparison.Ordinal);

        if (!needsQuoting)
        {
            return field;
        }

        return "\"" + field.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
