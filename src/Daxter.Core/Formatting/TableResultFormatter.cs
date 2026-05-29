using System.Globalization;
using Daxter.Core.Query;
using Spectre.Console;

namespace Daxter.Core.Formatting;

/// <summary>Renders a result as a bordered, human-readable table using Spectre.Console.</summary>
public sealed class TableResultFormatter : IResultFormatter
{
    public string Format(QueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (result.ColumnCount == 0)
        {
            return "(no columns)" + Environment.NewLine;
        }

        var table = new Table().Border(TableBorder.Rounded);
        foreach (var column in result.Columns)
        {
            table.AddColumn(Markup.Escape(column));
        }

        foreach (var row in result.Rows)
        {
            var cells = new string[result.ColumnCount];
            for (var i = 0; i < result.ColumnCount; i++)
            {
                var value = i < row.Length ? row[i] : null;
                cells[i] = Markup.Escape(Render(value));
            }

            table.AddRow(cells);
        }

        // Render to a string via an in-memory console so the formatter stays pure/testable.
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = AnsiSupport.No,
            ColorSystem = ColorSystemSupport.NoColors,
            Out = new AnsiConsoleOutput(writer),
        });

        console.Write(table);
        writer.Flush();
        return writer.ToString();
    }

    private static string Render(object? value) => value switch
    {
        null => string.Empty,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}
