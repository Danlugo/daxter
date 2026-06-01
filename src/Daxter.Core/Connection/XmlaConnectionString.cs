namespace Daxter.Core.Connection;

/// <summary>
/// Builds the XMLA connection string for the Power BI Service. Pure and
/// side-effect free so it can be unit tested without a live endpoint.
/// </summary>
public static class XmlaConnectionString
{
    /// <summary>Prefix for a Power BI workspace XMLA data source.</summary>
    public const string PowerBiPrefix = "powerbi://api.powerbi.com/v1.0/myorg/";

    /// <summary>
    /// Builds <c>Data Source=...;Initial Catalog=...;</c> from a workspace and
    /// optional dataset. A bare workspace name is expanded to the Power BI URL;
    /// a value already starting with a scheme (e.g. <c>powerbi://</c> or
    /// <c>asazure://</c>) is used verbatim.
    /// </summary>
    public static string Build(
        string workspace,
        string? dataset = null,
        string? roles = null,
        string? effectiveUserName = null)
    {
        if (string.IsNullOrWhiteSpace(workspace))
        {
            throw new ArgumentException("Workspace is required.", nameof(workspace));
        }

        var trimmed = workspace.Trim();
        var dataSource = trimmed.Contains("://", StringComparison.Ordinal)
            ? trimmed
            : PowerBiPrefix + trimmed;

        var connection = $"Data Source={EncodeValue(dataSource)};";

        if (!string.IsNullOrWhiteSpace(dataset))
        {
            connection += $"Initial Catalog={EncodeValue(dataset)};";
        }

        // RLS testing: connect under a role and/or impersonate a user.
        if (!string.IsNullOrWhiteSpace(roles))
        {
            connection += $"Roles={EncodeValue(roles)};";
        }

        if (!string.IsNullOrWhiteSpace(effectiveUserName))
        {
            connection += $"EffectiveUserName={EncodeValue(effectiveUserName)};";
        }

        return connection;
    }

    // Connection-string values that contain a semicolon or a quote character must be
    // enclosed in quotes, or the OLE-DB / ADOMD parser misreads them — e.g. an apostrophe
    // in a model name (like "Reseller's Margin") corrupts the parse and the AdomdConnection
    // ctor throws before we ever connect. Per the ADO.NET rule, enclose in the quote
    // character the value does NOT contain; if it contains both, enclose in double quotes
    // and double the embedded ones. A value with no special characters is left untouched.
    private static readonly char[] MustQuote = [';', '\'', '"'];

    private static string EncodeValue(string raw)
    {
        var value = raw.Trim();
        if (value.IndexOfAny(MustQuote) < 0)
        {
            return value;
        }

        if (!value.Contains('"', StringComparison.Ordinal))
        {
            return $"\"{value}\"";
        }

        if (!value.Contains('\'', StringComparison.Ordinal))
        {
            return $"'{value}'";
        }

        return $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
