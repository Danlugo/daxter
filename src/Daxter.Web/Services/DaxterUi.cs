using System.Diagnostics;
using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Editing;
using Daxter.Core.Metadata;
using Daxter.Core.Query;
using Daxter.Core.Rest;
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

    public DaxterUi(ConfigState state, ILogger<DaxterUi> log)
    {
        _state = state;
        _log = log;
    }

    /// <summary>The console's current effective config (editable via the Configure page).</summary>
    public DaxterConfig Config(bool requireWorkspace = false) => _state.ToConfig();

    /// <summary>True when refreshes/writes are enabled (Configure → Allow writes).</summary>
    public bool WritesEnabled => _state.AllowWrites;

    /// <summary>True when model editing is enabled (Configure → Allow model edits) — a stricter,
    /// separate gate than refresh writes (XMLA edits are irreversible for PBIX download).</summary>
    public bool ModelEditEnabled => _state.AllowModelEdit;

    /// <summary>True when the target looks like production (refreshes are refused).</summary>
    public bool IsProductionTarget(string ws, string ds) => _state.ToConfig(ws, ds).IsProductionTarget();

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

    // ---- admin: gateways + deployment pipelines (REST) ----

    public Task<QueryResult> GatewaysAsync(CancellationToken ct = default)
        => Track("gateways", null, async () =>
        {
            using var rest = new PowerBiRestClient(Provider(Config()));
            return await rest.GatewaysAsync(ct);
        });

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
    public async Task<ModelTree> ModelTreeAsync(string ws, string ds, CancellationToken ct = default)
    {
        var cfg = _state.ToConfig(ws, ds);
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
                if (cfg.IsProductionTarget())
                    throw new DaxterException($"Refusing to edit a production target ('{ws}').");
            }

            var token = await Provider(cfg).GetTokenAsync(ct);
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
        using var svc = new ModelEditService(cfg, token);
        return svc.ReadTable(table);
    }

    /// <summary>Reads a table's incremental refresh policy settings (null if it has none).</summary>
    public async Task<RefreshPolicyInfo?> ReadRefreshPolicyAsync(string ws, string ds, string table, CancellationToken ct = default)
    {
        var cfg = _state.ToConfig(ws, ds);
        var token = await Provider(cfg).GetTokenAsync(ct);
        using var svc = new ModelEditService(cfg, token);
        return svc.ReadRefreshPolicy(table);
    }

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
