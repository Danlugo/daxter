using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Export;
using Daxter.Core.Formatting;
using Daxter.Core.Maintenance;
using Daxter.Core.Metadata;
using Daxter.Core.Query;
using Daxter.Core.Rest;

namespace Daxter.Cli.Mcp;

/// <summary>
/// Bridges MCP tools to the Daxter.Core engine. Builds config from the server's
/// environment (overridable per call), runs the operation, and returns capped JSON
/// so a large result set can't blow the model's context.
/// </summary>
internal static class DaxterToolRuntime
{
    private const int RowCap = 1000;
    private static readonly JsonResultFormatter Json = new();

    public static async Task<string> XmlaAsync(
        string? workspace, string? dataset, Func<IXmlaSession, QueryResult> op, CancellationToken ct)
    {
        var config = Config(workspace, dataset);
        var factory = new AdomdXmlaSessionFactory(config, Provider(config));
        using var session = await factory.CreateAsync(ct);
        return Format(op(session));
    }

    public static Task<string> MetaAsync(
        string? workspace, string? dataset, Func<ModelMetadataService, QueryResult> op, CancellationToken ct)
        => XmlaAsync(workspace, dataset, session => op(new ModelMetadataService(session)), ct);

    public static async Task<string> RestAsync(
        string? workspace, string? dataset,
        Func<PowerBiRestClient, DaxterConfig, CancellationToken, Task<QueryResult>> op, CancellationToken ct)
    {
        var config = Config(workspace, dataset);
        using var rest = new PowerBiRestClient(Provider(config));
        return Format(await op(rest, config, ct));
    }

    public static async Task<string> DiffMeasuresAsync(
        string? workspace, string? dataset, string other, CancellationToken ct)
    {
        var config = Config(workspace, dataset);
        var left = new AdomdXmlaSessionFactory(config, Provider(config));
        var rightConfig = WithDataset(config, other);
        var right = new AdomdXmlaSessionFactory(rightConfig, Provider(rightConfig));

        using var leftSession = await left.CreateAsync(ct);
        using var rightSession = await right.CreateAsync(ct);
        return Format(ModelDiffService.DiffMeasures(leftSession, rightSession));
    }

    /// <summary>
    /// Gated maintenance: builds the command, returns it as a dry-run unless
    /// <paramref name="execute"/> is set AND the server allows writes
    /// (<c>DAXTER_MCP_ALLOW_WRITES=true</c>) AND the target is not PROD-looking.
    /// </summary>
    public static async Task<string> MaintenanceAsync(
        string? workspace, string? dataset, Func<MaintenanceService, string> build, bool execute, CancellationToken ct)
    {
        var config = Config(workspace, dataset);
        if (string.IsNullOrWhiteSpace(config.Dataset))
        {
            throw new DaxterException("A dataset is required for maintenance operations.");
        }

        var factory = new AdomdXmlaSessionFactory(config, Provider(config));
        using var session = await factory.CreateAsync(ct);
        var service = new MaintenanceService(session, config.Dataset!);
        var command = build(service);

        if (!execute)
        {
            return "DRY RUN — not executed:\n" + command;
        }

        if (!WritesAllowed())
        {
            return "REFUSED — writes are disabled on this MCP server. " +
                   "Set DAXTER_MCP_ALLOW_WRITES=true to enable.\n" + command;
        }

        if (LooksLikeProd(config))
        {
            return $"REFUSED — '{config.Workspace}' looks like PRODUCTION; writes are blocked over MCP.\n" + command;
        }

        service.Execute(command);
        return "EXECUTED:\n" + command;
    }

    public static PartitionOrder ParseOrder(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "newest-first" or "newest" => PartitionOrder.NewestFirst,
        "oldest-first" or "oldest" => PartitionOrder.OldestFirst,
        _ => throw new DaxterException($"Unknown order '{value}'. Use newest-first or oldest-first."),
    };

    private static bool WritesAllowed()
        => string.Equals(Environment.GetEnvironmentVariable("DAXTER_MCP_ALLOW_WRITES"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeProd(DaxterConfig config) => config.IsProductionTarget();

    private const int ExportCap = 40_000;

    /// <summary>Exports the model definition (.bim) via TOM; truncates very large output.</summary>
    public static async Task<string> ExportAsync(string? workspace, string? dataset, CancellationToken ct)
    {
        var config = Config(workspace, dataset);
        var token = await Provider(config).GetTokenAsync(ct);
        var bim = new ModelExportService(config, token).ExportBim();
        return bim.Length <= ExportCap
            ? bim
            : bim[..ExportCap] + $"\n/* truncated: {ExportCap} of {bim.Length} chars — use the CLI `model export` for the full file */";
    }

    /// <summary>Runs a DAX query under a role and/or impersonated user, returning the filtered result.</summary>
    public static async Task<string> TestRlsAsync(
        string? workspace, string? dataset, string? role, string? user, string query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(role) && string.IsNullOrWhiteSpace(user))
        {
            throw new DaxterException("Provide a role and/or a user to impersonate.");
        }

        if (string.IsNullOrWhiteSpace(query))
        {
            throw new DaxterException("Provide a DAX query to evaluate under the identity.");
        }

        var config = Config(workspace, dataset);
        var factory = new AdomdXmlaSessionFactory(config, Provider(config), role, user);
        using var session = await factory.CreateAsync(ct);
        return Format(session.Execute(query));
    }

    private static DaxterConfig Config(string? workspace, string? dataset)
        => DaxterConfig.FromEnvironment(workspace: workspace, dataset: dataset);

    private static ITokenProvider Provider(DaxterConfig config)
        // Headless: never block on an interactive device-code prompt the user can't see —
        // normal tools require a token cached by `daxter_login`. Device-code prompt → stderr.
        => new MsalTokenProvider(config, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: false);

    /// <summary>Config for tenant-level ops (list workspaces, gateways, sign-in) — no workspace needed.</summary>
    private static DaxterConfig TenantConfig() => DaxterConfig.FromEnvironment(requireWorkspace: false);

    /// <summary>Starts interactive sign-in and returns the device-code message for the user.</summary>
    public static async Task<string> LoginAsync(CancellationToken ct)
    {
        var provider = new MsalTokenProvider(TenantConfig(), deviceCodePrompt: Console.Error.WriteLine);
        var message = await provider.BeginInteractiveLoginAsync(ct);
        return message +
            "\n\nComplete the sign-in in your browser, then ask me to list your workspaces " +
            "so you can pick a default.";
    }

    /// <summary>Runs a tenant-level REST call (no workspace required).</summary>
    public static async Task<string> RestTenantAsync(
        Func<PowerBiRestClient, CancellationToken, Task<QueryResult>> op, CancellationToken ct)
    {
        var config = TenantConfig();
        using var rest = new PowerBiRestClient(Provider(config));
        return Format(await op(rest, ct));
    }

    private static DaxterConfig WithDataset(DaxterConfig c, string dataset) => new()
    {
        Workspace = c.Workspace,
        Dataset = dataset,
        TenantId = c.TenantId,
        ClientId = c.ClientId,
        ClientSecret = c.ClientSecret,
        AuthMode = c.AuthMode,
        Environment = c.Environment,
    };

    private static string Format(QueryResult result)
    {
        if (result.RowCount <= RowCap)
        {
            return Json.Format(result);
        }

        var capped = new QueryResult(result.Columns, result.Rows.Take(RowCap).ToList());
        return Json.Format(capped) + $"\n/* truncated: showing {RowCap} of {result.RowCount} rows */";
    }
}
