namespace Daxter.Core.Formatting;

/// <summary>Tweaks the CSV output shape so consumers picky about format see what they want.
/// <list type="bullet">
/// <item><see cref="QuoteAll"/>: wrap EVERY field in quotes, even when RFC 4180 wouldn't.
/// The Excel-on-Windows / Power BI "Export data" convention. Default <c>false</c> = RFC 4180
/// (quote only when the value contains <c>,</c> <c>"</c> or a newline).</item>
/// <item><see cref="Crlf"/>: end each line with <c>\r\n</c> instead of <c>\n</c>.
/// What Excel-on-Windows expects; harmless on macOS/Linux readers. Default <c>false</c> = LF.</item>
/// <item><see cref="QuoteHeader"/>: only meaningful when <see cref="QuoteAll"/> is on — whether the
/// header row is ALSO quoted. Default <c>false</c>: <c>QuoteAll</c> wraps the data rows but the header
/// stays bare comma-joined column names. That matches the Fabric/Spark CSV writer ("Save Fabric Data
/// to File"), which quotes data but not the header. Set <c>true</c> for the Power BI / Excel
/// "Export data" convention, which quotes the header too.</item>
/// </list>
/// <c>QuoteAll=true, Crlf=true</c> (header unquoted) is byte-equivalent to the Fabric Spark CSV writer;
/// add <c>QuoteHeader=true</c> for the Power BI / Excel "Export data" shape.</summary>
public readonly record struct CsvStyle(bool QuoteAll = false, bool Crlf = false, bool QuoteHeader = false)
{
    /// <summary>RFC 4180 + LF — the default everywhere.</summary>
    public static readonly CsvStyle Default = new();

    /// <summary>Quote-every-field (header included) + CRLF — matches Power BI / Excel "Export data".</summary>
    public static readonly CsvStyle ExcelWindows = new(QuoteAll: true, Crlf: true, QuoteHeader: true);

    /// <summary>The line terminator this style emits.</summary>
    public string LineEnding => Crlf ? "\r\n" : "\n";
}
