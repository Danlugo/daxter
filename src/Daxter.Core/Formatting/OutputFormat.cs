namespace Daxter.Core.Formatting;

/// <summary>Supported rendering formats for a <see cref="Query.QueryResult"/>.</summary>
public enum OutputFormat
{
    /// <summary>Human-readable bordered table (default).</summary>
    Table,

    /// <summary>RFC 4180 comma-separated values.</summary>
    Csv,

    /// <summary>JSON array of row objects.</summary>
    Json,
}
