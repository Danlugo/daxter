using System.Text.Json;
using Daxter.Core.Maintenance;
using Daxter.Core.Rest;

namespace Daxter.Core.Scheduling;

/// <summary>
/// Runs a refresh via the Power BI <b>Enhanced Refresh REST API</b> — server-managed and asynchronous.
/// DAXter submits the refresh (with <c>objects</c> for table/partition targeting, <c>maxParallelism</c>,
/// <c>retryCount</c>, and <c>commitMode=partialBatch</c> for partition jobs) and then just <b>polls</b>
/// the status; it holds no long-lived XMLA connection, so the refresh can't hang or drop on the client
/// the way the serial XMLA path can. Per-object (per-partition) status drives the same job progress, and
/// the completed-partition set is recorded so a resume re-runs only the partitions that didn't complete.
/// </summary>
public static class EnhancedRefresh
{
    /// <summary>Env override for the per-refresh partition parallelism the service uses (default 4,
    /// clamped 1–10). Lower than the API's default of 10 to be gentle on the capacity.</summary>
    public const string MaxParallelismEnv = "DAXTER_REFRESH_MAX_PARALLELISM";

    public static int MaxParallelism()
        => int.TryParse(Environment.GetEnvironmentVariable(MaxParallelismEnv)?.Trim(), out var n)
            ? Math.Clamp(n, 1, 10)
            : 4;

    /// <summary>Submits the refresh and polls to completion, mapping per-object status to
    /// <paramref name="progress"/>. Throws on failure (with the failed message) or cancellation.</summary>
    public static async Task RunAsync(PowerBiRestClient rest, string groupId, string datasetId,
        RefreshSpec spec, int maxParallelism, IRefreshProgress progress, CancellationToken ct)
    {
        var body = BuildBody(spec, maxParallelism);
        progress.Event("Submitting refresh to the service (enhanced API — runs server-side)…");
        var requestId = await rest.StartEnhancedRefreshAsync(groupId, datasetId, body, ct).ConfigureAwait(false);
        progress.Event($"Queued on the service (requestId {requestId}); polling status…");

        var planned = false;
        while (true)
        {
            if (progress.CancelRequested || ct.IsCancellationRequested)
            {
                try { await rest.CancelEnhancedRefreshAsync(groupId, datasetId, requestId, CancellationToken.None).ConfigureAwait(false); }
                catch { /* best-effort cancel */ }
                throw new OperationCanceledException();
            }

            await Task.Delay(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
            var st = await rest.GetEnhancedRefreshAsync(groupId, datasetId, requestId, ct).ConfigureAwait(false);

            if (st.Objects.Count > 0)
            {
                string Name(RefreshObjectStatus o) => o.Partition.Length > 0 ? o.Partition : o.Table;
                if (!planned) { progress.Plan(st.Objects.Select(Name).ToList()); planned = true; }

                var done = st.Objects.Where(o => o.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)).ToList();
                progress.Partitions(done.Count, st.Objects.Count);
                progress.Completed(done.Select(Name).ToList());   // exact completed set → correct resume

                var current = st.Objects.FirstOrDefault(o => o.Status.Equals("InProgress", StringComparison.OrdinalIgnoreCase));
                progress.Event(current is not null
                    ? $"[{done.Count}/{st.Objects.Count}] refreshing {Name(current)}…"
                    : $"[{done.Count}/{st.Objects.Count}] {st.Status}");
            }
            else
            {
                progress.Event($"status: {st.Status}");
            }

            if (st.Status.Equals("Completed", StringComparison.OrdinalIgnoreCase)) { progress.Event("✓ refresh completed (service)"); return; }
            if (st.Status.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                throw new DaxterException($"Enhanced refresh failed: {st.Error ?? "see the model's refresh history in the Service"}");
            if (st.Status.Equals("Cancelled", StringComparison.OrdinalIgnoreCase)) throw new OperationCanceledException();
            // NotStarted / InProgress / Unknown → keep polling
        }
    }

    /// <summary>Builds the enhanced-refresh request body for a spec. Partition jobs use
    /// <c>partialBatch</c> + <c>applyRefreshPolicy=false</c> (refresh existing partitions, committing each
    /// so a mid-run failure keeps completed work); whole-model/table use <c>transactional</c>.</summary>
    public static string BuildBody(RefreshSpec spec, int maxParallelism)
    {
        var partitioned = spec.Kind is RefreshKind.Partition or RefreshKind.SomePartitions or RefreshKind.AllPartitions;

        List<object>? objects = null;
        switch (spec.Kind)
        {
            case RefreshKind.Table:
                objects = new() { new { table = spec.Table } }; break;
            case RefreshKind.Partition:
                objects = new() { new { table = spec.Table, partition = spec.Partition } }; break;
            case RefreshKind.AllPartitions:
                objects = new() { new { table = spec.Table } }; break;   // server expands to all partitions
            case RefreshKind.SomePartitions:
                objects = (spec.Partitions ?? new List<string>())
                    .Select(p => (object)new { table = spec.Table, partition = p }).ToList();
                break;
            // Model → null (whole model)
        }

        var payload = new Dictionary<string, object?>
        {
            ["type"] = TypeString(spec.Type),
            ["commitMode"] = partitioned ? "partialBatch" : "transactional",
            ["maxParallelism"] = Math.Clamp(maxParallelism, 1, 10),
            ["retryCount"] = Math.Max(1, spec.Retries),
            ["timeout"] = "05:00:00",
        };

        // applyRefreshPolicy semantics (Power BI Enhanced Refresh API), v1.39.0:
        //  - Partition-targeted jobs MUST set it to false (combined with commitMode=
        //    partialBatch). We're refreshing already-existing partitions and the policy walk
        //    would conflict. Original v1.x behaviour preserved.
        //  - Model + table-level jobs CAN set it to true — the "Apply refresh policy"
        //    operation Tabular Editor invokes after a new-environment deploy. Two variants:
        //
        //      A. spec.ApplyPolicy=true, spec.PolicyTables=null  (Option A, bundled flag)
        //         → emit applyRefreshPolicy=true with NO objects list.
        //         Power BI runs a normal refresh on every table; ALSO walks the refresh
        //         policy on tables that have one. Non-policy tables get a regular refresh as
        //         part of the model job. Used by `daxter refresh model --apply-policy`.
        //
        //      B. spec.ApplyPolicy=true, spec.PolicyTables=[T1, T2, ...]  (Option B, surgical)
        //         → emit objects list with ONLY those tables, applyRefreshPolicy=true.
        //         Non-policy tables UNTOUCHED. Mirrors Tabular Editor's per-table semantics.
        //         Used by the dedicated `daxter refresh apply-policy` / `daxter_apply_refresh_policy`.
        //
        //  - Omitting the field (the default for model/table jobs in v1.x) is equivalent to
        //    false on the service side; we keep that legacy shape when ApplyPolicy is off,
        //    so nothing in the wire-shape changes for any existing v1.x callers.
        if (partitioned)
        {
            payload["applyRefreshPolicy"] = false;
        }
        else if (spec.ApplyPolicy)
        {
            payload["applyRefreshPolicy"] = true;

            // Option B variant — when PolicyTables is provided, REPLACE any inferred objects
            // list with the explicit policy-tables list. This is the scoped "only touch
            // policy tables" path; non-policy tables are not in the objects list, so the
            // service doesn't refresh them.
            if (spec.PolicyTables is { Count: > 0 } policyTables)
            {
                objects = policyTables.Select(t => (object)new { table = t }).ToList();
            }
            // The API documents an `effectiveDate` for testing policies with a fixed "now".
            // We don't surface that knob today — Power BI defaults to UtcNow which matches
            // what every Tabular-Editor user expects.
        }
        if (objects is not null) payload["objects"] = objects;

        return JsonSerializer.Serialize(payload);
    }

    private static string TypeString(RefreshType t) => t switch
    {
        RefreshType.Full => "full",
        RefreshType.ClearValues => "clearValues",
        RefreshType.Calculate => "calculate",
        RefreshType.DataOnly => "dataOnly",
        RefreshType.Automatic => "automatic",
        _ => "full",
    };
}
