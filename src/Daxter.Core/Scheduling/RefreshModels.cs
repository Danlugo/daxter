using System.Text.Json.Serialization;
using Daxter.Core.Maintenance;

namespace Daxter.Core.Scheduling;

/// <summary>What a refresh job targets.</summary>
public enum RefreshKind { Model, Table, Partition, AllPartitions, SomePartitions }

/// <summary>Lifecycle of a queued refresh.</summary>
public enum JobStatus { Queued, Running, Succeeded, Failed, Canceled, Interrupted }

/// <summary>Which interface enqueued the job — for display/audit on the unified Jobs view.</summary>
public enum JobOrigin { Web, Cli, Mcp }

/// <summary>
/// What a refresh targets. Table/Partition are null where not applicable. This is the unit every
/// interface (CLI, MCP, web) submits to the shared queue. <see cref="Retries"/> is honoured by the
/// worker (transient-failure re-attempts with backoff).
/// </summary>
public sealed record RefreshSpec(
    RefreshKind Kind,
    string Workspace,
    string Dataset,
    string? Table,
    string? Partition,
    PartitionOrder Order,
    RefreshType Type = RefreshType.Full,
    IReadOnlyList<string>? Partitions = null,
    int Retries = 0,
    /// <summary>v1.39.0 — when true, Power BI walks the targeted table's refresh policy and
    /// materialises the policy-defined partitions (e.g. rolling-window archive + hot range)
    /// instead of refreshing the partitions that already exist. The exact operation Tabular
    /// Editor's "Apply refresh policy" right-click invokes. Required after deploying a model
    /// to a new environment where the policy is defined but no partitions have been created
    /// from it yet. Partition-level refreshes (Partition / SomePartitions / AllPartitions) are
    /// INCOMPATIBLE — BuildBody refuses to emit applyRefreshPolicy=true for those kinds. See
    /// also <see cref="PolicyTables"/> for the surgical "only touch policy tables" scoping.</summary>
    bool ApplyPolicy = false,
    /// <summary>v1.39.0 — explicit list of tables to apply the refresh policy to. When set
    /// (and <see cref="ApplyPolicy"/> is true), <see cref="EnhancedRefresh.BuildBody"/> emits
    /// an objects list scoped to ONLY these tables — non-policy tables are untouched.
    /// Mirrors Tabular Editor's per-table semantics. When null and <see cref="ApplyPolicy"/>
    /// is true, the API call is unscoped (whole-model refresh; Power BI applies the policy
    /// only on tables that have one but still does a normal refresh on the rest). The
    /// CLI/MCP orchestrators populate this via XMLA TOM enumeration upstream, so the worker
    /// never has to guess.</summary>
    IReadOnlyList<string>? PolicyTables = null);

/// <summary>One timestamped step in a job's activity log.</summary>
public sealed record JobEvent(DateTimeOffset Time, string Message);

/// <summary>
/// A queued/running/finished refresh on the shared queue. Persisted to <c>~/.daxter/queue.json</c>
/// so every DAXter process sees the same jobs. Plain mutable POCO (no runtime-only handles) so it
/// round-trips through JSON across processes; per-job cancellation is cooperative via
/// <see cref="CancelRequested"/>, which any interface can set and the worker polls.
/// </summary>
public sealed class RefreshJob
{
    public int Id { get; set; }
    public string Title { get; set; } = "";
    public RefreshSpec Spec { get; set; } = null!;
    public JobOrigin Origin { get; set; } = JobOrigin.Web;
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset Created { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public string? Error { get; set; }

    /// <summary>Timestamped activity log (Queued → Started → Connecting → Executing → Done/…).</summary>
    public List<JobEvent> Events { get; set; } = new();

    /// <summary>The latest activity message (what the job is doing now).</summary>
    public string? Step { get; set; }

    /// <summary>Estimated total seconds from history (set at enqueue); null if no history yet.</summary>
    public double? EstimateSeconds { get; set; }

    /// <summary>For multi-partition jobs: total partitions and how many have completed.</summary>
    public int? PartitionTotal { get; set; }
    public int? PartitionDone { get; set; }

    /// <summary>The ordered partition list the worker is processing (captured at start). Lets a
    /// resume re-run only the not-yet-done partitions (<see cref="PartitionDone"/> onward) instead of
    /// the whole table. Null for non-partition refreshes.</summary>
    public List<string>? OrderedPartitions { get; set; }

    /// <summary>The EXACT partitions completed (by name). Set by the enhanced-refresh engine, where
    /// partitions can finish out of order — so a resume re-runs <c>OrderedPartitions \ DonePartitions</c>,
    /// not just "skip the first N". Null for the serial path (which completes strictly in order).</summary>
    public List<string>? DonePartitions { get; set; }

    /// <summary>Cross-process cooperative cancel: a surface sets this; the worker polls and aborts.</summary>
    public bool CancelRequested { get; set; }

    /// <summary>The worker that claimed this job (heartbeat owner id); null while queued.</summary>
    public string? WorkerId { get; set; }

    /// <summary>Model identity used for per-model serialization (case/space-insensitive).</summary>
    [JsonIgnore]
    public string ModelKey =>
        $"{Spec.Workspace?.Trim()}{Spec.Dataset?.Trim()}".ToLowerInvariant();

    [JsonIgnore] public bool IsActive => Status is JobStatus.Queued or JobStatus.Running;
    [JsonIgnore] public bool CanCancel => IsActive;
    [JsonIgnore] public TimeSpan? Duration => Started is { } s ? (Finished ?? DateTimeOffset.Now) - s : null;

    /// <summary>0..1 progress while running — by completed partitions when known, else elapsed vs estimate.</summary>
    [JsonIgnore]
    public double? Progress
    {
        get
        {
            if (Status != JobStatus.Running) return null;
            if (PartitionTotal is { } total && total > 0 && PartitionDone is { } done)
                return Math.Clamp((double)done / total, 0, 1);
            if (EstimateSeconds is { } est && est > 0 && Duration is { } d)
                return Math.Clamp(d.TotalSeconds / est, 0, 0.99);
            return null;
        }
    }
}
