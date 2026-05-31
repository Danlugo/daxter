using Daxter.Core;
using Daxter.Core.Audit;
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

    /// <summary>
    /// Runs a tool body and turns user-actionable <see cref="DaxterException"/>s into a
    /// returned message (the MCP SDK otherwise hides thrown-exception text behind a generic
    /// "An error occurred invoking …"). So guidance like "Not signed in — use daxter_login"
    /// reaches the model.
    /// </summary>
    private static async Task<string> Guard(Func<Task<string>> op)
    {
        try
        {
            return await op();
        }
        catch (DaxterException ex)
        {
            return ex.Message;
        }
    }

    public static Task<string> XmlaAsync(
        string? workspace, string? dataset, Func<IXmlaSession, QueryResult> op, CancellationToken ct)
        => Guard(async () =>
        {
            var config = Config(workspace, dataset);
            var factory = new AdomdXmlaSessionFactory(config, Provider(config));
            using var session = await factory.CreateAsync(ct);
            return Format(op(session));
        });

    public static Task<string> MetaAsync(
        string? workspace, string? dataset, Func<ModelMetadataService, QueryResult> op, CancellationToken ct)
        => XmlaAsync(workspace, dataset, session => op(new ModelMetadataService(session)), ct);

    public static Task<string> RestAsync(
        string? workspace, string? dataset,
        Func<PowerBiRestClient, DaxterConfig, CancellationToken, Task<QueryResult>> op, CancellationToken ct)
        => Guard(async () =>
        {
            var config = Config(workspace, dataset);
            using var rest = new PowerBiRestClient(Provider(config));
            return Format(await op(rest, config, ct));
        });

    public static Task<string> DiffMeasuresAsync(
        string? workspace, string? dataset, string other, CancellationToken ct)
        => Guard(async () =>
        {
            var config = Config(workspace, dataset);
            var left = new AdomdXmlaSessionFactory(config, Provider(config));
            var rightConfig = WithDataset(config, other);
            var right = new AdomdXmlaSessionFactory(rightConfig, Provider(rightConfig));

            using var leftSession = await left.CreateAsync(ct);
            using var rightSession = await right.CreateAsync(ct);
            return Format(ModelDiffService.DiffMeasures(leftSession, rightSession));
        });

    /// <summary>
    /// Gated maintenance: builds the command and returns it as a dry-run unless <paramref name="execute"/>
    /// is set AND writes are enabled (the web console's "Allow writes" toggle, or
    /// <c>DAXTER_MCP_ALLOW_WRITES=true</c>). PROD targets are allowed by default; set
    /// <c>DAXTER_MCP_BLOCK_PROD_WRITES=true</c> to re-block them.
    /// </summary>
    public static Task<string> MaintenanceAsync(
        string? workspace, string? dataset, Func<MaintenanceService, string> build, bool execute, CancellationToken ct)
        => Guard(async () =>
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
                return "REFUSED — writes are disabled. Enable them in the web console " +
                       "(Configure → Allow writes) or set DAXTER_MCP_ALLOW_WRITES=true, then retry.\n" + command;
            }

            if (LooksLikeProd(config) && ProdWritesBlocked())
            {
                return $"REFUSED — '{config.Workspace}' looks like PRODUCTION and DAXTER_MCP_BLOCK_PROD_WRITES=true.\n" + command;
            }

            service.Execute(command);
            return "EXECUTED:\n" + command;
        });

    public static PartitionOrder ParseOrder(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "newest-first" or "newest" => PartitionOrder.NewestFirst,
        "oldest-first" or "oldest" => PartitionOrder.OldestFirst,
        _ => throw new DaxterException($"Unknown order '{value}'. Use newest-first or oldest-first."),
    };

    /// <summary>Writes are enabled by the server env var OR the web console's saved "Allow writes" toggle.</summary>
    internal static bool WritesAllowed()
        => string.Equals(Environment.GetEnvironmentVariable("DAXTER_MCP_ALLOW_WRITES"), "true", StringComparison.OrdinalIgnoreCase)
           || ConsoleConfigAllowsWrites();

    /// <summary>Reads the web console's "Allow writes" toggle from ~/.daxter/console-config.json (shared volume).</summary>
    internal static bool ConsoleConfigAllowsWrites()
    {
        try
        {
            var home = Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
            var path = Path.Combine(home, ".daxter", "console-config.json");
            if (!File.Exists(path)) return false;
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
            return doc.RootElement.TryGetProperty("AllowWrites", out var v)
                && v.ValueKind == System.Text.Json.JsonValueKind.True;
        }
        catch { return false; }
    }

    /// <summary>Optional guardrail: set DAXTER_MCP_BLOCK_PROD_WRITES=true to re-block prod refresh/cache over MCP.</summary>
    internal static bool ProdWritesBlocked()
        => string.Equals(Environment.GetEnvironmentVariable("DAXTER_MCP_BLOCK_PROD_WRITES"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeProd(DaxterConfig config) => config.IsProductionTarget();

    private const int ExportCap = 40_000;

    /// <summary>Exports the model definition (.bim) via TOM; truncates very large output.</summary>
    public static Task<string> ExportAsync(string? workspace, string? dataset, CancellationToken ct)
        => Guard(async () =>
        {
            var config = Config(workspace, dataset);
            var token = await Provider(config).GetTokenAsync(ct);
            var bim = new ModelExportService(config, token).ExportBim();
            return bim.Length <= ExportCap
                ? bim
                : bim[..ExportCap] + $"\n/* truncated: {ExportCap} of {bim.Length} chars — use the CLI `model export` for the full file */";
        });

    /// <summary>Runs a DAX query under a role and/or impersonated user, returning the filtered result.</summary>
    public static Task<string> TestRlsAsync(
        string? workspace, string? dataset, string? role, string? user, string query, CancellationToken ct)
        => Guard(async () =>
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
        });

    private static DaxterConfig Config(string? workspace, string? dataset)
        => DaxterConfig.FromEnvironment(workspace: workspace, dataset: dataset);

    private static ITokenProvider Provider(DaxterConfig config)
        // Headless: never block on an interactive device-code prompt the user can't see —
        // normal tools require a token cached by `daxter_login`. Device-code prompt → stderr.
        => new MsalTokenProvider(config, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: false);

    /// <summary>Config for tenant-level ops (list workspaces, gateways, sign-in) — no workspace needed.</summary>
    private static DaxterConfig TenantConfig() => DaxterConfig.FromEnvironment(requireWorkspace: false);

    /// <summary>Starts interactive sign-in and returns a clean, click-first device-code message.</summary>
    public static Task<string> LoginAsync(CancellationToken ct)
        => Guard(async () =>
        {
            var provider = new MsalTokenProvider(TenantConfig(), deviceCodePrompt: Console.Error.WriteLine);
            // Returns the URL + code immediately; the token caches in the background once the
            // user completes sign-in (Completion task is intentionally not awaited here).
            var login = await provider.StartDeviceLoginAsync(ct);
            return FormatLoginPrompt(login.VerificationUrl, login.UserCode);
        });

    /// <summary>
    /// User-facing sign-in message. With a device URL + code, returns a clean, click-first
    /// prompt (the URL renders as a clickable link in the client); when both are absent —
    /// already signed in — returns the ready-to-go message. Pure so it can be unit-tested.
    /// </summary>
    public static string FormatLoginPrompt(string? verificationUrl, string? userCode)
    {
        if (string.IsNullOrWhiteSpace(verificationUrl) || string.IsNullOrWhiteSpace(userCode))
            return "You're already signed in to Power BI. Ask me to \"list my workspaces\" to pick a default.";

        return
            "🔐 Signing you in to Power BI:\n\n" +
            $"1. Open {verificationUrl}\n" +
            $"2. Enter code: {userCode}\n" +
            "3. Sign in with your account — that's it.\n\n" +
            "When you're done, ask me to \"list my workspaces\" to pick a default.";
    }

    /// <summary>
    /// Deployment-rule check via XMLA: reads a model's parameters from each pipeline stage and
    /// flags values that differ across stages (where a deployment rule / manual override applies).
    /// </summary>
    public static Task<string> PipelineRulesAsync(string pipelineId, string model, CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(pipelineId))
                throw new DaxterException("Provide a pipelineId (from daxter_pipelines).");
            if (string.IsNullOrWhiteSpace(model))
                throw new DaxterException("Provide a model (semantic dataset) name.");

            var config = TenantConfig();
            var tokens = Provider(config);
            using var rest = new PowerBiRestClient(tokens);
            var matrix = await PipelineRulesService.ComputeAsync(rest, config, tokens, pipelineId, model, ct: ct);
            var table = PipelineRulesService.ToTable(matrix);

            var body = Format(table);
            if (matrix.Notes.Count == 0) return body;
            return body + "\n/* notes:\n  - " + string.Join("\n  - ", matrix.Notes) + "\n*/";
        });

    /// <summary>
    /// Pipeline-wide audit: lists models whose parameter values are identical across every stage
    /// (no deployment rule / manual override in effect anywhere in the pipeline).
    /// </summary>
    public static Task<string> PipelineModelsWithoutRulesAsync(string pipelineId, CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(pipelineId))
                throw new DaxterException("Provide a pipelineId (from daxter_pipelines).");

            var config = TenantConfig();
            var tokens = Provider(config);
            using var rest = new PowerBiRestClient(tokens);
            var scan = await PipelineRulesService.ScanPipelineAsync(rest, config, tokens, pipelineId, 5, null, ct);
            var rows = scan.Models
                .Where(m => m.Matrix.Rows.Count > 0 && m.Matrix.Rows.All(r => !r.Differs))
                .Select(m => new object?[] { m.Model })
                .ToList();
            return Format(new QueryResult(new[] { "Model" }, rows));
        });

    /// <summary>
    /// Pipeline-wide audit: lists models whose parameter has (or doesn't have) the expected value
    /// in a chosen stage.
    /// </summary>
    public static Task<string> PipelineParamCheckAsync(
        string pipelineId, string stage, string param, string value, bool notEquals, string? model, CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(pipelineId)) throw new DaxterException("Provide a pipelineId.");
            if (string.IsNullOrWhiteSpace(stage)) throw new DaxterException("Provide a stage workspace name.");
            if (string.IsNullOrWhiteSpace(param)) throw new DaxterException("Provide a parameter name.");
            if (value is null) throw new DaxterException("Provide a value (use empty string for blank).");

            var config = TenantConfig();
            var tokens = Provider(config);
            using var rest = new PowerBiRestClient(tokens);

            // Scope: one model (fast, single read) or the whole pipeline.
            var scan = string.IsNullOrWhiteSpace(model)
                ? await PipelineRulesService.ScanPipelineAsync(rest, config, tokens, pipelineId, 5, null, ct)
                : await PipelineRulesService.ScanModelAsync(rest, config, tokens, pipelineId, model, ct: ct);

            if (scan.Stages.ToList().FindIndex(s => string.Equals(s.Workspace, stage, StringComparison.OrdinalIgnoreCase)) < 0)
                throw new DaxterException($"Stage '{stage}' isn't in this pipeline. Available: " +
                    string.Join(", ", scan.Stages.Select(s => s.Workspace)));

            // Single-model: report the actual value + pass/fail (even on a miss). Pipeline-wide: list matches.
            if (!string.IsNullOrWhiteSpace(model))
            {
                var sIdx0 = scan.Stages.ToList().FindIndex(s => string.Equals(s.Workspace, stage, StringComparison.OrdinalIgnoreCase));
                var row0 = scan.Models.FirstOrDefault()?.Matrix.Rows
                    .FirstOrDefault(r => string.Equals(r.Name, param, StringComparison.OrdinalIgnoreCase));
                var actual0 = (sIdx0 >= 0 && row0 is not null) ? row0.Values[sIdx0] : null;
                string actual, result;
                if (actual0 is null) { actual = "(param not in stage)"; result = "n/a"; }
                else
                {
                    var eq = string.Equals(actual0, value, StringComparison.Ordinal);
                    actual = actual0;
                    result = (notEquals ? !eq : eq) ? "MATCH" : "no match";
                }
                return Format(new QueryResult(
                    new[] { "Model", "Stage", "Param", "Expected", "Actual", "Result" },
                    new List<object?[]> { new object?[] { model, stage, param, (notEquals ? "!= " : "= ") + value, actual, result } }));
            }

            var sIdx = scan.Stages.ToList().FindIndex(s => string.Equals(s.Workspace, stage, StringComparison.OrdinalIgnoreCase));
            var rows = scan.Models
                .Select(m => new { m.Model, Row = m.Matrix.Rows.FirstOrDefault(r => string.Equals(r.Name, param, StringComparison.OrdinalIgnoreCase)) })
                .Where(x => x.Row is not null)
                .Select(x => new { x.Model, Value = x.Row!.Values[sIdx] })
                .Where(x => x.Value is not null && (notEquals
                    ? !string.Equals(x.Value, value, StringComparison.Ordinal)
                    : string.Equals(x.Value, value, StringComparison.Ordinal)))
                .Select(x => new object?[] { x.Model, x.Value })
                .ToList();
            return Format(new QueryResult(new[] { "Model", $"{param}@{stage}" }, rows));
        });

    /// <summary>Lists the saved audit checks (shared with the web console via ~/.daxter).</summary>
    public static Task<string> AuditListSavedAsync(CancellationToken ct)
        => Guard(() =>
        {
            var rows = new SavedAuditCheckStore().All()
                .Select(c => new object?[] { c.Name, c.PipelineId, c.Stage, c.Param, (c.NotEquals ? "!=" : "=") + " " + c.Value })
                .ToList();
            return Task.FromResult(Format(new QueryResult(new[] { "Name", "Pipeline", "Stage", "Param", "Expected" }, rows)));
        });

    /// <summary>Runs a saved audit check by name (re-runs its stored parameter-value check).</summary>
    public static Task<string> AuditRunSavedAsync(string name, CancellationToken ct)
    {
        var c = new SavedAuditCheckStore().FindByName(name);
        return c is null
            ? Task.FromResult($"No saved check named '{name}'. Use daxter_audit_list_saved to see the available names.")
            : PipelineParamCheckAsync(c.PipelineId, c.Stage, c.Param, c.Value, c.NotEquals, null, ct);
    }

    /// <summary>Runs every saved rule for a pipeline against a fresh scan; lists the models matching each.
    /// Scope to one model with <paramref name="model"/> (fast), or omit for all models.</summary>
    public static Task<string> AuditRunAllSavedAsync(string pipelineId, string? model, CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(pipelineId)) throw new DaxterException("Provide a pipelineId.");
            var rules = new SavedAuditCheckStore().All().Where(c => c.PipelineId == pipelineId).ToList();
            if (rules.Count == 0) return $"No saved rules for pipeline '{pipelineId}'. Save some in the web console first.";

            var config = TenantConfig();
            var tokens = Provider(config);
            using var rest = new PowerBiRestClient(tokens);
            var scan = string.IsNullOrWhiteSpace(model)
                ? await PipelineRulesService.ScanPipelineAsync(rest, config, tokens, pipelineId, 5, null, ct)
                : await PipelineRulesService.ScanModelAsync(rest, config, tokens, pipelineId, model, ct: ct);

            var rows = rules.Select(c =>
            {
                var r = PipelineRulesService.EvaluateRule(scan, c.Stage, c.Param, c.Value, c.NotEquals);
                return new object?[] { c.Name, c.Param, c.Stage, (c.NotEquals ? "!=" : "=") + " " + c.Value, $"{r.Matched}/{r.Checked}" };
            }).ToList();
            return Format(new QueryResult(new[] { "Rule", "Param", "Stage", "Expected", "Matches" }, rows));
        });

    /// <summary>Runs a tenant-level REST call (no workspace required).</summary>
    public static Task<string> RestTenantAsync(
        Func<PowerBiRestClient, CancellationToken, Task<QueryResult>> op, CancellationToken ct)
        => Guard(async () =>
        {
            var config = TenantConfig();
            using var rest = new PowerBiRestClient(Provider(config));
            return Format(await op(rest, ct));
        });

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
