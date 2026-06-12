using System.Diagnostics;
using Daxter.Core;
using Daxter.Core.Artifacts;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Editing;
using Daxter.Core.Metadata;
using Daxter.Core.Query;
using Daxter.Core.Rest;
using Daxter.Core.Sql;
using Microsoft.Extensions.Logging;

namespace Daxter.Web.Services;

public sealed record HealthCheck(string Name, bool Ok, string Detail);

/// <summary>A column or measure in the model fields tree.</summary>
public sealed record ModelObject(string Name, bool IsMeasure);
/// <summary>A table with its columns + measures.</summary>
public sealed record ModelTableNode(string Table, IReadOnlyList<ModelObject> Items);
/// <summary>The model's fields tree for the DAX explorer.</summary>
public sealed record ModelTree(IReadOnlyList<ModelTableNode> Tables);

/// <summary>A pipeline and the models found in its first (Dev) stage — for the model-first picker.</summary>
public sealed record PipelineModels(string PipelineId, string PipelineName, IReadOnlyList<string> Models);

/// <summary>Bridges the Blazor pages to the Daxter.Core engine (same logic as the CLI/MCP).</summary>
public sealed class DaxterUi
{
    private readonly ConfigState _state;
    private readonly ILogger<DaxterUi> _log;
    private readonly IArtifactStore _artifacts;

    public DaxterUi(ConfigState state, ILogger<DaxterUi> log, IArtifactStore artifacts)
    {
        _state = state;
        _log = log;
        _artifacts = artifacts;
    }

    /// <summary>The console's current effective config (editable via the Configure page).</summary>
    public DaxterConfig Config(bool requireWorkspace = false) => _state.ToConfig();

    /// <summary>True when general (non-refresh) writes are enabled (Configure → Allow writes).
    /// Forced off by read-only mode (<c>DAXTER_READONLY</c>) via <see cref="ConfigState"/>.</summary>
    public bool WritesEnabled => _state.AllowWrites;

    /// <summary>True when REFRESHES / operational jobs are permitted (permission level ≥ execute).
    /// The per-workspace Production guardrail (<see cref="IsReadOnlyTarget"/>) is applied on top.</summary>
    public bool RefreshEnabled => _state.CanRefresh;

    /// <summary>True when model editing is enabled (Configure → Allow model edits) — a stricter,
    /// separate gate than refresh writes (XMLA edits are irreversible for PBIX download).</summary>
    public bool ModelEditEnabled => _state.AllowModelEdit;

    /// <summary>True when the target workspace matches the read-only patterns (deny-list, the
    /// legacy prod-workspaces list, OR is outside the allow-list when one is configured). Pages
    /// like Refresh use this to mark the operation as locked in the UI before the user clicks.</summary>
    public bool IsReadOnlyTarget(string ws, string ds) => _state.ToConfig(ws, ds).IsReadOnlyTarget();

    /// <summary>Backwards-compat alias for <see cref="IsReadOnlyTarget"/> — older pages still call
    /// it as "production target" but the underlying check is the same read-only gate.</summary>
    public bool IsProductionTarget(string ws, string ds) => IsReadOnlyTarget(ws, ds);

    private static ITokenProvider Provider(DaxterConfig config, bool interactive = false)
        => new MsalTokenProvider(config, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: interactive);

    // MsalTokenProvider implements BOTH ITokenProvider (XMLA/REST) and IFabricSqlTokenProvider
    // (database.windows.net scope for Fabric SQL endpoints). The SQL surface needs the second face.
    // Same MSAL account underneath, so signing in once for the XMLA/REST surface silently entitles
    // every SQL call too — no second device-code prompt.
    private static IFabricSqlTokenProvider SqlProvider(DaxterConfig config, bool interactive = false)
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

    /// <summary>Starts device-code sign-in for the <b>Fabric SQL endpoint scope</b> (database.windows.net)
    /// using the SQL-side client id. Needed because Power BI's first-party client id is NOT
    /// pre-authorized for that scope (AADSTS65002) — so the SQL surface uses Azure CLI's client id by
    /// default and the user signs in once for it. Returns the URL + code to show, plus a Completion
    /// task to await; "Already signed in." when the SQL-scope cache is already warm.</summary>
    public Task<DeviceLogin> BeginSqlLoginAsync(CancellationToken ct = default)
    {
        _log.LogInformation("Fabric SQL device-code sign-in started");
        return new MsalTokenProvider(Config(), deviceCodePrompt: Console.Error.WriteLine).StartFabricSqlDeviceLoginAsync(ct);
    }

    public Task<QueryResult> WorkspacesAsync(CancellationToken ct = default)
        => Track("workspaces", null, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return SortedByName(await rest.GroupsAsync(ct));   // alphabetised for the picker
        });

    public Task<QueryResult> DatasetsAsync(string workspace, CancellationToken ct = default)
        => Track("datasets", workspace, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(workspace, ct);
            // Sorted + only real semantic models for the pickers: hide Fabric's internal dataflow
            // staging artifacts that /datasets surfaces. (Lakehouse/warehouse *default* models share
            // their item's name and have no public flag, so a few may still appear — Microsoft is
            // decoupling/sunsetting them.) The CLI/MCP inventory keeps the full list.
            return SortedByName(await rest.DatasetsAsync(groupId, ct), n => !NonModelDatasets.Contains(n));
        });

    // Fabric internal artifacts that /datasets surfaces but are never user semantic models.
    private static readonly HashSet<string> NonModelDatasets = new(StringComparer.OrdinalIgnoreCase)
    {
        "StagingLakehouseForDataflows", "StagingWarehouseForDataflows",
        "DataflowsStagingLakehouse", "DataflowsStagingWarehouse",
    };

    /// <summary>Result sorted by its "name" column (Ordinal, case-insensitive), optionally dropping rows
    /// whose name fails <paramref name="keep"/>. Used so workspace/dataset pickers are alphabetised and
    /// free of non-model noise — apply to every selectable dropdown (see ui-contract).</summary>
    private static QueryResult SortedByName(QueryResult r, Func<string, bool>? keep = null)
    {
        var ni = -1;
        for (var i = 0; i < r.Columns.Count; i++)
            if (string.Equals(r.Columns[i], "name", StringComparison.OrdinalIgnoreCase)) { ni = i; break; }
        if (ni < 0) return r;
        string Name(object?[] row) => ni < row.Length ? row[ni]?.ToString() ?? "" : "";
        var rows = r.Rows
            .Where(row => keep is null || keep(Name(row)))
            .OrderBy(Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new QueryResult(r.Columns, rows);
    }

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

    /// <summary>Measures with their home table resolved (Name, DisplayFolder, Expression, Table).</summary>
    public Task<QueryResult> MeasuresWithTableAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("measures", ws, ds, s =>
        {
            var measures = s.Execute(
                "SELECT [Name], [DisplayFolder], [Expression], [TableID] FROM $SYSTEM.TMSCHEMA_MEASURES ORDER BY [Name]");
            var tables = s.Execute("SELECT [ID], [Name] FROM $SYSTEM.TMSCHEMA_TABLES");

            var idToName = new Dictionary<long, string>();
            foreach (var t in tables.Rows)
                if (t[0] is not null) idToName[Convert.ToInt64(t[0])] = t[1]?.ToString() ?? "";

            var rows = new List<object?[]>();
            foreach (var m in measures.Rows)
            {
                var table = m[3] is not null && idToName.TryGetValue(Convert.ToInt64(m[3]), out var nm) ? nm : "";
                rows.Add(new object?[] { m[0], m[1], m[2], table });
            }
            return new QueryResult(new[] { "Name", "DisplayFolder", "Expression", "Table" }, rows);
        }, ct);

    public Task<QueryResult> QueryAsync(string ws, string ds, string dax, CancellationToken ct = default)
        => XmlaAsync("query", ws, ds, s => s.Execute(dax), ct);

    public Task<QueryResult> ParametersAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("parameters", ws, ds, s => new ModelMetadataService(s).Parameters(), ct);

    public Task<QueryResult> PartitionsAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("partitions", ws, ds, s => new ModelMetadataService(s).Partitions(null), ct);

    public Task<QueryResult> RlsAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("rls", ws, ds, s => new ModelMetadataService(s).Roles(), ct);

    public Task<QueryResult> RoleMembersAsync(string ws, string ds, string role, CancellationToken ct = default)
        => XmlaAsync($"role-members:{role}", ws, ds, s => new ModelMetadataService(s).RoleMembers(role), ct);

    public Task<QueryResult> RoleFiltersAsync(string ws, string ds, string role, CancellationToken ct = default)
        => XmlaAsync($"role-filters:{role}", ws, ds, s => new ModelMetadataService(s).RoleFilters(role), ct);

    public Task<QueryResult> RelationshipsAsync(string ws, string ds, CancellationToken ct = default)
        => XmlaAsync("relationships", ws, ds, s => new ModelMetadataService(s).Relationships(), ct);

    public Task<QueryResult> ColumnsAsync(string ws, string ds, string table, CancellationToken ct = default)
        => XmlaAsync($"columns:{table}", ws, ds, s => new ModelMetadataService(s).Columns(table), ct);

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

    /// <summary>Classifies a workspace's reports as thin / thick / paginated + downloadable, so you can
    /// pick which are safe to export for analysis (thin ones are decoupled from model edits).</summary>
    public Task<QueryResult> ReportInventoryAsync(string ws, CancellationToken ct = default)
        => Track("report-inventory", ws, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.ReportInventoryAsync(await rest.ResolveGroupIdAsync(ws, ct), ct);
        });

    /// <summary>Fetches a report's definition (PBIR / legacy) and bundles every part into one JSON object
    /// (<c>{ path: content }</c>) for the browser to download. The field references inside (e.g.
    /// <c>report.json</c>) are the substrate for column-usage analysis. REST yields, so no offload needed.</summary>
    public async Task<string> ReportDefinitionBundleAsync(string ws, string report, CancellationToken ct = default)
    {
        _log.LogInformation("export-report definition: {Ws}/{Report}", ws, report);
        using var rest = new PowerBiRestClient(Provider(Config()));
        var g = await rest.ResolveGroupIdAsync(ws, ct);
        var rid = await rest.ResolveReportIdAsync(g, report, ct);
        var parts = await rest.ReportDefinitionAsync(g, rid, ct);
        var map = parts.ToDictionary(p => p.Path, p => p.Content);
        return System.Text.Json.JsonSerializer.Serialize(map);
    }

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

    // ---- scheduled refresh config (Power BI Service "Scheduled refresh", import models) ----

    /// <summary>Reads the configured scheduled refresh (import models). DirectQuery/Direct Lake/Push
    /// surface a clear hint from Core.</summary>
    public Task<PowerBiRestClient.RefreshScheduleInfo> RefreshScheduleAsync(string ws, string ds, CancellationToken ct = default)
        => Track("refresh-schedule", $"{ws}/{ds}", async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(ws, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
            return await rest.GetRefreshScheduleAsync(groupId, datasetId, ct);
        });

    /// <summary>Updates the scheduled refresh (partial — only set fields change). Gated by the console
    /// "Allow writes" toggle and the read-only-target guardrail.</summary>
    public Task<bool> SetRefreshScheduleAsync(
        string ws, string ds,
        bool? enabled, IReadOnlyList<string>? days, IReadOnlyList<string>? times,
        string? timezone, string? notify, CancellationToken ct = default)
        => Track("set-refresh-schedule", $"{ws}/{ds}", async () =>
        {
            if (!WritesEnabled)
            {
                throw new DaxterException("Writes are disabled. Turn on 'Allow writes' in Configure to change the schedule.");
            }

            if (_state.ToConfig(ws, ds).IsReadOnlyTarget())
            {
                throw new DaxterException($"'{ws}' is a read-only target; the schedule can't be changed here.");
            }

            var body = RefreshScheduleRequest.Build(enabled, days, times, timezone, notify);
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(ws, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
            await rest.UpdateRefreshScheduleAsync(groupId, datasetId, body, ct);
            return true;
        });

    // ---- admin: gateways + deployment pipelines (REST) ----

    public Task<QueryResult> GatewaysAsync(CancellationToken ct = default)
        => Track("gateways", null, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.GatewaysAsync(ct);
        });

    /// <summary>A model's current connections (display name + connectivity type + details) from the Fabric
    /// API. Needs only model read/write, so it names bindings to gateways the caller can't manage. Returns
    /// null on failure (e.g. Fabric API unreachable) so the page can fall back to the raw data-sources view.</summary>
    public async Task<QueryResult?> ItemConnectionsAsync(string ws, string ds, CancellationToken ct = default)
    {
        try
        {
            return await Track("item-connections", $"{ws}/{ds}", async () =>
            {
                using var rest = new PowerBiRestClient(Provider(Config()));
                var groupId = await rest.ResolveGroupIdAsync(ws, ct);
                var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
                return await rest.ItemConnectionsAsync(groupId, datasetId, ct);
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "item-connections unavailable for {Ws}/{Ds}; falling back to data sources", ws, ds);
            return null;
        }
    }

    /// <summary>Deep link to a model's "Gateway and cloud connections" settings in the Power BI Service —
    /// where the cloud "Maps to" is set (no public write API). Returns null if the ids can't be resolved.</summary>
    public async Task<string?> ModelSettingsUrlAsync(string ws, string ds, CancellationToken ct = default)
    {
        try
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(ws, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
            return $"https://app.powerbi.com/groups/{groupId}/settings/datasets/{datasetId}";
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "could not build model settings URL for {Ws}/{Ds}", ws, ds);
            return null;
        }
    }

    /// <summary>All connections the account can access (cloud + gateway) from the Fabric API — used to
    /// list the shareable <em>cloud</em> connections. Returns null on failure so the page can hide the
    /// section gracefully.</summary>
    public async Task<QueryResult?> ConnectionsAsync(CancellationToken ct = default)
    {
        try
        {
            return await Track("connections", null, async () =>
            {
                using var rest = new PowerBiRestClient(Provider(Config()));
                return await rest.ConnectionsAsync(ct);
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "connections list unavailable (Fabric API)");
            return null;
        }
    }

    // ---- take ownership + gateway binding (service config — XMLA can't do these; gated like writes) ----

    /// <summary>Gateways the model can be bound to (those with matching data sources).</summary>
    public Task<QueryResult> DiscoverGatewaysAsync(string ws, string ds, CancellationToken ct = default)
        => Track("discover-gateways", $"{ws}/{ds}", async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(ws, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
            return await rest.DiscoverGatewaysAsync(groupId, datasetId, ct);
        });

    /// <summary>The data sources (connections) defined on a gateway — their ids are what a bind maps to.</summary>
    public Task<QueryResult> GatewayDatasourcesAsync(string gatewayId, CancellationToken ct = default)
        => Track("gateway-datasources", gatewayId, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.GatewayDatasourcesAsync(gatewayId, ct);
        });

    /// <summary>Takes over ownership of the model (gated; refused on production). Returns a status line.</summary>
    public async Task<string> TakeOverAsync(string ws, string ds, CancellationToken ct = default)
    {
        EnsureWritable(_state.ToConfig(ws, ds), "take over a model");
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(ws, ct);
        var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
        await rest.TakeOverAsync(groupId, datasetId, ct);
        _log.LogInformation("take-over [{Ws}/{Ds}] → OK", ws, ds);
        return $"Took over '{ds}' — you are now the owner.";
    }

    /// <summary>Binds the model to a gateway, optionally mapping its sources to specific gateway
    /// connection ids (none = first matching per source). Gated; refused on production.</summary>
    /// <summary>Binds ONE data source of a model to a connection (any connectivity type incl.
    /// ShareableCloud) via the Fabric bindConnection API — the per-source / cloud-"Maps to" path.
    /// Gated like other writes; requires model ownership (take over first).</summary>
    public async Task<string> BindConnectionAsync(string ws, string ds, string? connectionId,
        string connectivityType, string sourceType, string sourcePath, CancellationToken ct = default)
    {
        EnsureWritable(_state.ToConfig(ws, ds), "bind a data source to a connection");
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(ws, ct);
        var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
        await rest.BindConnectionAsync(groupId, datasetId, connectionId, connectivityType, sourceType, sourcePath, ct);
        _log.LogInformation("bind-connection [{Ws}/{Ds}] {Type} '{Path}' → {Conn} ({Id})",
            ws, ds, sourceType, sourcePath, connectivityType, connectionId ?? "—");
        return $"Bound {sourceType} source to {connectivityType}.";
    }

    public async Task<string> BindToGatewayAsync(string ws, string ds, string gatewayObjectId,
        IReadOnlyList<string>? datasourceObjectIds, CancellationToken ct = default)
    {
        EnsureWritable(_state.ToConfig(ws, ds), "bind a model to a gateway");
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(ws, ct);
        var datasetId = await rest.ResolveDatasetIdAsync(groupId, ds, ct);
        await rest.BindToGatewayAsync(groupId, datasetId, gatewayObjectId, datasourceObjectIds, ct);
        var n = datasourceObjectIds?.Count ?? 0;
        _log.LogInformation("bind-to-gateway [{Ws}/{Ds}] → gateway {Gw}, {N} datasource(s)", ws, ds, gatewayObjectId, n);
        return $"Bound '{ds}' to gateway {gatewayObjectId}" + (n > 0 ? $" — {n} data source(s) mapped." : ".");
    }

    private void EnsureWritable(DaxterConfig cfg, string what)
    {
        if (!_state.AllowWrites)
            throw new DaxterException($"Writes are disabled. Turn on \"Allow writes\" on the Configure page to {what}.");
        if (cfg.IsReadOnlyTarget())
        {
            // The reason tells the user WHICH rule matched — "read-only pattern X", "not in the
            // write-allowed list", legacy heuristic, etc. Much better than the old opaque message.
            var reason = cfg.ReadOnlyReason() ?? "read-only rule";
            throw new DaxterException(
                $"Refusing to {what} on a READ-ONLY target ('{cfg.Workspace}') — matched {reason}.");
        }
    }

    // ---- Fabric items: Copy Jobs + Notebooks (list, definition, run, monitor) ----

    /// <summary>All Copy Jobs in a workspace. Read-only Fabric REST.</summary>
    public Task<QueryResult> CopyJobsAsync(string ws, CancellationToken ct = default)
        => Track("copy-jobs", ws, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(ws, ct);
            return await rest.CopyJobsAsync(groupId, ct);
        });

    /// <summary>All Notebooks in a workspace. Read-only Fabric REST.</summary>
    public Task<QueryResult> NotebooksAsync(string ws, CancellationToken ct = default)
        => Track("notebooks", ws, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(ws, ct);
            return await rest.NotebooksAsync(groupId, ct);
        });

    /// <summary>A Copy Job's full <c>copyjob-content.json</c> definition (Base64-decoded parts) —
    /// the source/destination/connection/mapping JSON the Fabric UI calls "View JSON code". Returns
    /// null on failure so the page can show an inline error instead of a broken viewer.</summary>
    public async Task<IReadOnlyList<FabricItemPart>?> CopyJobDefinitionAsync(string ws, string copyJobId, CancellationToken ct = default)
    {
        try
        {
            return await Track<IReadOnlyList<FabricItemPart>>("copy-job-definition", $"{ws}/{copyJobId}", async () =>
            {
                using var rest = new PowerBiRestClient(Provider(Config()));
                var groupId = await rest.ResolveGroupIdAsync(ws, ct);
                return await rest.CopyJobDefinitionAsync(groupId, copyJobId, ct);
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "copy-job definition unavailable [{Ws}/{Id}]", ws, copyJobId); return null; }
    }

    /// <summary>A Notebook's full definition — typically a single <c>artifact.content.ipynb</c>
    /// payload (the notebook cells, in standard Jupyter format) plus a small <c>.platform</c> file.
    /// Pass <paramref name="format"/> = "ipynb" to force Jupyter format regardless of the notebook's
    /// language (otherwise the service returns the language-specific source — .py/.scala/.sql/.r).</summary>
    public async Task<IReadOnlyList<FabricItemPart>?> NotebookDefinitionAsync(string ws, string notebookId, string? format = null, CancellationToken ct = default)
    {
        try
        {
            return await Track<IReadOnlyList<FabricItemPart>>("notebook-definition", $"{ws}/{notebookId}", async () =>
            {
                using var rest = new PowerBiRestClient(Provider(Config()));
                var groupId = await rest.ResolveGroupIdAsync(ws, ct);
                return await rest.NotebookDefinitionAsync(groupId, notebookId, format, ct);
            });
        }
        catch (Exception ex) { _log.LogWarning(ex, "notebook definition unavailable [{Ws}/{Id}]", ws, notebookId); return null; }
    }

    /// <summary>Triggers a Copy Job on demand and returns the new instance id. Writes-gated like
    /// every other mutation (Allow writes + non-prod check). Pass <paramref name="executionData"/>
    /// for parameterized runs (almost always null for Copy Jobs).</summary>
    public async Task<string> RunCopyJobAsync(string ws, string copyJobId, string? executionData = null, CancellationToken ct = default)
    {
        EnsureWritable(_state.ToConfig(ws, null), "run a Copy Job");
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(ws, ct);
        var instanceId = await rest.StartItemJobAsync(groupId, copyJobId, "Execute", executionData, ct);
        _log.LogInformation("copy-job RUN [{Ws}/{Id}] → instance {Instance}", ws, copyJobId, instanceId);
        return instanceId;
    }

    /// <summary>Triggers a Notebook on demand and returns the new instance id. Writes-gated.
    /// <paramref name="executionData"/> can carry parameter overrides — see the Job Scheduler API.</summary>
    public async Task<string> RunNotebookAsync(string ws, string notebookId, string? executionData = null, CancellationToken ct = default)
    {
        EnsureWritable(_state.ToConfig(ws, null), "run a Notebook");
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(ws, ct);
        var instanceId = await rest.StartItemJobAsync(groupId, notebookId, "RunNotebook", executionData, ct);
        _log.LogInformation("notebook RUN [{Ws}/{Id}] → instance {Instance}", ws, notebookId, instanceId);
        return instanceId;
    }

    /// <summary>Recent run instances for a Fabric item (Copy Job, Notebook, or any other) — status,
    /// start/end, duration, failure reason. Read-only.</summary>
    public Task<QueryResult> ItemJobInstancesAsync(string ws, string itemId, CancellationToken ct = default)
        => Track("item-runs", $"{ws}/{itemId}", async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            var groupId = await rest.ResolveGroupIdAsync(ws, ct);
            return await rest.ListItemJobInstancesAsync(groupId, itemId, ct);
        });

    /// <summary>Cancels a running item-job instance. Writes-gated.</summary>
    public async Task CancelItemJobInstanceAsync(string ws, string itemId, string instanceId, CancellationToken ct = default)
    {
        EnsureWritable(_state.ToConfig(ws, null), "cancel a job instance");
        using var rest = new PowerBiRestClient(Provider(Config()));
        var groupId = await rest.ResolveGroupIdAsync(ws, ct);
        await rest.CancelItemJobInstanceAsync(groupId, itemId, instanceId, ct);
        _log.LogInformation("job-instance CANCEL [{Ws}/{Item}/{Instance}] → OK", ws, itemId, instanceId);
    }

    // ---- Fabric SQL endpoints (Warehouse + Lakehouse SQL analytics endpoint) ----

    /// <summary>Every SQL endpoint in a workspace — every Warehouse and every Lakehouse SQL endpoint
    /// — for the Sql page's endpoint picker. Returns null on failure so the page can show an empty
    /// dropdown instead of an error banner.</summary>
    public async Task<QueryResult?> SqlEndpointsAsync(string ws, CancellationToken ct = default)
    {
        try
        {
            return await Track("sql-endpoints", ws, async () =>
            {
                using var rest = new PowerBiRestClient(Provider(Config()));
                var groupId = await rest.ResolveGroupIdAsync(ws, ct);
                return await rest.SqlEndpointsAsync(groupId, ct);
            });
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SQL endpoints list unavailable for workspace '{Ws}'", ws);
            return null;
        }
    }

    /// <summary>Runs <paramref name="sql"/> on the Fabric SQL endpoint at <paramref name="server"/> /
    /// <paramref name="database"/> and returns the first result set. Read-only T-SQL runs unconditionally;
    /// any non-read statement requires the Allow-writes gate (same toggle that gates model edits) — the
    /// SQL page wraps the call with the standard confirm modal when that gate is on. Offloaded to a
    /// pool thread so the busy overlay paints (Microsoft.Data.SqlClient's open/execute are sync at the
    /// TDS layer and would otherwise block the Blazor circuit).</summary>
    public Task<QueryResult> SqlQueryAsync(string server, string database, string sql, CancellationToken ct = default)
        => Track("sql-query", $"{server}/{database}", () => Task.Run(async () =>
        {
            var client = new FabricSqlClient(SqlProvider(Config()));
            return await client.ExecuteAsync(server, database, sql, allowWrite: _state.AllowWrites, ct);
        }, ct));

    /// <summary>Returns every object in a Fabric SQL endpoint — (schema, kind, name) — so the /sql
    /// page can render its left-side explorer tree. Same INFORMATION_SCHEMA round-trip the CLI/MCP
    /// use, offloaded to <see cref="Task.Run"/> so the busy overlay paints.</summary>
    public Task<QueryResult> SqlObjectsAsync(string server, string database, CancellationToken ct = default)
        => Track("sql-objects", $"{server}/{database}", () => Task.Run(async () =>
        {
            var client = new FabricSqlClient(SqlProvider(Config()));
            return await client.ListObjectsAsync(server, database, ct);
        }, ct));

    public Task<QueryResult> PipelinesAsync(CancellationToken ct = default)
        => Track("pipelines", null, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.PipelinesAsync(ct);
        });

    public Task<QueryResult> PipelineStagesAsync(string pipelineId, CancellationToken ct = default)
        => Track("pipeline-stages", pipelineId, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.PipelineStagesAsync(pipelineId, ct);
        });

    public Task<QueryResult> PipelineOperationsAsync(string pipelineId, CancellationToken ct = default)
        => Track("pipeline-operations", pipelineId, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.PipelineOperationsAsync(pipelineId, ct);
        });

    /// <summary>
    /// Compares a model's parameter values across a pipeline's stages — see
    /// <see cref="PipelineRulesService"/> in Daxter.Core for the shared implementation.
    /// </summary>
    public async Task<PipelineParamMatrix> PipelineParameterMatrixAsync(string pipelineId, string model, CancellationToken ct = default)
    {
        var cfg = Config();
        using var rest = new PowerBiRestClient(Provider(cfg));
        var matrix = await PipelineRulesService.ComputeAsync(rest, cfg, Provider(cfg), pipelineId, model, ct: ct);
        _log.LogInformation("pipeline-params [{Model}] → {Stages} stages, {Rows} params, {Rules} differ",
            model, matrix.Stages.Count, matrix.Rows.Count, matrix.Rows.Count(r => r.Differs));
        return matrix;
    }

    /// <summary>
    /// Pipeline-wide audit — scans every model in the source stage and returns its parameter
    /// matrix across all stages. Use the progress callback to update a UI counter.
    /// </summary>
    public async Task<PipelineScan> PipelineScanAsync(
        string pipelineId, int concurrency = 5,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        var cfg = Config();
        using var rest = new PowerBiRestClient(Provider(cfg));
        var scan = await PipelineRulesService.ScanPipelineAsync(
            rest, cfg, Provider(cfg), pipelineId, concurrency, onProgress, ct);
        _log.LogInformation("pipeline-scan [{Concurrency} parallel] → {Models} models, {NoRule} without rules",
            concurrency, scan.Models.Count, scan.Models.Count(m => m.Matrix.Rows.All(r => !r.Differs)));
        return scan;
    }

    /// <summary>
    /// Index of every accessible pipeline and the models in its first (Dev) stage — used to drive
    /// a model-first picker (choose a model → find the pipelines that contain it).
    /// </summary>
    public async Task<IReadOnlyList<PipelineModels>> PipelineModelIndexAsync(CancellationToken ct = default)
    {
        using var rest = new PowerBiRestClient(Provider(Config()));
        var pipes = await rest.PipelinesAsync(ct);
        int pi = Col(pipes, "id"), pn = Col(pipes, "displayName");

        var result = new List<PipelineModels>();
        foreach (var row in pipes.Rows)
        {
            var id = pi >= 0 ? row[pi]?.ToString() ?? "" : "";
            if (id.Length == 0) continue;
            var name = pn >= 0 ? row[pn]?.ToString() ?? "" : "";

            var models = new List<string>();
            try
            {
                var stagesQr = await rest.PipelineStagesAsync(id, ct);
                int oi = Col(stagesQr, "order"), wi = Col(stagesQr, "workspaceName");
                var firstWs = stagesQr.Rows
                    .Where(r => wi >= 0 && !string.IsNullOrEmpty(r[wi]?.ToString()))
                    .OrderBy(r => oi >= 0 && r[oi] is not null ? Convert.ToInt32(r[oi]) : 0)
                    .Select(r => r[wi]!.ToString()!)
                    .FirstOrDefault();
                if (firstWs is not null)
                {
                    var gid = await rest.ResolveGroupIdAsync(firstWs, ct);
                    var ds = await rest.DatasetsAsync(gid, ct);
                    int ni = Col(ds, "name");
                    models = ds.Rows.Select(r => ni >= 0 ? r[ni]?.ToString() ?? "" : "")
                        .Where(s => s.Length > 0).ToList();
                }
            }
            catch { /* skip pipelines whose stage workspace we can't enumerate */ }

            result.Add(new PipelineModels(id, name, models));
        }
        return result;
    }

    private static int Col(QueryResult r, string column)
    {
        for (var i = 0; i < r.Columns.Count; i++)
            if (string.Equals(r.Columns[i], column, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Runs a DAX query under an impersonated role and/or user (RLS test). Read-only, but the
    /// connecting identity must be an admin of the workspace/model to impersonate.
    /// </summary>
    public Task<QueryResult> TestRlsAsync(string ws, string ds, string? role, string? user, string dax, CancellationToken ct = default)
        => Track("test-rls", $"{ws}/{ds}", async () =>
        {
            if (string.IsNullOrWhiteSpace(role) && string.IsNullOrWhiteSpace(user))
                throw new DaxterException("Provide a role and/or a user to impersonate.");
            if (string.IsNullOrWhiteSpace(dax))
                throw new DaxterException("Provide a DAX query to evaluate under the identity.");
            var cfg = _state.ToConfig(ws, ds);
            var factory = new AdomdXmlaSessionFactory(cfg, Provider(cfg), role, user);
            using var session = await factory.CreateAsync(ct);
            return session.Execute(dax);
        });

    /// <summary>Loads the model's fields tree (tables → their columns + measures) for the DAX explorer.</summary>
    public Task<ModelTree> ModelTreeAsync(string ws, string ds, CancellationToken ct = default)
    {
        var cfg = _state.ToConfig(ws, ds);
        // Offload the synchronous ADOMD reads so the circuit thread can render the busy overlay.
        return Task.Run(async () =>
        {
        var factory = new AdomdXmlaSessionFactory(cfg, Provider(cfg));
        using var session = await factory.CreateAsync(ct);

        var tables = session.Execute("SELECT [ID], [Name], [IsHidden] FROM $SYSTEM.TMSCHEMA_TABLES ORDER BY [Name]");
        var columns = session.Execute("SELECT [TableID], [ExplicitName], [IsHidden] FROM $SYSTEM.TMSCHEMA_COLUMNS");
        var measures = session.Execute("SELECT [TableID], [Name] FROM $SYSTEM.TMSCHEMA_MEASURES");

        // TableID -> objects
        var byTable = new Dictionary<long, List<ModelObject>>();
        void Add(long tid, string name, bool isMeasure)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (!byTable.TryGetValue(tid, out var list)) byTable[tid] = list = new();
            list.Add(new ModelObject(name, isMeasure));
        }

        foreach (var r in columns.Rows)
        {
            var hidden = r.Length > 2 && r[2] is bool b && b;
            var name = r[1]?.ToString();
            if (!hidden && !string.IsNullOrEmpty(name) && !name!.StartsWith("RowNumber-", StringComparison.Ordinal))
                Add(Convert.ToInt64(r[0]), name, isMeasure: false);
        }
        foreach (var r in measures.Rows)
            Add(Convert.ToInt64(r[0]), r[1]?.ToString() ?? "", isMeasure: true);

        var nodes = new List<ModelTableNode>();
        foreach (var t in tables.Rows)
        {
            if (t.Length > 2 && t[2] is bool hidden && hidden) continue; // skip hidden tables
            var id = Convert.ToInt64(t[0]);
            var name = t[1]?.ToString() ?? "";
            var items = byTable.TryGetValue(id, out var list)
                ? list.OrderByDescending(o => o.IsMeasure).ThenBy(o => o.Name, StringComparer.OrdinalIgnoreCase).ToList()
                : new List<ModelObject>();
            nodes.Add(new ModelTableNode(name, items));
        }

        return new ModelTree(nodes);
        }, ct);
    }

    /// <summary>
    /// Runs a model edit through the SAME <see cref="ModelEditService"/> the CLI and MCP use. The
    /// <paramref name="op"/> stages a change in the in-memory model and returns a human-readable
    /// description; <paramref name="apply"/>=false discards it (dry-run preview), apply takes a
    /// <c>.bim</c> backup then <c>SaveChanges()</c>. Gated on <see cref="ModelEditEnabled"/> and
    /// refuses production targets (XMLA edits are irreversible for PBIX download).
    /// </summary>
    public async Task<string> ModelEditAsync(string ws, string ds, Func<ModelEditService, string> op, bool apply, CancellationToken ct = default)
    {
        var cfg = _state.ToConfig(ws, ds);
        var sw = Stopwatch.StartNew();
        var label = $"model-edit [{ws}/{ds}]";
        try
        {
            if (apply)
            {
                if (!_state.AllowModelEdit)
                    throw new DaxterException("Model editing is disabled. Turn on \"Allow model edits\" on the Configure page first.");
                if (cfg.IsReadOnlyTarget())
                {
                    var reason = cfg.ReadOnlyReason() ?? "read-only rule";
                    throw new DaxterException($"Refusing to edit a READ-ONLY target ('{ws}') — matched {reason}.");
                }
            }

            var token = await Provider(cfg).GetTokenAsync(ct);
            // Offload the synchronous TOM staging/apply so the circuit thread can render the busy overlay.
            return await Task.Run(() =>
            {
                using var svc = new ModelEditService(cfg, token);
                var desc = op(svc);   // stages the change in-memory (TOM)

                if (!apply)
                {
                    _log.LogInformation("{Label} → DRY RUN in {Ms} ms", label, sw.ElapsedMilliseconds);
                    return "DRY RUN — not applied:\n" + desc;
                }

                var backup = new ModelBackupService(cfg, token).Backup();
                svc.Apply();
                _log.LogInformation("{Label} → APPLIED in {Ms} ms (backup {Backup})", label, sw.ElapsedMilliseconds, backup);
                return $"APPLIED — backup saved to {backup}\n{desc}";
            }, ct);
        }
        catch (Exception ex)
        {
            _log.LogError("{Label} failed: {Error}", label, ex.Message);
            throw;
        }
    }

    /// <summary>Reads a table's current definition (kind + source expression + import column specs) via
    /// TOM, for the Model Edit page to pre-fill when a table row is clicked.</summary>
    public async Task<(string Kind, string Expression, string Columns, bool HasPolicy)> ReadTableAsync(string ws, string ds, string table, CancellationToken ct = default)
    {
        var cfg = _state.ToConfig(ws, ds);
        var token = await Provider(cfg).GetTokenAsync(ct);
        // Offload the synchronous TOM read so the circuit thread can render the busy overlay.
        return await Task.Run(() =>
        {
            using var svc = new ModelEditService(cfg, token);
            return svc.ReadTable(table);
        }, ct);
    }

    /// <summary>Reads a table's incremental refresh policy settings (null if it has none).</summary>
    public async Task<RefreshPolicyInfo?> ReadRefreshPolicyAsync(string ws, string ds, string table, CancellationToken ct = default)
    {
        var cfg = _state.ToConfig(ws, ds);
        var token = await Provider(cfg).GetTokenAsync(ct);
        // Offload the synchronous TOM read so the circuit thread can render the busy overlay.
        return await Task.Run(() =>
        {
            using var svc = new ModelEditService(cfg, token);
            return svc.ReadRefreshPolicy(table);
        }, ct);
    }

    /// <summary>Reads a table's columns + their editable properties (format, sort-by, summarize-by, …) via
    /// TOM, for the Model Edit page's Columns tab to pre-fill when a column is selected.</summary>
    public async Task<IReadOnlyList<ColumnInfo>> ReadColumnsAsync(string ws, string ds, string table, CancellationToken ct = default)
    {
        var cfg = _state.ToConfig(ws, ds);
        var token = await Provider(cfg).GetTokenAsync(ct);
        // Offload the synchronous TOM read so the circuit thread can render the busy overlay.
        return await Task.Run(() =>
        {
            using var svc = new ModelEditService(cfg, token);
            return svc.ReadColumns(table);
        }, ct);
    }

    private Task<QueryResult> XmlaAsync(string op, string ws, string ds, Func<IXmlaSession, QueryResult> body, CancellationToken ct)
        // Offload the synchronous ADOMD work (connection.Open + Execute have no async API) to a
        // thread-pool thread so the Blazor circuit thread stays free to render the busy overlay —
        // otherwise the spinner can't paint until the blocking read has already finished.
        => Track(op, $"{ws}/{ds}", () => Task.Run(async () =>
        {
            var cfg = _state.ToConfig(ws, ds);
            var factory = new AdomdXmlaSessionFactory(cfg, Provider(cfg));
            using var session = await factory.CreateAsync(ct);
            return body(session);
        }, ct));

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

    /// <summary>Same as <see cref="Track(string, string?, Func{Task{QueryResult}})"/> but generic —
    /// for ops that return a typed payload (item-definition parts, job-instance records). Lets
    /// non-QueryResult bridge methods still benefit from start/success/error logging.</summary>
    private async Task<T> Track<T>(string op, string? target, Func<Task<T>> body)
    {
        var label = target is null ? op : $"{op} [{target}]";
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await body();
            _log.LogInformation("{Label} → OK in {Ms} ms", label, sw.ElapsedMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            _log.LogError("{Label} failed after {Ms} ms: {Error}", label, sw.ElapsedMilliseconds, ex.Message);
            throw;
        }
    }

    // ── Artifacts bridge ──────────────────────────────────────────────────────────────────────
    // The IArtifactStore singleton is owned by the Web host; both /artifacts (Razor) and the
    // /api/artifacts streaming HTTP endpoints route through this bridge so logging is uniform
    // and so future cross-store routing (local FS today, S3 tomorrow) has one chokepoint to
    // change. The store handles its own key sanitisation + quota; bridge methods are thin
    // wrappers that add Track-pattern logging.

    /// <summary>Direct access to the singleton store — used by the HTTP /api/artifacts streaming
    /// endpoints, which need the raw IArtifactStore to pipe straight to Response.Body.</summary>
    public IArtifactStore ArtifactStore => _artifacts;

    /// <summary>List artifacts under an optional prefix. Returned newest-first for the /artifacts
    /// page; raw key order is still available via the store directly when callers want it.</summary>
    public Task<IReadOnlyList<ArtifactRef>> ArtifactsListAsync(string? prefix = null, CancellationToken ct = default)
        => Track<IReadOnlyList<ArtifactRef>>("artifacts-list", prefix, () => _artifacts.ListAsync(prefix, ct));

    /// <summary>Metadata for a single artifact key. Null when the key doesn't exist.</summary>
    public Task<ArtifactRef?> ArtifactsMetaAsync(string key, CancellationToken ct = default)
        => Track<ArtifactRef?>("artifacts-meta", key, () => _artifacts.GetMetaAsync(key, ct));

    /// <summary>Delete one artifact or an entire prefix (recursive). Returns files removed. The
    /// store is the user's own data — no Allow-writes gate needed (unlike Power BI mutations).</summary>
    public Task<int> ArtifactsDeleteAsync(string keyPrefix, CancellationToken ct = default)
        => Track<int>("artifacts-delete", keyPrefix, () => _artifacts.DeleteAsync(keyPrefix, ct));

    /// <summary>Trigger an on-demand purge of every artifact whose TTL has expired. Mostly a UI
    /// affordance — the nightly hosted-service tick (Phase 2) does this automatically.</summary>
    public Task<long> ArtifactsPurgeExpiredAsync(CancellationToken ct = default)
        => Track<long>("artifacts-purge-expired", null, () => _artifacts.PurgeExpiredAsync(ct));

    /// <summary>Current usage in bytes — the /artifacts page surfaces this with the quota so a
    /// glance at the footer shows how full the store is.</summary>
    public Task<long> ArtifactsUsageBytesAsync(CancellationToken ct = default)
        => _artifacts.CurrentUsageBytesAsync(ct);
}
