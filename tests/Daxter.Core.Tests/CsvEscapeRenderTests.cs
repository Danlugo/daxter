using System.Globalization;
using Daxter.Core.Formatting;

namespace Daxter.Core.Tests;

/// <summary>The Escape / Render helpers are shared between the in-memory CsvResultFormatter and the
/// streaming SQL exporter (FabricSqlClient.StreamCsvAsync). If either drifts, exported CSVs from the
/// /sql page's "Export All CSV" / `daxter sql query --out` / daxter_sql_export wouldn't match what
/// the grid CSV shows — pin them.</summary>
public class CsvEscapeRenderTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("with,comma", "\"with,comma\"")]
    [InlineData("with\"quote", "\"with\"\"quote\"")]
    [InlineData("line\nbreak", "\"line\nbreak\"")]
    [InlineData("CR\rLF", "\"CR\rLF\"")]
    [InlineData("", "")]
    [InlineData("nothing-special", "nothing-special")]
    public void Escape_only_quotes_when_needed(string input, string expected)
        => Assert.Equal(expected, CsvResultFormatter.Escape(input));

    [Fact]
    public void Render_null_is_empty()
        => Assert.Equal("", CsvResultFormatter.Render(null));

    // Numbers must be culture-invariant — a German server locale otherwise produces "1,5" for 1.5
    // and breaks downstream CSV consumers expecting "1.5".
    [Fact]
    public void Render_double_is_culture_invariant()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("1.5", CsvResultFormatter.Render(1.5));
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }

    // Dates: ToString(null, InvariantCulture) on a DateTime emits the round-trip format DAXter wants
    // (no locale-formatted "08.06.2026 12:34"). The exporter and the grid must agree on this.
    [Fact]
    public void Render_datetime_is_culture_invariant()
    {
        var prev = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            var dt = new DateTime(2026, 6, 8, 12, 34, 56);
            // InvariantCulture's default DateTime format is "MM/dd/yyyy HH:mm:ss" — no locale leak.
            Assert.Equal("06/08/2026 12:34:56", CsvResultFormatter.Render(dt));
        }
        finally { CultureInfo.CurrentCulture = prev; }
    }
}
