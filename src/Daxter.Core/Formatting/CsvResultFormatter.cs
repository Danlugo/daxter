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

    /// <summary>Stringifies a SQL value for CSV — culture-invariant on <see cref="IFormattable"/>
    /// (so dates and numbers don't pick up a server locale) and empty for nulls. Public so the
    /// streaming SQL exporter can use the same conversion the in-memory formatter does.</summary>
    public static string Render(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>RFC 4180 field-escape. Quotes only when needed (contains a quote, comma, or newline)
    /// and doubles inner quotes. Public so the streaming SQL exporter can share it.</summary>
    public static string Escape(string field)
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
