namespace Daxter.Core.Formatting;

/// <summary>Tweaks the CSV output shape so consumers picky about format see what they want.
/// <list type="bullet">
/// <item><see cref="QuoteAll"/>: wrap EVERY field in quotes, even when RFC 4180 wouldn't.
/// The Excel-on-Windows / Power BI "Export data" convention. Default <c>false</c> = RFC 4180
/// (quote only when the value contains <c>,</c> <c>"</c> or a newline).</item>
/// <item><see cref="Crlf"/>: end each line with <c>\r\n</c> instead of <c>\n</c>.
/// What Excel-on-Windows expects; harmless on macOS/Linux readers. Default <c>false</c> = LF.</item>
/// </list>
/// Together (<c>QuoteAll=true, Crlf=true</c>) produces the exact format Power BI and Excel's
/// "Export data" emit, so a DAXter export can be byte-equivalent to a hand-pulled file.</summary>
public readonly record struct CsvStyle(bool QuoteAll = false, bool Crlf = false)
{
    /// <summary>RFC 4180 + LF — the default everywhere.</summary>
    public static readonly CsvStyle Default = new();

    /// <summary>Quote-every-field + CRLF — matches Power BI / Excel "Export data" output.</summary>
    public static readonly CsvStyle ExcelWindows = new(QuoteAll: true, Crlf: true);

    /// <summary>The line terminator this style emits.</summary>
    public string LineEnding => Crlf ? "\r\n" : "\n";
}
