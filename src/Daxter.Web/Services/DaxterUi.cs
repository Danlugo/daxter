using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Metadata;
using Daxter.Core.Query;
using Daxter.Core.Rest;

namespace Daxter.Web.Services;

public sealed record HealthCheck(string Name, bool Ok, string Detail);

/// <summary>Bridges the Blazor pages to the Daxter.Core engine (same logic as the CLI/MCP).</summary>
public sealed class DaxterUi
{
    public DaxterConfig Config(bool requireWorkspace = false)
        => DaxterConfig.FromEnvironment(requireWorkspace: requireWorkspace);

    private static ITokenProvider Provider(DaxterConfig config, bool interactive = false)
        => new MsalTokenProvider(config, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: interactive);

    /// <summary>Status-page checks: config, token/sign-in, REST reachability, and an XMLA round-trip.</summary>
    public async Task<List<HealthCheck>> RunHealthAsync(CancellationToken ct = default)
    {
        var checks = new List<HealthCheck>();

        DaxterConfig cfg;
        try
        {
            cfg = Config();
        }
        catch (Exception ex)
        {
            checks.Add(new("Configuration", false, ex.Message));
            return checks;
        }

        var target = string.IsNullOrWhiteSpace(cfg.Workspace) ? "(no default workspace)" : $"workspace '{cfg.Workspace}'";
        checks.Add(new("Configuration", true, $"{cfg.AuthMode}, {target}"));

        try
        {
            await Provider(cfg).GetTokenAsync(ct);
            checks.Add(new("Authentication", true, cfg.AuthMode == AuthMode.ServicePrincipal ? "service principal token acquired" : "signed in (token cached)"));
        }
        catch (Exception ex)
        {
            checks.Add(new("Authentication", false, ex.Message));
            return checks;
        }

        try
        {
            using var rest = new PowerBiRestClient(Provider(cfg));
            var ws = await rest.GroupsAsync(ct);
            checks.Add(new("Power BI REST", true, $"{ws.RowCount} workspaces visible"));
        }
        catch (Exception ex)
        {
            checks.Add(new("Power BI REST", false, ex.Message));
        }

        if (!string.IsNullOrWhiteSpace(cfg.Workspace) && !string.IsNullOrWhiteSpace(cfg.Dataset))
        {
            try
            {
                var factory = new AdomdXmlaSessionFactory(cfg, Provider(cfg));
                using var session = await factory.CreateAsync(ct);
                session.Execute("EVALUATE ROW(\"ok\", 1)");
                checks.Add(new("XMLA query", true, $"connected to '{cfg.Dataset}'"));
            }
            catch (Exception ex)
            {
                checks.Add(new("XMLA query", false, ex.Message));
            }
        }

        return checks;
    }

    /// <summary>Starts device-code sign-in; returns the URL + code to display.</summary>
    public Task<string> BeginLoginAsync(CancellationToken ct = default)
        => new MsalTokenProvider(Config(), deviceCodePrompt: Console.Error.WriteLine).BeginInteractiveLoginAsync(ct);

    public async Task<QueryResult> WorkspacesAsync(CancellationToken ct = default)
    {
        using var rest = new PowerBiRestClient(Provider(Config()));
        return await rest.GroupsAsync(ct);
    }

    public async Task<QueryResult> DatasetsAsync(string workspace, CancellationToken ct = default)
    {
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(workspace, ct);
        return await rest.DatasetsAsync(groupId, ct);
    }

    public async Task<QueryResult> RefreshHistoryAsync(string workspace, string dataset, int top = 10, CancellationToken ct = default)
    {
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(workspace, ct);
        var datasetId = await rest.ResolveDatasetIdAsync(groupId, dataset, ct);
        return await rest.RefreshHistoryAsync(groupId, datasetId, top, ct);
    }

    public Task<QueryResult> TablesAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync(ws, ds, s => s.Execute("SELECT [Name] AS [Table] FROM $SYSTEM.TMSCHEMA_TABLES ORDER BY [Name]"), ct);

    public Task<QueryResult> MeasuresAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync(ws, ds, s => new ModelMetadataService(s).Measures(true), ct);

    public Task<QueryResult> QueryAsync(string ws, string ds, string dax, CancellationToken ct = default)
        => XmlaAsync(ws, ds, s => s.Execute(dax), ct);

    public Task<QueryResult> ParametersAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync(ws, ds, s => new ModelMetadataService(s).Parameters(), ct);

    public Task<QueryResult> PartitionsAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync(ws, ds, s => new ModelMetadataService(s).Partitions(null), ct);

    public Task<QueryResult> RlsAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync(ws, ds, s => new ModelMetadataService(s).Roles(), ct);

    public async Task<QueryResult> ReportsAsync(string ws, CancellationToken ct = default)
    {
        using var rest = new PowerBiRestClient(Provider(Config()));
        return await rest.ReportsAsync(await rest.ResolveGroupIdAsync(ws, ct), ct);
    }

    public async Task<QueryResult> LineageAsync(string ws, CancellationToken ct = default)
    {
        using var rest = new PowerBiRestClient(Provider(Config()));
        return await rest.LineageAsync(await rest.ResolveGroupIdAsync(ws, ct), ct);
    }

    public async Task<QueryResult> DatasourcesAsync(string ws, string ds, CancellationToken ct = default)
    {
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(ws, ct);
        var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
        return await rest.DatasourcesAsync(groupId, datasetId, ct);
    }

    public async Task<QueryResult> PermissionsAsync(string ws, string ds, CancellationToken ct = default)
    {
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(ws, ct);
        var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
        return await rest.DatasetUsersAsync(groupId, datasetId, ct);
    }

    private async Task<QueryResult> XmlaAsync(string ws, string ds, Func<IXmlaSession, QueryResult> op, CancellationToken ct)
    {
        var cfg = DaxterConfig.FromEnvironment(workspace: ws, dataset: ds);
        var factory = new AdomdXmlaSessionFactory(cfg, Provider(cfg));
        using var session = await factory.CreateAsync(ct);
        return op(session);
    }
}
