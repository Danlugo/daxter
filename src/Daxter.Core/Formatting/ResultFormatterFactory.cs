namespace Daxter.Core.Formatting;

/// <summary>Maps an <see cref="OutputFormat"/> to its <see cref="IResultFormatter"/>.</summary>
public static class ResultFormatterFactory
{
    public static IResultFormatter Create(OutputFormat format) => format switch
    {
        OutputFormat.Table => new TableResultFormatter(),
        OutputFormat.Csv => new CsvResultFormatter(),
        OutputFormat.Json => new JsonResultFormatter(),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown output format."),
    };

    /// <summary>Parses a CLI <c>--output</c> value. Defaults to <see cref="OutputFormat.Table"/>.</summary>
    public static OutputFormat Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "table" => OutputFormat.Table,
        "csv" => OutputFormat.Csv,
        "json" => OutputFormat.Json,
        _ => throw new DaxterException(
            $"Unknown output format '{value}'. Use table, csv, or json."),
    };
}
