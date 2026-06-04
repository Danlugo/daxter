using System.Diagnostics;
using Daxter.Core.Connection;
using Daxter.Core.Maintenance;

namespace Daxter.Core.Scheduling;

/// <summary>
/// Refreshes a table's partitions one at a time, each on a <b>freshly opened</b> XMLA session so the
/// worker re-acquires the access token before every partition.
///
/// <para>A long serial refresh (e.g. 67 partitions) can run far longer than a single OAuth access
/// token's lifetime. Reusing one session — the token captured when the connection opened — for the
/// whole job made big refreshes fail mid-run with <c>"Refreshing the expired access-token has failed"</c>
/// once that token expired (observed ~21 min in, partition 41/67). Opening a new session per partition
/// is a cheap, silent token-cache acquire and lets the job outlive any single token's lifetime.</para>
///
/// <para>Cancellation (<see cref="IRefreshProgress.CancelRequested"/> or <paramref name="ct"/>) is
/// honoured between partitions, and the in-flight session is registered on <paramref name="ct"/> so a
/// cancel disposes it and aborts the running command.</para>
/// </summary>
public static class SerialPartitionRefresh
{
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

        retries = Math.Max(0, retries);
        progress.Partitions(0, partitions.Count);
        progress.Event($"Refreshing {partitions.Count} partition(s) of '{table}', one at a time (fresh token each)");

        for (var i = 0; i < partitions.Count; i++)
        {
            if (progress.CancelRequested || ct.IsCancellationRequested)
                throw new OperationCanceledException();

            var name = partitions[i];
            progress.Event($"[{i + 1}/{partitions.Count}] refreshing {name}…");
            var sw = Stopwatch.StartNew();

            // Fresh session => fresh access token, so this partition never inherits an expired one.
            var session = await openSession(ct).ConfigureAwait(false);
            try
            {
                using (ct.Register(() => { try { session.Dispose(); } catch { /* aborting the running command */ } }))
                {
                    var maint = new MaintenanceService(session, database);
                    var tmsl = buildRefresh(maint, name);
                    RetryPolicy.Execute(() => maint.Execute(tmsl), retries,
                        onRetry: (n, max, ex) => progress.Event($"transient failure — retry {n}/{max}: {ex.Message}"));
                }
            }
            finally
            {
                try { session.Dispose(); } catch { /* already disposed by a cancel */ }
            }

            sw.Stop();
            progress.Partitions(i + 1, partitions.Count);
            progress.Event($"[{i + 1}/{partitions.Count}] ✓ {name} — {(int)sw.Elapsed.TotalSeconds}s");
        }
    }
}
