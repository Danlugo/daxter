using System.ComponentModel;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ModelContextProtocol.Server;
using Daxter.Core;
using Daxter.Core.Artifacts;
using Daxter.Core.Audit;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Editing;
using Daxter.Core.Export;
using Daxter.Core.Formatting;
using Daxter.Core.Sql;
using Daxter.Core.Maintenance;
using Daxter.Core.Metadata;
using Daxter.Core.Query;
using Daxter.Core.Rest;
using Daxter.Core.Scheduling;

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
    /// Runs a tool body and turns any thrown exception into a returned message (the MCP SDK
    /// otherwise hides thrown-exception text behind a generic "An error occurred invoking …").
    /// <see cref="DaxterException"/> carries user-actionable guidance ("Not signed in — use
    /// daxter_login"); any other exception (e.g. an ADOMD connection-string parse error) still
    /// surfaces its underlying message instead of the opaque generic one.
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
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    public static Task<string> XmlaAsync(
        string? workspace, string? dataset, Func<IXmlaSession, QueryResult> op, CancellationToken ct)
        => Guard(async () =>
        {
            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
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

    /// <summary>Like <see cref="RestAsync"/> but the op returns ready text (not a table) — e.g. a report
    /// definition file. Workspace-scoped; no dataset required.</summary>
    public static Task<string> RestTextAsync(
        string? workspace,
        Func<PowerBiRestClient, DaxterConfig, CancellationToken, Task<string>> op, CancellationToken ct)
        => Guard(async () =>
        {
            var config = Config(workspace, null);
            using var rest = new PowerBiRestClient(Provider(config));
            return await op(rest, config, ct);
        });

    /// <summary>Takes over ownership of a model. DRY-RUN unless execute=true AND writes enabled.</summary>
    public static Task<string> TakeOverAsync(string? workspace, string? dataset, bool execute, CancellationToken ct)
        => Guard(async () =>
        {
            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
            if (string.IsNullOrWhiteSpace(config.Dataset))
                throw new DaxterException("A dataset is required to take over a model.");
            if (!execute)
                return $"DRY RUN — not applied. Would take over '{config.Dataset}' in '{config.Workspace}'. Set execute=true (with writes enabled) to do it.";
            if (!WritesAllowed())
                return "REFUSED — writes are disabled. Enable them in the web console (Configure → Allow writes) or set DAXTER_MCP_ALLOW_WRITES=true, then retry.";
            if (LooksLikeProd(config) && ProdWritesBlocked(config))
                return $"REFUSED — '{config.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).";
            using var rest = new PowerBiRestClient(Provider(config));
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, config.Dataset!, ct);
            await rest.TakeOverAsync(groupId, datasetId, ct);
            return $"Took over '{config.Dataset}' — you are now the owner.";
        });

    /// <summary>Binds a model to a gateway (optionally mapping specific connection ids). DRY-RUN unless
    /// execute=true AND writes enabled. Public REST covers on-prem/VNet gateways; shareable-cloud-connection
    /// "Maps to" is UI-only.</summary>
    /// <summary>Binds one data source of a model to a connection via the Fabric bindConnection API
    /// (any connectivity type incl. ShareableCloud). Must own the model. Gated like other writes.</summary>
    public static Task<string> BindConnectionAsync(string? workspace, string? dataset,
        string? connectionId, string connectivityType, string sourceType, string sourcePath, bool execute, CancellationToken ct)
        => Guard(async () =>
        {
            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
            if (string.IsNullOrWhiteSpace(config.Dataset))
                throw new DaxterException("A dataset is required to bind a connection.");
            if (string.IsNullOrWhiteSpace(connectivityType))
                throw new DaxterException("connectivityType is required (ShareableCloud | OnPremisesGateway | VirtualNetworkGateway | PersonalCloud | Automatic | None).");
            if (string.IsNullOrWhiteSpace(sourceType) || string.IsNullOrWhiteSpace(sourcePath))
                throw new DaxterException("sourceType + sourcePath are required (identify the data source — see daxter_item_connections).");

            var plan = $"bind {sourceType} source '{sourcePath}' of '{config.Dataset}' to {connectivityType}"
                       + (string.IsNullOrWhiteSpace(connectionId) ? "" : $" connection {connectionId}");
            if (!execute)
                return $"DRY RUN — not applied. Would {plan}. Set execute=true (with writes enabled) to do it.";
            if (!WritesAllowed())
                return "REFUSED — writes are disabled. Enable them in the web console (Configure → Allow writes) or set DAXTER_MCP_ALLOW_WRITES=true, then retry.";
            if (LooksLikeProd(config) && ProdWritesBlocked(config))
                return $"REFUSED — '{config.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).";

            using var rest = new PowerBiRestClient(Provider(config));
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, config.Dataset!, ct);
            await rest.BindConnectionAsync(groupId, datasetId, connectionId, connectivityType, sourceType, sourcePath, ct);
            return $"Bound — {plan}. (Requires model ownership; run daxter_take_over first if it fails with a permission error.)";
        });

    public static Task<string> BindToGatewayAsync(string? workspace, string? dataset, string gatewayObjectId,
        IReadOnlyList<string>? datasourceObjectIds, bool execute, CancellationToken ct)
        => Guard(async () =>
        {
            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
            if (string.IsNullOrWhiteSpace(config.Dataset))
                throw new DaxterException("A dataset is required to bind a model.");
            if (string.IsNullOrWhiteSpace(gatewayObjectId))
                throw new DaxterException("gatewayObjectId is required (see daxter_discover_gateways).");
            var n = datasourceObjectIds?.Count ?? 0;
            var plan = $"bind '{config.Dataset}' to gateway {gatewayObjectId}" + (n > 0 ? $" ({n} data source(s) mapped)" : "");
            if (!execute)
                return $"DRY RUN — not applied. Would {plan}. Set execute=true (with writes enabled) to do it.";
            if (!WritesAllowed())
                return "REFUSED — writes are disabled. Enable them in the web console (Configure → Allow writes) or set DAXTER_MCP_ALLOW_WRITES=true, then retry.";
            if (LooksLikeProd(config) && ProdWritesBlocked(config))
                return $"REFUSED — '{config.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).";
            using var rest = new PowerBiRestClient(Provider(config));
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, config.Dataset!, ct);
            await rest.BindToGatewayAsync(groupId, datasetId, gatewayObjectId, datasourceObjectIds, ct);
            return $"Bound '{config.Dataset}' to gateway {gatewayObjectId}" + (n > 0 ? $" — {n} data source(s) mapped." : ".");
        });

    public static Task<string> DiffMeasuresAsync(
        string? workspace, string? dataset, string other, CancellationToken ct)
        => Guard(async () =>
        {
            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
            var left = new AdomdXmlaSessionFactory(config, Provider(config));
            var rightConfig = await ResolveTargetsAsync(WithDataset(config, other), ct);
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
        string? workspace, string? dataset, Func<MaintenanceService, string> build, bool execute,
        CancellationToken ct, int retries = 0)
        => Guard(async () =>
        {
            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
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

            if (LooksLikeProd(config) && ProdWritesBlocked(config))
            {
                return $"REFUSED — '{config.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).\n" + command;
            }

            var notes = new List<string>();
            RetryPolicy.Execute(() => service.Execute(command), retries,
                onRetry: (attempt, total, ex) => notes.Add($"transient failure (retry {attempt}/{total}): {ex.Message}"));
            var prefix = notes.Count > 0 ? string.Join("\n", notes) + "\n" : "";
            return $"{prefix}EXECUTED:\n" + command;
        });

    /// <summary>
    /// Gated model edit: builds the TMSL and returns it as a dry-run unless <paramref name="execute"/>
    /// is set AND BOTH writes (Allow writes) AND model editing (<c>DAXTER_MCP_ALLOW_MODEL_EDIT</c>) are
    /// enabled. Before applying, exports a <c>.bim</c> backup — the only "undo", since an XMLA write
    /// permanently blocks downloading the model as a PBIX.
    /// </summary>
    public static Task<string> ModelEditAsync(
        string? workspace, string? dataset, Func<ModelEditService, string> build, bool execute, CancellationToken ct)
        => Guard(async () =>
        {
            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
            if (string.IsNullOrWhiteSpace(config.Dataset))
            {
                throw new DaxterException("A dataset is required for model edits.");
            }

            var token = await Provider(config).GetTokenAsync(ct);
            using var service = new ModelEditService(config, token);
            var change = build(service);   // stages the change in-memory (TOM); not yet saved

            const string caveat =
                "\n\n⚠ Editing a Power BI Desktop model over XMLA is IRREVERSIBLE for PBIX download — the " +
                "model can no longer be downloaded as a .pbix. A .bim backup is taken before applying.";

            if (!execute)
            {
                return "DRY RUN — not applied:\n" + change + caveat;
            }

            if (!WritesAllowed())
            {
                return "REFUSED — writes are disabled. Enable them in the web console " +
                       "(Configure → Allow writes) or set DAXTER_MCP_ALLOW_WRITES=true, then retry.\n" + change;
            }

            if (!ModelEditAllowed())
            {
                return "REFUSED — model editing is disabled (a separate, stricter gate than refresh writes). " +
                       "Enable it in the web console (Configure → Allow model edits) or set " +
                       "DAXTER_MCP_ALLOW_MODEL_EDIT=true, then retry." + caveat + "\n" + change;
            }

            if (LooksLikeProd(config) && ProdWritesBlocked(config))
            {
                return $"REFUSED — '{config.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).\n" + change;
            }

            var backup = new ModelBackupService(config, token).Backup();
            service.Apply();
            return $"APPLIED (backup: {backup}):\n" + change;
        });

    /// <summary>
    /// Gated refresh that <b>enqueues</b> a job onto the shared refresh queue rather than executing it
    /// inline. The single worker (hosted by the web container) drains the queue, serialized one refresh
    /// per model. Dry-run returns the plan without queueing; execute enqueues once writes are enabled.
    /// This is how the MCP server "always routes refreshes through the scheduler".
    /// </summary>
    public static Task<string> EnqueueRefreshAsync(
        string? workspace, string? dataset,
        RefreshKind kind, string? table, string? partition,
        PartitionOrder order, RefreshType type, IReadOnlyList<string>? partitions,
        bool execute, int retries, CancellationToken ct)
        => Guard(async () =>
        {
            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
            if (string.IsNullOrWhiteSpace(config.Dataset))
            {
                throw new DaxterException("A dataset is required for refresh operations.");
            }

            var spec = new RefreshSpec(kind, config.Workspace!, config.Dataset!, table, partition, order, type, partitions, retries);
            var plan = RefreshTitle.Describe(spec);

            if (!execute)
            {
                return $"DRY RUN — not queued. Would queue: {plan}. Set execute=true (with writes enabled) to queue it.";
            }

            if (!WritesAllowed())
            {
                return "REFUSED — writes are disabled. Enable them in the web console " +
                       "(Configure → Allow writes) or set DAXTER_MCP_ALLOW_WRITES=true, then retry.";
            }

            if (LooksLikeProd(config) && ProdWritesBlocked(config))
            {
                return $"REFUSED — '{config.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).";
            }

            var store = new RefreshQueueStore();
            var job = store.Enqueue(spec, RefreshTitle.For(spec), JobOrigin.Mcp);
            return $"QUEUED as job #{job.Id} — {plan}. The DAXter worker (web container) runs it, serialized one refresh per model.{WorkerWarning(store)} Track with daxter_refresh_jobs.";
        });

    /// <summary>Re-runs a finished/interrupted job. When <paramref name="remainingOnly"/> and the job is a
    /// partition refresh that stopped partway, queues a SomePartitions job for just the not-yet-done
    /// partitions; otherwise a full re-run. Same write gate as a normal enqueue.</summary>
    public static Task<string> ResumeRefreshAsync(int jobId, bool remainingOnly, bool execute, CancellationToken ct)
        => Guard(() =>
        {
            var store = new RefreshQueueStore();
            var resume = store.ResumeSpec(jobId, remainingOnly);
            if (resume is null)
                return Task.FromResult($"Job #{jobId} not found.");

            var (spec, count, partial) = resume.Value;
            var plan = RefreshTitle.Describe(spec);
            var what = partial ? $"resume {count} remaining partition(s)" : "full re-run";

            if (!execute)
                return Task.FromResult($"DRY RUN — not queued. Would {what}: {plan}. Set execute=true (with writes enabled) to queue it.");
            if (!WritesAllowed())
                return Task.FromResult("REFUSED — writes are disabled. Enable them in the web console " +
                    "(Configure → Allow writes) or set DAXTER_MCP_ALLOW_WRITES=true, then retry.");

            var config = Config(spec.Workspace, spec.Dataset);
            if (LooksLikeProd(config) && ProdWritesBlocked(config))
                return Task.FromResult($"REFUSED — '{spec.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).");

            var job = store.Enqueue(spec, RefreshTitle.For(spec), JobOrigin.Mcp);
            return Task.FromResult($"QUEUED as job #{job.Id} ({what}) — {plan}.{WorkerWarning(store)} Track with daxter_refresh_jobs.");
        });

    /// <summary>A warning suffix when no live worker is draining the queue, else empty.</summary>
    internal static string WorkerWarning(RefreshQueueStore store)
    {
        var age = store.HeartbeatAge();
        return age is null || age > TimeSpan.FromSeconds(30)
            ? " ⚠ No refresh worker is currently running — start the DAXter web container (it hosts the worker) so queued jobs execute."
            : "";
    }

    /// <summary>JSON snapshot of the shared refresh queue (all interfaces), plus worker liveness.</summary>
    public static string RefreshJobsJson(string? workspace, string? dataset, int top)
    {
        var store = new RefreshQueueStore();
        var jobs = (!string.IsNullOrWhiteSpace(workspace) && !string.IsNullOrWhiteSpace(dataset))
            ? store.For(workspace, dataset)
            : store.All();

        var age = store.HeartbeatAge();
        var worker = age is null ? "none"
            : age > TimeSpan.FromSeconds(30) ? $"stale ({(int)age.Value.TotalSeconds}s ago)"
            : "alive";

        var list = jobs.Take(Math.Max(1, top)).Select(j => new
        {
            id = j.Id,
            status = j.Status.ToString(),
            origin = j.Origin.ToString(),
            title = j.Title,
            step = j.Step,
            model = $"{j.Spec.Workspace} / {j.Spec.Dataset}",
            partitions = j.PartitionTotal is { } t ? $"{j.PartitionDone ?? 0}/{t}" : null,
            created = j.Created,
            started = j.Started,
            finished = j.Finished,
            error = j.Error,
            // Tells the calling agent exactly how to recover a failed/interrupted job — and that resume
            // picks up where it left off (only the not-yet-done partitions).
            resume_hint = ResumeHint(j),
        });

        return JsonSerializer.Serialize(
            new { worker, total = jobs.Count, jobs = list },
            new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>For a failed/interrupted/canceled job, the exact <c>daxter_resume_refresh</c> call to
    /// recover it — and whether it will pick up where it left off (only the remaining partitions) or be a
    /// full re-run. Null for active/succeeded jobs. Lets the calling agent self-recover on failure.</summary>
    private static string? ResumeHint(RefreshJob j)
    {
        if (j.Status is not (JobStatus.Failed or JobStatus.Interrupted or JobStatus.Canceled))
            return null;
        var done = j.PartitionDone ?? 0;
        if (j.Spec.Kind is RefreshKind.AllPartitions or RefreshKind.SomePartitions
            && j.OrderedPartitions is { Count: > 0 } ord && done > 0 && done < ord.Count)
            return $"{j.Status} — resume to pick up where it left off: re-run the remaining {ord.Count - done} " +
                   $"partition(s) with daxter_resume_refresh(job_id={j.Id}, execute=true).";
        return $"{j.Status} — re-run with daxter_resume_refresh(job_id={j.Id}, execute=true) " +
               "(full re-run; no partial partition progress was recorded for this job).";
    }

    /// <summary>The full catalogue of registered MCP tools — name, title, read/write kind, and description —
    /// plus the running version. Built by <b>reflecting the registered tools at runtime</b>, so it is always
    /// complete and never drifts: every release automatically surfaces its new features here. This is how an
    /// agent discovers everything DAXter can do in one call.</summary>
    public static string CapabilitiesJson()
    {
        var tools = typeof(DaxterTools).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Select(m => (m, attr: m.GetCustomAttribute<McpServerToolAttribute>()))
            .Where(x => x.attr is not null)
            .Select(x => new
            {
                name = x.attr!.Name ?? x.m.Name,
                title = x.attr.Title,
                // Check ReadOnly FIRST: the McpServerToolAttribute's Destructive default is true, so
                // a tool that's explicitly ReadOnly = true (e.g. daxter_sql_query, daxter_rls,
                // daxter_role_filters) would otherwise fall into the "destructive write" bucket and
                // agents would refuse to run it without a confirmation. Read-first matches intent.
                kind = x.attr.ReadOnly == true ? "read"
                     : x.attr.Destructive == true ? "write (gated, destructive)"
                     : "write (gated)",
                description = x.m.GetCustomAttribute<DescriptionAttribute>()?.Description ?? "",
            })
            .OrderBy(t => t.name, StringComparer.Ordinal)
            .ToList();

        var version = Environment.GetEnvironmentVariable("DAXTER_VERSION") ?? "dev";
        return JsonSerializer.Serialize(
            new { version, tool_count = tools.Count, tools },
            new JsonSerializerOptions { WriteIndented = true });
    }

    public static PartitionOrder ParseOrder(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "newest-first" or "newest" => PartitionOrder.NewestFirst,
        "oldest-first" or "oldest" => PartitionOrder.OldestFirst,
        _ => throw new DaxterException($"Unknown order '{value}'. Use newest-first or oldest-first."),
    };

    /// <summary>Writes are enabled by the server env var OR the web console's saved "Allow writes" toggle
    /// (the shared <c>~/.daxter/console-config.json</c>, read via <see cref="PersistedSettings"/>).</summary>
    internal static bool WritesAllowed()
        => string.Equals(Environment.GetEnvironmentVariable("DAXTER_MCP_ALLOW_WRITES"), "true", StringComparison.OrdinalIgnoreCase)
           || PersistedSettings.Load().AllowWrites;

    /// <summary>Model editing is enabled by <c>DAXTER_MCP_ALLOW_MODEL_EDIT=true</c> OR the web console's
    /// "Allow model edits" toggle — a separate, stricter gate than refresh writes (edits are irreversible
    /// for PBIX download).</summary>
    internal static bool ModelEditAllowed()
        => string.Equals(Environment.GetEnvironmentVariable("DAXTER_MCP_ALLOW_MODEL_EDIT"), "true", StringComparison.OrdinalIgnoreCase)
           || PersistedSettings.Load().AllowModelEdit;

    /// <summary>True when the read-only/prod-block guardrail is active for the MCP server. The
    /// gate fires automatically when the user has configured EXPLICIT read-only or write-allowed
    /// patterns (the new model — they told us what's locked), and stays opt-in via
    /// <c>DAXTER_MCP_BLOCK_PROD_WRITES=true</c> for the legacy heuristic-only setups so existing
    /// installations don't suddenly start refusing.</summary>
    internal static bool ProdWritesBlocked(DaxterConfig config)
    {
        // Explicit user config → always enforce. This is the new behavior; if the user listed a
        // workspace as read-only on the Configure page, an MCP agent must respect that.
        if (config.ReadOnlyWorkspaces.Count > 0 || config.WriteWorkspaces.Count > 0) return true;
        // Legacy: only the env var or the "*prod*" heuristic — keep opt-in for backwards compat.
        return string.Equals(Environment.GetEnvironmentVariable("DAXTER_MCP_BLOCK_PROD_WRITES"), "true", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Backwards-compat shim — callers without a config in hand get the env-var-only check.
    /// Prefer the typed overload for accurate enforcement of the new read-only/allow-list rules.</summary>
    internal static bool ProdWritesBlocked()
        => string.Equals(Environment.GetEnvironmentVariable("DAXTER_MCP_BLOCK_PROD_WRITES"), "true", StringComparison.OrdinalIgnoreCase);

    private static bool LooksLikeProd(DaxterConfig config) => config.IsReadOnlyTarget();

    private const int ExportCap = 40_000;

    /// <summary>Exports the model definition (.bim) via TOM; truncates very large output.</summary>
    public static Task<string> ExportAsync(string? workspace, string? dataset, CancellationToken ct)
        => Guard(async () =>
        {
            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
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

            var config = await ResolveTargetsAsync(Config(workspace, dataset), ct);
            var factory = new AdomdXmlaSessionFactory(config, Provider(config), role, user);
            using var session = await factory.CreateAsync(ct);
            return Format(session.Execute(query));
        });

    private static DaxterConfig Config(string? workspace, string? dataset)
        => DaxterConfig.FromEnvironment(workspace: workspace, dataset: dataset);

    /// <summary>
    /// The XMLA endpoint addresses a workspace and dataset by NAME, but a caller may pass a GUID
    /// id for either. When one is a GUID, resolve it to its canonical name via REST so both the
    /// XMLA connection string and the TMSL refresh target use the real name. Plain names pass
    /// through untouched — no REST round-trip in the common case.
    /// </summary>
    private static async Task<DaxterConfig> ResolveTargetsAsync(DaxterConfig config, CancellationToken ct)
    {
        var wsIsId = !string.IsNullOrWhiteSpace(config.Workspace) && Guid.TryParse(config.Workspace!.Trim(), out _);
        var dsIsId = !string.IsNullOrWhiteSpace(config.Dataset) && Guid.TryParse(config.Dataset!.Trim(), out _);
        if (!wsIsId && !dsIsId)
        {
            return config;
        }

        using var rest = new PowerBiRestClient(Provider(config));
        var workspace = config.Workspace;
        if (wsIsId)
        {
            workspace = await rest.GroupNameByIdAsync(config.Workspace!.Trim(), ct);
        }

        var dataset = config.Dataset;
        if (dsIsId)
        {
            var groupId = wsIsId
                ? config.Workspace!.Trim()
                : await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            dataset = await rest.DatasetNameByIdAsync(groupId, config.Dataset!.Trim(), ct);
        }

        return WithWorkspaceDataset(config, workspace, dataset);
    }

    private static DaxterConfig WithWorkspaceDataset(DaxterConfig c, string workspace, string? dataset) => new()
    {
        Workspace = workspace,
        Dataset = dataset,
        TenantId = c.TenantId,
        ClientId = c.ClientId,
        ClientSecret = c.ClientSecret,
        AuthMode = c.AuthMode,
        Environment = c.Environment,
    };

    private static ITokenProvider Provider(DaxterConfig config)
        // Headless: never block on an interactive device-code prompt the user can't see —
        // normal tools require a token cached by `daxter_login`. Device-code prompt → stderr.
        => new MsalTokenProvider(config, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: false);

    // Same MsalTokenProvider instance type — it implements both ITokenProvider (XMLA/REST scope) and
    // IFabricSqlTokenProvider (database.windows.net scope). One MSAL account underneath; the user
    // signs in once via daxter_login and the SQL tools silently get a SQL-scope token.
    private static MsalTokenProvider MsalProvider(DaxterConfig config)
        => new MsalTokenProvider(config, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: false);

    // ---- Fabric SQL endpoints ----

    /// <summary>Lists Fabric SQL endpoints (Warehouses + Lakehouse SQL endpoints) in a workspace via
    /// Fabric REST. Read-only; no SQL token needed for discovery.</summary>
    public static Task<string> SqlEndpointsAsync(string? workspace, CancellationToken ct)
        => Guard(async () =>
        {
            var config = Config(workspace, null);
            using var rest = new PowerBiRestClient(Provider(config));
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            return Format(await rest.SqlEndpointsAsync(groupId, ct));
        });

    /// <summary>Lists schemas + tables/views/functions/stored procedures on a Fabric SQL endpoint.
    /// One INFORMATION_SCHEMA round-trip; resolves endpoint NAME → (server, database) via the
    /// workspace's discovery list so the caller doesn't need the GUID hostname. Always read-only.</summary>
    public static Task<string> SqlObjectsAsync(string? workspace, string endpointName, CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(endpointName))
                throw new DaxterException("endpoint is required (the Warehouse or Lakehouse name — see daxter_sql_endpoints).");
            var config = Config(workspace, null);
            var msal = MsalProvider(config);

            using var rest = new PowerBiRestClient(msal);
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            var list = await rest.SqlEndpointsAsync(groupId, ct);
            var match = list.Rows.FirstOrDefault(r =>
                string.Equals(r[0]?.ToString(), endpointName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new DaxterException(
                    $"Endpoint '{endpointName}' not found in '{config.Workspace}'. Call daxter_sql_endpoints to list available endpoints.");

            var server = match[1]?.ToString() ?? "";
            var database = match[2]?.ToString() ?? "";
            var client = new FabricSqlClient(msal);
            return Format(await client.ListObjectsAsync(server, database, ct));
        });

    /// <summary>Streams a Fabric SQL endpoint's full result set as CSV to a file on the persistent
    /// token volume (<c>~/.daxter/exports/sql/</c>). Bypasses the in-memory materialization that
    /// <c>daxter_sql_query</c> uses — safe for <c>SELECT *</c> on multi-million-row tables. Returns
    /// a status line with the on-disk path and the row count; the user can <c>docker cp</c> the file
    /// off the container (or it's already on the host if the volume is host-mounted). Read-only by
    /// default; non-SELECT requires the writes gate same as the live-query path.</summary>
    public static Task<string> SqlExportAsync(
        string? workspace, string endpointName, string sql, CancellationToken ct,
        bool quoteAll = false, bool crlf = false, string? artifactKey = null)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(endpointName))
                throw new DaxterException("endpoint is required (the Warehouse or Lakehouse name — see daxter_sql_endpoints).");
            if (string.IsNullOrWhiteSpace(sql))
                throw new DaxterException("sql is required.");

            var config = Config(workspace, null);
            var isRead = SqlWriteGate.IsReadOnly(sql);
            if (!isRead && !WritesAllowed())
                return "REFUSED — this statement is not read-only and writes are disabled. " +
                       "Enable them (Configure → Allow writes, or DAXTER_MCP_ALLOW_WRITES=true) to export it.";
            if (!isRead && LooksLikeProd(config) && ProdWritesBlocked(config))
                return $"REFUSED — '{config.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).";

            var msal = MsalProvider(config);
            using var rest = new PowerBiRestClient(msal);
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            var list = await rest.SqlEndpointsAsync(groupId, ct);
            var match = list.Rows.FirstOrDefault(r =>
                string.Equals(r[0]?.ToString(), endpointName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new DaxterException(
                    $"Endpoint '{endpointName}' not found in '{config.Workspace}'. Call daxter_sql_endpoints first.");

            var server = match[1]?.ToString() ?? "";
            var database = match[2]?.ToString() ?? "";

            // Persistent volume — survives container restarts and lets the user docker cp off.
            var home = Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath();
            var dir = Path.Combine(home, ".daxter", "exports", "sql");
            Directory.CreateDirectory(dir);
            var ts = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmss");
            var safeEndpoint = string.Join("-", endpointName.Split(Path.GetInvalidFileNameChars()));
            var path = Path.Combine(dir, $"{ts}-{safeEndpoint}.csv");

            var style = new CsvStyle(QuoteAll: quoteAll, Crlf: crlf);
            var client = new FabricSqlClient(msal);
            await using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using var sw = new StreamWriter(fs);
            var rows = await client.StreamCsvAsync(server, database, sql, allowWrite: !isRead && WritesAllowed(), sw, ct, style: style);
            await sw.FlushAsync(ct);
            await fs.FlushAsync(ct);

            // Optional second hop — also persist the bytes into the artifact store under a
            // caller-chosen key. Lets the agent then fetch the CSV via daxter_artifact_get /
            // bundle, with no docker-cp / bind-mount required. Source-tool stamp survives so
            // the /artifacts page shows where each artifact came from.
            string? artifactLine = null;
            if (!string.IsNullOrWhiteSpace(artifactKey))
            {
                await using var src = File.OpenRead(path);
                var aref = await Artifacts.PutAsync(artifactKey, src,
                    new ArtifactMeta(SourceTool: "daxter_sql_export"), ct);
                artifactLine = $" Mirrored to artifact '{aref.Key}' ({aref.Bytes:N0} bytes) — fetch via daxter_artifact_get.";
            }

            return $"Wrote {rows} row{(rows == 1 ? "" : "s")} to {path} on the container's persistent volume" +
                   (quoteAll || crlf ? $" (style: QuoteAll={quoteAll}, CRLF={crlf})" : "") +
                   (artifactLine ?? $" (use `docker cp daxter-mcp-…:{path} ./` to pull it onto your host, or pass artifactKey to mirror to the artifact store).");
        });

    /// <summary>Runs T-SQL on a Fabric SQL endpoint. Resolves (server, database) from the workspace
    /// + endpoint name via <see cref="PowerBiRestClient.SqlEndpointsAsync"/> (so the caller passes the
    /// friendly name, not the GUID hostname). Read-only T-SQL runs unconditionally; non-read T-SQL is
    /// gated by <see cref="WritesAllowed"/> the same way the model-mutating tools are — there's no
    /// "set this for SQL only" mode. Production-target block applies too.</summary>
    public static Task<string> SqlQueryAsync(
        string? workspace, string endpointName, string sql, CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(endpointName))
                throw new DaxterException("endpoint is required (the Warehouse or Lakehouse name — see daxter_sql_endpoints).");
            if (string.IsNullOrWhiteSpace(sql))
                throw new DaxterException("sql is required.");

            var config = Config(workspace, null);
            var isRead = SqlWriteGate.IsReadOnly(sql);
            if (!isRead && !WritesAllowed())
                return "REFUSED — this statement is not read-only and writes are disabled. " +
                       "Enable them in the web console (Configure → Allow writes) or set DAXTER_MCP_ALLOW_WRITES=true, then retry.";
            if (!isRead && LooksLikeProd(config) && ProdWritesBlocked(config))
                return $"REFUSED — '{config.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).";

            var msal = MsalProvider(config);

            // Resolve endpoint -> (server, database) via Fabric REST so the agent didn't have to
            // know the GUID hostname.
            using var rest = new PowerBiRestClient(msal);
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            var list = await rest.SqlEndpointsAsync(groupId, ct);
            var match = list.Rows.FirstOrDefault(r =>
                string.Equals(r[0]?.ToString(), endpointName, StringComparison.OrdinalIgnoreCase));
            if (match is null)
                throw new DaxterException(
                    $"Endpoint '{endpointName}' not found in '{config.Workspace}'. Call daxter_sql_endpoints to list available endpoints.");

            var server = match[1]?.ToString() ?? "";
            var database = match[2]?.ToString() ?? "";
            var client = new FabricSqlClient(msal);
            var result = await client.ExecuteAsync(server, database, sql, allowWrite: !isRead && WritesAllowed(), ct);
            return Format(result);
        });

    /// <summary>Config for tenant-level ops (list workspaces, gateways, sign-in) — no workspace needed.</summary>
    private static DaxterConfig TenantConfig() => DaxterConfig.FromEnvironment(requireWorkspace: false);

    // ---- Fabric items: Copy Jobs + Notebooks ----

    public static Task<string> CopyJobsAsync(string? workspace, CancellationToken ct)
        => RestAsync(workspace, null, async (rest, cfg, c) =>
            await rest.CopyJobsAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, c), c), ct);

    public static Task<string> NotebooksAsync(string? workspace, CancellationToken ct)
        => RestAsync(workspace, null, async (rest, cfg, c) =>
            await rest.NotebooksAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, c), c), ct);

    /// <summary>Returns the Copy Job's <c>copyjob-content.json</c> as a single text blob so the
    /// agent can read the source/sink/mapping config in one call (same content Fabric's UI shows
    /// under "View JSON code"). Multiple parts are joined with a <c># path:</c> marker so they're
    /// distinguishable.</summary>
    public static Task<string> CopyJobDefinitionAsync(string? workspace, string copyJobId, CancellationToken ct)
        => RestTextAsync(workspace, async (rest, cfg, c) =>
        {
            var groupId = await rest.ResolveGroupIdAsync(cfg.Workspace, c);
            var parts = await rest.CopyJobDefinitionAsync(groupId, copyJobId, c);
            return JoinDefinitionParts(parts);
        }, ct);

    /// <summary>Returns the Notebook's full definition (the cells, in standard Jupyter ipynb format
    /// by default — pass <paramref name="format"/> = "FabricGitSource" for the language-specific
    /// source file instead).</summary>
    public static Task<string> NotebookDefinitionAsync(string? workspace, string notebookId, string? format, CancellationToken ct)
        => RestTextAsync(workspace, async (rest, cfg, c) =>
        {
            var groupId = await rest.ResolveGroupIdAsync(cfg.Workspace, c);
            var parts = await rest.NotebookDefinitionAsync(groupId, notebookId, format ?? "ipynb", c);
            return JoinDefinitionParts(parts);
        }, ct);

    /// <summary>Runs an item-job on demand (Copy Job: jobType=Execute · Notebook: jobType=RunNotebook).
    /// DRY-RUN unless execute=true AND writes enabled. Returns the new instance id on success so the
    /// agent can poll daxter_item_job_status to track it.</summary>
    public static Task<string> RunItemJobAsync(
        string? workspace, string itemId, string jobType, string? executionData, bool execute, CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(itemId)) throw new DaxterException("itemId is required.");
            if (string.IsNullOrWhiteSpace(jobType)) throw new DaxterException("jobType is required (Execute | RunNotebook | DefaultJob).");
            var config = Config(workspace, null);
            var plan = $"run item {itemId} (jobType={jobType}) in '{config.Workspace}'";
            if (!execute)
                return $"DRY RUN — not applied. Would {plan}. Set execute=true (with writes enabled) to do it.";
            if (!WritesAllowed())
                return "REFUSED — writes are disabled. Enable them (Configure → Allow writes, or DAXTER_MCP_ALLOW_WRITES=true) to run a job.";
            if (LooksLikeProd(config) && ProdWritesBlocked(config))
                return $"REFUSED — '{config.Workspace}' is READ-ONLY ({config.ReadOnlyReason() ?? "prod-block guardrail active"}).";

            using var rest = new PowerBiRestClient(Provider(config));
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            var instanceId = await rest.StartItemJobAsync(groupId, itemId, jobType, executionData, ct);
            return $"Started — instanceId: {instanceId}. Poll daxter_item_job_status (or list with daxter_item_runs) to track it.";
        });

    public static Task<string> ItemRunsAsync(string? workspace, string itemId, CancellationToken ct)
        => RestAsync(workspace, null, async (rest, cfg, c) =>
            await rest.ListItemJobInstancesAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, c), itemId, c), ct);

    public static Task<string> ItemJobStatusAsync(string? workspace, string itemId, string instanceId, CancellationToken ct)
        => RestTextAsync(workspace, async (rest, cfg, c) =>
        {
            var groupId = await rest.ResolveGroupIdAsync(cfg.Workspace, c);
            var instance = await rest.GetItemJobInstanceAsync(groupId, itemId, instanceId, c);
            return JsonSerializer.Serialize(instance, new JsonSerializerOptions { WriteIndented = true });
        }, ct);

    /// <summary>Cancels a running item-job instance. DRY-RUN unless execute=true AND writes enabled.</summary>
    public static Task<string> CancelItemJobAsync(
        string? workspace, string itemId, string instanceId, bool execute, CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(itemId) || string.IsNullOrWhiteSpace(instanceId))
                throw new DaxterException("itemId and instanceId are required.");
            var config = Config(workspace, null);
            var plan = $"cancel job instance {instanceId} on item {itemId} in '{config.Workspace}'";
            if (!execute)
                return $"DRY RUN — not applied. Would {plan}. Set execute=true (with writes enabled) to do it.";
            if (!WritesAllowed())
                return "REFUSED — writes are disabled. Enable them (Configure → Allow writes, or DAXTER_MCP_ALLOW_WRITES=true) to cancel a job.";

            using var rest = new PowerBiRestClient(Provider(config));
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace!, ct);
            await rest.CancelItemJobInstanceAsync(groupId, itemId, instanceId, ct);
            return $"Cancellation requested for instance {instanceId}. The next status poll should report 'Cancelled'.";
        });

    /// <summary>Stitches a Fabric item-definition parts list into one text blob the agent can read
    /// directly. Multiple parts (rare for Copy Jobs / Notebooks) are joined with a path marker so
    /// the consumer can split them apart if it needs to.</summary>
    private static string JoinDefinitionParts(IReadOnlyList<FabricItemPart> parts)
    {
        if (parts.Count == 0) return "(empty definition)";
        if (parts.Count == 1) return parts[0].Content;
        var sb = new StringBuilder();
        foreach (var p in parts)
        {
            sb.Append("# ----- ").AppendLine(p.Path);
            sb.AppendLine(p.Content);
            sb.AppendLine();
        }
        return sb.ToString();
    }

    /// <summary>Starts interactive sign-in and returns a clean, click-first device-code message.
    /// <paramref name="target"/> selects WHICH scope to sign in for: <c>"powerbi"</c> (default —
    /// XMLA + Fabric REST) or <c>"sql"</c> (Fabric SQL endpoint, separate client id, separate cache
    /// entry — required once because Power BI's first-party client id isn't pre-authorized for
    /// <c>database.windows.net</c>).</summary>
    public static Task<string> LoginAsync(CancellationToken ct, string? target = null)
        => Guard(async () =>
        {
            var provider = new MsalTokenProvider(TenantConfig(), deviceCodePrompt: Console.Error.WriteLine);
            // Returns the URL + code immediately; the token caches in the background once the
            // user completes sign-in (Completion task is intentionally not awaited here).
            var login = string.Equals(target, "sql", StringComparison.OrdinalIgnoreCase)
                ? await provider.StartFabricSqlDeviceLoginAsync(ct)
                : await provider.StartDeviceLoginAsync(ct);
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

    // ── Artifact-store MCP runtime ────────────────────────────────────────────────────────────
    // The MCP server is long-lived (one stdio process per Claude Desktop session), so we use a
    // process-wide singleton store. The Web host already does its own DI registration; CLI
    // commands instantiate per-invocation. All three converge on the same on-disk root, so
    // bytes written by one surface are immediately visible to the others.
    //
    // INLINE-vs-URL strategy. MCP can carry bytes inline as base64, but blowing through the
    // protocol's per-message budget with a 100MB .pbix is a recipe for the agent silently
    // truncating. For artifacts ≤ this threshold we return base64; above it we return an
    // HTTPS URL pointing at the /api/artifacts endpoint the Web host exposes. The agent then
    // streams it directly (no MCP overhead). The threshold is conservative — it can grow once
    // we've measured real-world payload sizes.
    private const int InlineBytesThreshold = 10 * 1024 * 1024;   // 10 MB
    private static readonly Lazy<LocalArtifactStore> ArtifactsLazy = new(() => new LocalArtifactStore());
    public static IArtifactStore Artifacts => ArtifactsLazy.Value;

    /// <summary>List artifacts as a JSON envelope the agent can iterate. Returns up to
    /// <see cref="RowCap"/> entries (then truncates) so a large store can't blow the context.</summary>
    public static Task<string> ArtifactListAsync(string? prefix, CancellationToken ct)
        => Guard(async () =>
        {
            var items = await Artifacts.ListAsync(prefix, ct);
            var capped = items.Count <= RowCap ? items : items.Take(RowCap).ToList();
            var usage = await Artifacts.CurrentUsageBytesAsync(ct);
            var envelope = new
            {
                items = capped.Select(a => new
                {
                    key = a.Key,
                    bytes = a.Bytes,
                    created_utc = a.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    expires_utc = a.ExpiresAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    source_tool = a.SourceTool,
                }),
                total_listed = items.Count,
                returned = capped.Count,
                truncated = items.Count > capped.Count,
                usage_bytes = usage,
                quota_bytes = Artifacts.QuotaBytes,
            };
            return System.Text.Json.JsonSerializer.Serialize(envelope,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        });

    /// <summary>Get a single artifact. ≤ 10 MB: return base64 inline so the agent can parse it
    /// immediately (PBIR JSON, copy-job def). &gt; 10 MB: return an HTTP URL and prompt the
    /// agent to fetch it via the streaming endpoint (a 200 MB .pbix never enters the MCP body).</summary>
    public static Task<string> ArtifactGetAsync(string key, CancellationToken ct)
        => Guard(async () =>
        {
            var meta = await Artifacts.GetMetaAsync(key, ct)
                ?? throw new DaxterException($"Artifact not found: {key}");

            if (meta.Bytes > InlineBytesThreshold)
            {
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    key = meta.Key,
                    bytes = meta.Bytes,
                    download_url = $"/api/artifacts/{Uri.EscapeDataString(meta.Key).Replace("%2F", "/")}",
                    inline = false,
                    note = $"Artifact exceeds {InlineBytesThreshold:N0} bytes — fetch it via the URL " +
                           "(GET against the DAXter web host, e.g. http://localhost:8088 + download_url).",
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }

            await using var fs = await Artifacts.OpenReadAsync(key, ct);
            using var ms = new MemoryStream();
            await fs.CopyToAsync(ms, ct);
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                key = meta.Key,
                bytes = meta.Bytes,
                created_utc = meta.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                expires_utc = meta.ExpiresAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                source_tool = meta.SourceTool,
                inline = true,
                content_base64 = Convert.ToBase64String(ms.ToArray()),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        });

    /// <summary>Zip a prefix and return either inline base64 (≤ threshold) or a download URL.
    /// The Web /api/artifacts/{key}?bundle=1 endpoint serves the streaming version.</summary>
    public static Task<string> ArtifactBundleAsync(string prefix, CancellationToken ct)
        => Guard(async () =>
        {
            var members = await Artifacts.ListAsync(prefix, ct);
            if (members.Count == 0) throw new DaxterException($"No artifacts under prefix: {prefix}");
            var estimatedBytes = members.Sum(m => m.Bytes);
            // Cheap pre-check — if the uncompressed total is already over budget, don't bother
            // zipping; redirect the agent to the URL endpoint. Compression might shrink it but we
            // can't know without zipping, and we don't want to do that work just to throw it away.
            if (estimatedBytes > InlineBytesThreshold)
            {
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    prefix,
                    member_count = members.Count,
                    uncompressed_bytes = estimatedBytes,
                    download_url = $"/api/artifacts/{Uri.EscapeDataString(prefix).Replace("%2F", "/")}?bundle=1",
                    inline = false,
                    note = "Bundle estimated > 10 MB — fetch via the URL (it streams the zip without buffering).",
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }

            await using var bundle = await Artifacts.OpenBundleAsync(prefix, ct);
            using var ms = new MemoryStream();
            await bundle.CopyToAsync(ms, ct);
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                prefix,
                member_count = members.Count,
                bytes = ms.Length,
                inline = true,
                content_base64 = Convert.ToBase64String(ms.ToArray()),
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        });

    /// <summary>Metadata for one artifact key (size, created, TTL, source tool). Cheap; returns
    /// JSON the agent can drop into a report or use to decide whether to fetch.</summary>
    public static Task<string> ArtifactMetaAsync(string key, CancellationToken ct)
        => Guard(async () =>
        {
            var meta = await Artifacts.GetMetaAsync(key, ct);
            if (meta is null) return $"Not found: {key}";
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                key = meta.Key,
                bytes = meta.Bytes,
                created_utc = meta.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                expires_utc = meta.ExpiresAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                source_tool = meta.SourceTool,
            }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
        });

    /// <summary>Delete one artifact or a prefix (recursive). Returns the count removed. NOT
    /// gated on Allow-writes — this is the user's own local data, not a Power BI mutation. The
    /// tool description warns the agent to confirm with the user first.</summary>
    public static Task<string> ArtifactDeleteAsync(string keyPrefix, CancellationToken ct)
        => Guard(async () =>
        {
            // Show what'll be deleted before doing it — the agent can echo this back to the user
            // and confirm. Since we can't pause for confirmation in a single tool call, this is
            // the agent's responsibility; we just provide the receipt.
            var hits = await Artifacts.ListAsync(keyPrefix, ct);
            if (hits.Count == 0) return $"Nothing matches '{keyPrefix}'.";
            var removed = await Artifacts.DeleteAsync(keyPrefix, ct);
            return $"Removed {removed} artifact(s) ({hits.Sum(h => h.Bytes):N0} bytes) under '{keyPrefix}'.";
        });

    /// <summary>Put bytes into the store. Accepts EITHER inline base64 content (small payloads,
    /// avoids a second HTTP round-trip) OR a URL the daemon fetches (large payloads, agent
    /// uploads to HTTPS and just passes the URL). Source-tool stamp + optional TTL flow through
    /// to the metadata index. Quota refusal becomes a typed error the agent can show as a
    /// "delete some artifacts first" hint.</summary>
    public static Task<string> ArtifactPutAsync(
        string key, string? contentBase64, string? fetchUrl, double? ttlHours, string? sourceTool,
        CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(key))
                throw new DaxterException("key is required (forward-slash path, e.g. 'reports/fixed/page.json').");
            // Exactly one of content / url must be set — otherwise the tool's behaviour is
            // ambiguous. Empty body would mean "create a 0-byte file" which is rarely useful.
            var hasContent = !string.IsNullOrEmpty(contentBase64);
            var hasUrl = !string.IsNullOrEmpty(fetchUrl);
            if (hasContent == hasUrl)
                throw new DaxterException("Pass exactly one of contentBase64 (inline) OR fetchUrl (daemon fetches).");

            var meta = new ArtifactMeta(
                ExpiresAt: ttlHours is { } h && h > 0 ? DateTime.UtcNow.AddHours(h) : null,
                SourceTool: sourceTool ?? "daxter_artifact_put");

            try
            {
                ArtifactRef aref;
                if (hasContent)
                {
                    var bytes = Convert.FromBase64String(contentBase64!);
                    aref = await Artifacts.PutAsync(key, new MemoryStream(bytes), meta, ct);
                }
                else
                {
                    // Url-fetch path — for payloads too big to drop into MCP messages. Note
                    // that this is the DAEMON's outbound fetch, not the agent's; the URL must
                    // be reachable from inside the DAXter container.
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    await using var stream = await http.GetStreamAsync(fetchUrl, ct);
                    aref = await Artifacts.PutAsync(key, stream, meta, ct);
                }
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    key = aref.Key,
                    bytes = aref.Bytes,
                    created_utc = aref.CreatedAt.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    expires_utc = aref.ExpiresAt?.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    source_tool = aref.SourceTool,
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch (ArtifactQuotaExceededException ex)
            {
                return $"REFUSED — {ex.Message} Run daxter_artifact_list to see what's eating the budget; daxter_artifact_delete to free space.";
            }
            catch (InvalidArtifactKeyException ex)
            {
                throw new DaxterException(ex.Message);
            }
            catch (FormatException)
            {
                throw new DaxterException("contentBase64 is not valid base64.");
            }
        });

    /// <summary>Unzip a zip payload into the store under a key prefix. Accepts inline base64
    /// (small archives) OR a URL (large archives). Returns the count + per-entry summary so the
    /// agent can confirm what landed. Same TTL + source-tool plumbing as Put.</summary>
    public static Task<string> ArtifactExtractAsync(
        string keyPrefix, string? zipBase64, string? fetchUrl, double? ttlHours, string? sourceTool,
        CancellationToken ct)
        => Guard(async () =>
        {
            if (string.IsNullOrWhiteSpace(keyPrefix))
                throw new DaxterException("keyPrefix is required (e.g. 'alignment/sales-dashboard-fixed').");
            var hasContent = !string.IsNullOrEmpty(zipBase64);
            var hasUrl = !string.IsNullOrEmpty(fetchUrl);
            if (hasContent == hasUrl)
                throw new DaxterException("Pass exactly one of zipBase64 (inline) OR fetchUrl (daemon fetches).");

            var meta = new ArtifactMeta(
                ExpiresAt: ttlHours is { } h && h > 0 ? DateTime.UtcNow.AddHours(h) : null,
                SourceTool: sourceTool ?? "daxter_artifact_extract");
            try
            {
                IReadOnlyList<ArtifactRef> written;
                if (hasContent)
                {
                    var bytes = Convert.FromBase64String(zipBase64!);
                    written = await Artifacts.ExtractAsync(keyPrefix, new MemoryStream(bytes), meta, ct);
                }
                else
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
                    await using var stream = await http.GetStreamAsync(fetchUrl, ct);
                    written = await Artifacts.ExtractAsync(keyPrefix, stream, meta, ct);
                }
                return System.Text.Json.JsonSerializer.Serialize(new
                {
                    prefix = keyPrefix,
                    extracted = written.Count,
                    total_bytes = written.Sum(w => w.Bytes),
                    keys = written.Select(w => w.Key),
                }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
            }
            catch (ArtifactQuotaExceededException ex)
            {
                return $"REFUSED — {ex.Message} Run daxter_artifact_list to see what's eating the budget; daxter_artifact_delete to free space.";
            }
            catch (InvalidArtifactKeyException ex)
            {
                throw new DaxterException(ex.Message);
            }
            catch (FormatException)
            {
                throw new DaxterException("zipBase64 is not valid base64.");
            }
        });
}
