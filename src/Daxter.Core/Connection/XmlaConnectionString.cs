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

        var connection = $"Data Source={dataSource};";

        if (!string.IsNullOrWhiteSpace(dataset))
        {
            connection += $"Initial Catalog={dataset.Trim()};";
        }

        // RLS testing: connect under a role and/or impersonate a user.
        if (!string.IsNullOrWhiteSpace(roles))
        {
            connection += $"Roles={roles.Trim()};";
        }

        if (!string.IsNullOrWhiteSpace(effectiveUserName))
        {
            connection += $"EffectiveUserName={effectiveUserName.Trim()};";
        }

        return connection;
    }
}
