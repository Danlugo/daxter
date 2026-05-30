using System.ComponentModel;
using Daxter.Core;
using Daxter.Core.Maintenance;
using ModelContextProtocol.Server;

namespace Daxter.Cli.Mcp;

/// <summary>
/// Read-only MCP tools over the Power BI Service. Every tool accepts optional
/// <c>workspace</c>/<c>dataset</c> arguments that override the server's env config.
/// Mutating operations (refresh/cache) are intentionally not exposed in v1.
/// </summary>
[McpServerToolType]
public static class DaxterTools
{
    [McpServerTool(Name = "daxter_query"), Description("Run a DAX or MDX query against a Power BI semantic model. Returns rows as JSON.")]
    public static Task<string> Query(
        [Description("The DAX or MDX query, e.g. EVALUATE TOPN(10, Sales)")] string query,
        [Description("Workspace name (optional; defaults to server config)")] string? workspace = null,
        [Description("Dataset / model name (optional; defaults to server config)")] string? dataset = null,
        CancellationToken ct = default)
        => DaxterToolRuntime.XmlaAsync(workspace, dataset, s => s.Execute(query), ct);

    [McpServerTool(Name = "daxter_dmv"), Description("Run a DMV ($SYSTEM) query, e.g. SELECT * FROM $SYSTEM.TMSCHEMA_TABLES.")]
    public static Task<string> Dmv(
        [Description("A DMV SELECT statement.")] string statement,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.XmlaAsync(workspace, dataset, s => s.Execute(statement), ct);

    [McpServerTool(Name = "daxter_list_tables"), Description("List the tables in a semantic model.")]
    public static Task<string> ListTables(string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.XmlaAsync(workspace, dataset,
            s => s.Execute("SELECT [Name] AS [Table] FROM $SYSTEM.TMSCHEMA_TABLES ORDER BY [Name]"), ct);

    [McpServerTool(Name = "daxter_measures"), Description("List measures in a model. Set withExpression=true to include the DAX.")]
    public static Task<string> Measures(
        [Description("Include each measure's DAX expression.")] bool withExpression = false,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.MetaAsync(workspace, dataset, m => m.Measures(withExpression), ct);

    [McpServerTool(Name = "daxter_measure"), Description("Show one measure's full definition (name, type, folder, format, DAX).")]
    public static Task<string> Measure(
        [Description("Measure name.")] string name,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.MetaAsync(workspace, dataset, m => m.Measure(name), ct);

    [McpServerTool(Name = "daxter_mcode"), Description("Show the Power Query (M) code for a table's partitions.")]
    public static Task<string> MCode(
        [Description("Table name.")] string table,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.MetaAsync(workspace, dataset, m => m.MCode(table), ct);

    [McpServerTool(Name = "daxter_parameters"), Description("List the model's shared M expressions / parameters.")]
    public static Task<string> Parameters(string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.MetaAsync(workspace, dataset, m => m.Parameters(), ct);

    [McpServerTool(Name = "daxter_partitions"), Description("List partitions and last-refresh times, optionally for one table.")]
    public static Task<string> Partitions(
        [Description("Table name (optional; all partitions if omitted).")] string? table = null,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.MetaAsync(workspace, dataset, m => m.Partitions(table), ct);

    [McpServerTool(Name = "daxter_rls"), Description("List RLS roles in a model.")]
    public static Task<string> Rls(string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.MetaAsync(workspace, dataset, m => m.Roles(), ct);

    [McpServerTool(Name = "daxter_diff_measures"), Description("Compare measures between the configured model and another model in the same workspace.")]
    public static Task<string> DiffMeasures(
        [Description("The other dataset/model name to compare against.")] string other,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.DiffMeasuresAsync(workspace, dataset, other, ct);

    [McpServerTool(Name = "daxter_refresh_history"), Description("Show recent refresh history (status, start/end) for a model via REST.")]
    public static Task<string> RefreshHistory(
        [Description("Number of recent refreshes (default 10).")] int top = 10,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(workspace, dataset, async (rest, cfg, c) =>
        {
            var groupId = await rest.ResolveGroupIdAsync(cfg.Workspace, c);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, cfg.Dataset!, c);
            return await rest.RefreshHistoryAsync(groupId, datasetId, top, c);
        }, ct);

    [McpServerTool(Name = "daxter_workspaces"), Description("List Power BI workspaces (with their group ids) the identity can see.")]
    public static Task<string> Workspaces(CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(null, null, (rest, _, c) => rest.GroupsAsync(c), ct);

    [McpServerTool(Name = "daxter_datasets"), Description("List datasets in a workspace.")]
    public static Task<string> Datasets(string? workspace = null, CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(workspace, null, async (rest, cfg, c) =>
            await rest.DatasetsAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, c), c), ct);

    [McpServerTool(Name = "daxter_reports"), Description("List reports in a workspace.")]
    public static Task<string> Reports(string? workspace = null, CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(workspace, null, async (rest, cfg, c) =>
            await rest.ReportsAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, c), c), ct);

    [McpServerTool(Name = "daxter_lineage"), Description("Report → dataset lineage for a workspace.")]
    public static Task<string> Lineage(string? workspace = null, CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(workspace, null, async (rest, cfg, c) =>
            await rest.LineageAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, c), c), ct);

    [McpServerTool(Name = "daxter_export"), Description("Export the model's full definition as Tabular (.bim) JSON. Large output is truncated — use the CLI for the complete file.")]
    public static Task<string> Export(string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.ExportAsync(workspace, dataset, ct);

    [McpServerTool(Name = "daxter_permissions"), Description("Who has access. With a dataset → dataset users; without a dataset → workspace users.")]
    public static Task<string> Permissions(string? dataset = null, string? workspace = null, CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(workspace, dataset, async (rest, cfg, c) =>
        {
            var groupId = await rest.ResolveGroupIdAsync(cfg.Workspace, c);
            if (!string.IsNullOrWhiteSpace(cfg.Dataset))
            {
                var datasetId = await rest.ResolveDatasetIdAsync(groupId, cfg.Dataset!, c);
                return await rest.DatasetUsersAsync(groupId, datasetId, c);
            }

            return await rest.WorkspaceUsersAsync(groupId, c);
        }, ct);

    [McpServerTool(Name = "daxter_gateways"), Description("List gateways visible to the identity (requires gateway-admin rights).")]
    public static Task<string> Gateways(CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(null, null, (rest, _, c) => rest.GatewaysAsync(c), ct);

    [McpServerTool(Name = "daxter_datasources"), Description("Data sources and gateway binding used by a dataset's refresh.")]
    public static Task<string> Datasources(string? dataset = null, string? workspace = null, CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(workspace, dataset, async (rest, cfg, c) =>
        {
            var groupId = await rest.ResolveGroupIdAsync(cfg.Workspace, c);
            var datasetId = await rest.ResolveDatasetIdAsync(
                groupId, cfg.Dataset ?? throw new DaxterException("A dataset is required."), c);
            return await rest.DatasourcesAsync(groupId, datasetId, c);
        }, ct);

    [McpServerTool(Name = "daxter_test_rls"), Description("Test RLS: run a DAX query impersonating a user and/or under a role; returns the filtered result.")]
    public static Task<string> TestRls(
        [Description("DAX query to evaluate under the identity, e.g. EVALUATE ROW(\"n\", COUNTROWS('Sales'))")] string query,
        [Description("RLS role name.")] string? role = null,
        [Description("User UPN to impersonate, e.g. user@contoso.com")] string? user = null,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.TestRlsAsync(workspace, dataset, role, user, query, ct);

    [McpServerTool(Name = "daxter_pipelines"), Description("List Fabric / Power BI deployment pipelines.")]
    public static Task<string> Pipelines(CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(null, null, (rest, _, c) => rest.PipelinesAsync(c), ct);

    [McpServerTool(Name = "daxter_pipeline_stages"), Description("A deployment pipeline's stages (dev/test/prod) and their workspaces.")]
    public static Task<string> PipelineStages(
        [Description("Pipeline id (from daxter_pipelines).")] string pipelineId, CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(null, null, (rest, _, c) => rest.PipelineStagesAsync(pipelineId, c), ct);

    [McpServerTool(Name = "daxter_pipeline_operations"), Description("Recent deploy operations for a pipeline.")]
    public static Task<string> PipelineOperations(
        [Description("Pipeline id (from daxter_pipelines).")] string pipelineId, CancellationToken ct = default)
        => DaxterToolRuntime.RestAsync(null, null, (rest, _, c) => rest.PipelineOperationsAsync(pipelineId, c), ct);

    // ---- Gated write tools (DRY-RUN by default; disabled unless DAXTER_MCP_ALLOW_WRITES=true) ----

    [McpServerTool(Name = "daxter_refresh"), Description(
        "Refresh a model/table/partitions. DRY-RUN by default (returns the TMSL without running). " +
        "To execute, set execute=true AND the server env DAXTER_MCP_ALLOW_WRITES=true; PROD-looking targets are always blocked.")]
    public static Task<string> Refresh(
        [Description("Scope: model | table | partitions")] string scope = "model",
        [Description("Table name (required for table/partitions).")] string? table = null,
        [Description("Refresh type: full|automatic|calculate|dataOnly|clearValues")] string? type = null,
        [Description("Partition order for scope=partitions: newest-first | oldest-first")] string? order = null,
        [Description("Actually execute (default false = dry run).")] bool execute = false,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.MaintenanceAsync(workspace, dataset, svc => scope.Trim().ToLowerInvariant() switch
        {
            "model" => svc.BuildModelRefresh(MaintenanceService.ParseRefreshType(type)),
            "table" => svc.BuildTableRefresh(
                table ?? throw new DaxterException("table is required for scope=table."),
                MaintenanceService.ParseRefreshType(type)),
            "partitions" => svc.BuildPartitionsRefresh(
                table ?? throw new DaxterException("table is required for scope=partitions."),
                DaxterToolRuntime.ParseOrder(order), MaintenanceService.ParseRefreshType(type)),
            _ => throw new DaxterException($"Unknown scope '{scope}'. Use model | table | partitions."),
        }, execute, ct);

    [McpServerTool(Name = "daxter_clear_cache"), Description(
        "Clear the model's data cache. DRY-RUN by default; requires execute=true AND DAXTER_MCP_ALLOW_WRITES=true.")]
    public static Task<string> ClearCache(
        [Description("Actually execute (default false = dry run).")] bool execute = false,
        string? workspace = null, string? dataset = null, CancellationToken ct = default)
        => DaxterToolRuntime.MaintenanceAsync(workspace, dataset, svc => svc.BuildClearCache(), execute, ct);
}
