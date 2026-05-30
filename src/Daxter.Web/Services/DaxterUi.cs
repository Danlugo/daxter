using System.Diagnostics;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Metadata;
using Daxter.Core.Query;
using Daxter.Core.Rest;
using Microsoft.Extensions.Logging;

namespace Daxter.Web.Services;

public sealed record HealthCheck(string Name, bool Ok, string Detail);

/// <summary>Bridges the Blazor pages to the Daxter.Core engine (same logic as the CLI/MCP).</summary>
public sealed class DaxterUi
{
    private readonly ConfigState _state;
    private readonly ILogger<DaxterUi> _log;

    public DaxterUi(ConfigState state, ILogger<DaxterUi> log)
    {
        _state = state;
        _log = log;
    }

    /// <summary>The console's current effective config (editable via the Configure page).</summary>
    public DaxterConfig Config(bool requireWorkspace = false) => _state.ToConfig();

    private static ITokenProvider Provider(DaxterConfig config, bool interactive = false)
        => new MsalTokenProvider(config, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: interactive);

    /// <summary>Status-page checks: config, token/sign-in, REST reachability, and an XMLA round-trip.</summary>
    public async Task<List<HealthCheck>> RunHealthAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Health check started");
        var checks = new List<HealthCheck>();

        DaxterConfig cfg;
        try
        {
            cfg = Config();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Health: configuration invalid");
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
            _log.LogWarning("Health: not authenticated — {Error}", ex.Message);
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
            _log.LogError(ex, "Health: Power BI REST unreachable");
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
                _log.LogError(ex, "Health: XMLA round-trip failed");
                checks.Add(new("XMLA query", false, ex.Message));
            }
        }

        _log.LogInformation("Health check finished: {Ok}/{Total} checks ok",
            checks.Count(c => c.Ok), checks.Count);
        return checks;
    }

    /// <summary>
    /// Starts device-code sign-in; returns the URL + code to display <b>and</b> a completion task
    /// that finishes when the user has authenticated (or faults with the reason it failed).
    /// </summary>
    public Task<DeviceLogin> BeginLoginAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Device-code sign-in started");
        return new MsalTokenProvider(Config(), deviceCodePrompt: Console.Error.WriteLine).StartDeviceLoginAsync(ct);
    }

    public Task<QueryResult> WorkspacesAsync(CancellationToken ct = default)
        => Track("workspaces", null, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.GroupsAsync(ct);
        });

    public Task<QueryResult> DatasetsAsync(string workspace, CancellationToken ct = default)
        => Track("datasets", workspace, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(workspace, ct);
            return await rest.DatasetsAsync(groupId, ct);
        });

    public Task<QueryResult> RefreshHistoryAsync(string workspace, string dataset, int top = 10, CancellationToken ct = default)
        => Track("refresh-history", $"{workspace}/{dataset}", async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(workspace, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, dataset, ct);
            return await rest.RefreshHistoryAsync(groupId, datasetId, top, ct);
        });

    public Task<QueryResult> TablesAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("tables", ws, ds, s => s.Execute("SELECT [Name] AS [Table] FROM $SYSTEM.TMSCHEMA_TABLES ORDER BY [Name]"), ct);

    public Task<QueryResult> MeasuresAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("measures", ws, ds, s => new ModelMetadataService(s).Measures(true), ct);

    public Task<QueryResult> QueryAsync(string ws, string ds, string dax, CancellationToken ct = default)
        => XmlaAsync("query", ws, ds, s => s.Execute(dax), ct);

    public Task<QueryResult> ParametersAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("parameters", ws, ds, s => new ModelMetadataService(s).Parameters(), ct);

    public Task<QueryResult> PartitionsAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("partitions", ws, ds, s => new ModelMetadataService(s).Partitions(null), ct);

    public Task<QueryResult> RlsAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("rls", ws, ds, s => new ModelMetadataService(s).Roles(), ct);

    public Task<QueryResult> McodeAsync(string ws, string ds, string table, CancellationToken ct = default)
        => XmlaAsync($"mcode:{table}", ws, ds, s => new ModelMetadataService(s).MCode(table), ct);

    public Task<QueryResult> TablePartitionsAsync(string ws, string ds, string table, CancellationToken ct = default)
        => XmlaAsync($"partitions:{table}", ws, ds, s => new ModelMetadataService(s).Partitions(table), ct);

    public Task<QueryResult> ReportsAsync(string ws, CancellationToken ct = default)
        => Track("reports", ws, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.ReportsAsync(await rest.ResolveGroupIdAsync(ws, ct), ct);
        });

    public Task<QueryResult> LineageAsync(string ws, CancellationToken ct = default)
        => Track("lineage", ws, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.LineageAsync(await rest.ResolveGroupIdAsync(ws, ct), ct);
        });

    public Task<QueryResult> DatasourcesAsync(string ws, string ds, CancellationToken ct = default)
        => Track("datasources", $"{ws}/{ds}", async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(ws, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
            return await rest.DatasourcesAsync(groupId, datasetId, ct);
        });

    public Task<QueryResult> PermissionsAsync(string ws, string ds, CancellationToken ct = default)
        => Track("permissions", $"{ws}/{ds}", async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(ws, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
            return await rest.DatasetUsersAsync(groupId, datasetId, ct);
        });

    private Task<QueryResult> XmlaAsync(string op, string ws, string ds, Func<IXmlaSession, QueryResult> body, CancellationToken ct)
        => Track(op, $"{ws}/{ds}", async () =>
        {
            var cfg = _state.ToConfig(ws, ds);
            var factory = new AdomdXmlaSessionFactory(cfg, Provider(cfg));
            using var session = await factory.CreateAsync(ct);
            return body(session);
        });

    /// <summary>Runs an operation with start/success/failure logging (op, target, rows, elapsed).</summary>
    private async Task<QueryResult> Track(string op, string? target, Func<Task<QueryResult>> body)
    {
        var label = target is null ? op : $"{op} [{target}]";
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await body();
            _log.LogInformation("{Label} → {Rows} rows in {Ms} ms", label, result.RowCount, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogError("{Label} failed after {Ms} ms: {Error}", label, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }
}
