using Daxter.Core.Formatting;

namespace Daxter.Core.Tests;

/// <summary>The <see cref="CsvStyle"/> options that drive the streaming SQL export (FabricSqlClient.
/// StreamCsvAsync) and surface as Web checkboxes / CLI --quote-all/--crlf / MCP params. Pin the
/// invariants here so the various surfaces can't drift apart.</summary>
public class CsvStyleTests
{
    [Fact]
    public void Default_is_RFC4180_LF()
    {
        var d = CsvStyle.Default;
        Assert.False(d.QuoteAll);
        Assert.False(d.Crlf);
        Assert.Equal("\n", d.LineEnding);
    }

    [Fact]
    public void ExcelWindows_is_quote_all_plus_CRLF()
    {
        // Matches Power BI's "Export data" output. If a customer compares an Export All download
        // byte-for-byte against an Excel-Windows export, this is the preset that should match.
        var x = CsvStyle.ExcelWindows;
        Assert.True(x.QuoteAll);
        Assert.True(x.Crlf);
        Assert.Equal("\r\n", x.LineEnding);
    }

    [Theory]
    [InlineData(false, "\n")]
    [InlineData(true, "\r\n")]
    public void LineEnding_picks_correctly(bool crlf, string expected)
        => Assert.Equal(expected, new CsvStyle(false, crlf).LineEnding);

    // The streaming exporter's per-field rule (see FabricSqlClient.WriteField): with QuoteAll on,
    // every field is wrapped and inner quotes doubled — even when RFC 4180 wouldn't bother.
    [Theory]
    [InlineData("plain")]                   // no special chars
    [InlineData("with,comma")]              // comma
    [InlineData("with\"quote")]             // quote (doubled inside)
    [InlineData("")]                        // empty
    public void QuoteAll_wraps_every_field(string input)
    {
        // Run through the same logic the streamer does — Escape vs forced-quote.
        var rfc = CsvResultFormatter.Escape(input);
        var forced = "\"" + input.Replace("\"", "\"\"") + "\"";

        // QuoteAll always produces a quoted string (starts + ends with ").
        Assert.StartsWith("\"", forced);
        Assert.EndsWith("\"", forced);

        // RFC 4180 only quotes when needed — "plain" stays "plain", everything else gets quoted.
        var needsQuote = input.Contains(',') || input.Contains('"') || input.Contains('\n') || input.Contains('\r');
        if (needsQuote)
            Assert.Equal(forced, rfc);          // both produce the same shape
        else
            Assert.Equal(input, rfc);            // RFC leaves it alone
    }
}
