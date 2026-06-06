using System.Diagnostics;
using Daxter.Core.Connection;
using Daxter.Core.Maintenance;

namespace Daxter.Core.Scheduling;

/// <summary>
/// Refreshes a table's partitions one at a time, each on a <b>freshly opened</b> XMLA session so the
/// worker re-acquires the access token before every partition — and <b>re-opens the session on retry</b>
/// so a dropped connection recovers too.
///
/// <para>A long serial refresh (e.g. 67 partitions) can run far longer than a single OAuth access
/// token's lifetime. Reusing one session — the token captured when the connection opened — for the
/// whole job made big refreshes fail mid-run with <c>"Refreshing the expired access-token has failed"</c>
/// once that token expired (observed ~21 min in, partition 41/67). Opening a new session per partition
/// is a cheap, silent token-cache acquire and lets the job outlive any single token's lifetime.</para>
///
/// <para>The other long-run failure is the connection <i>dropping</i> mid-command
/// (<c>"The connection is not open"</c>) — typically capacity/XMLA-endpoint pressure under concurrent
/// refreshes. Retrying on the SAME (dead) session can't recover, so each retry here <b>re-opens a fresh
/// session</b> (fresh connection + token). Partition refreshes also retry by default
/// (<see cref="MinRetries"/>) since each attempt is a cheap re-open and transient drops are common on
/// long runs.</para>
///
/// <para>Cancellation (<see cref="IRefreshProgress.CancelRequested"/> or <paramref name="ct"/>) is
/// honoured between partitions, and the in-flight session is registered on <paramref name="ct"/> so a
/// cancel disposes it and aborts the running command.</para>
/// </summary>
public static class SerialPartitionRefresh
{
    /// <summary>Minimum per-partition attempts beyond the first — so a transient token/connection drop
    /// recovers even when the caller didn't ask for retries. Each retry re-opens a fresh session.</summary>
    public const int MinRetries = 2;

    /// <param name="buildRefresh">Builds the single-partition TMSL — <c>(maint, partitionName) =&gt; tmsl</c>.
    /// Pure; the per-partition <see cref="MaintenanceService"/> is supplied for convenience.</param>
    /// <param name="openSession">Opens a NEW session (and therefore acquires a fresh token). Called once per partition.</param>
    public static async Task RunAsync(
        string table,
        IReadOnlyList<string> partitions,
        string database,
        Func<MaintenanceService, string, string> buildRefresh,
        Func<CancellationToken, Task<IXmlaSession>> openSession,
        int retries,
        IRefreshProgress progress,
        CancellationToken ct)
    {
        if (partitions is null || partitions.Count == 0)
            throw new DaxterException($"No partitions to refresh for table: {table}");

        // At least MinRetries extra attempts — each re-opens a fresh session, so transient token/connection
        // drops on a long run recover even when the caller passed retries=0.
        var attempts = Math.Max(Math.Max(0, retries), MinRetries);
        progress.Partitions(0, partitions.Count);
        progress.Event($"Refreshing {partitions.Count} partition(s) of '{table}', one at a time (fresh session each, retry {attempts}×)");

        for (var i = 0; i < partitions.Count; i++)
        {
            if (progress.CancelRequested || ct.IsCancellationRequested)
                throw new OperationCanceledException();

            var name = partitions[i];
            progress.Event($"[{i + 1}/{partitions.Count}] refreshing {name}…");
            var sw = Stopwatch.StartNew();

            // Each attempt opens a NEW session => fresh access token AND a fresh connection. So a retry
            // recovers from an expired token OR a dropped connection ("The connection is not open").
            await RetryPolicy.ExecuteAsync(async () =>
            {
                var session = await openSession(ct).ConfigureAwait(false);
                try
                {
                    using (ct.Register(() => { try { session.Dispose(); } catch { /* aborting the running command */ } }))
                    {
                        var maint = new MaintenanceService(session, database);
                        maint.Execute(buildRefresh(maint, name));
                    }
                }
                finally
                {
                    try { session.Dispose(); } catch { /* already disposed by a cancel */ }
                }
            }, attempts,
            onRetry: (n, max, ex) => progress.Event($"transient failure on {name} — retry {n}/{max} (fresh session): {ex.Message}"),
            ct).ConfigureAwait(false);

            sw.Stop();
            progress.Partitions(i + 1, partitions.Count);
            progress.Event($"[{i + 1}/{partitions.Count}] ✓ {name} — {(int)sw.Elapsed.TotalSeconds}s");
        }
    }
}
