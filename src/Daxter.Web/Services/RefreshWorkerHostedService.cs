using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Connection;
using Daxter.Core.Maintenance;
using Daxter.Core.Scheduling;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Daxter.Web.Services;

/// <summary>
/// The single shared refresh worker, hosted by the long-running web container. Runs the Core
/// <see cref="RefreshScheduler"/> against the shared <c>~/.daxter</c> queue: it drains jobs enqueued
/// by any interface (CLI, MCP, UI), executes each over XMLA with ordered per-partition processing
/// and configurable retries, and serializes them <b>one refresh per model at a time</b> (different
/// models run concurrently). This is the one place refreshes execute — so per-model serialization,
/// ordering, retries and job status are identical no matter which interface started the refresh.
/// </summary>
public sealed class RefreshWorkerHostedService : BackgroundService
{
    private readonly RefreshQueueStore _store;
    private readonly ConfigState _state;
    private readonly JobHistoryStore _history;
    private readonly JobService _jobs;
    private readonly ILogger<RefreshWorkerHostedService> _log;

    public RefreshWorkerHostedService(
        RefreshQueueStore store, ConfigState state, JobHistoryStore history,
        JobService jobs, ILogger<RefreshWorkerHostedService> log)
    {
        _store = store;
        _state = state;
        _history = history;
        _jobs = jobs;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var scheduler = new RefreshScheduler(
            _store,
            executor: ExecuteRefreshAsync,
            workerId: $"web-{Guid.NewGuid():N}",
            maxConcurrentModels: 4,
            pollInterval: TimeSpan.FromSeconds(2),
            onChanged: _ => _jobs.RaiseChanged());

        _log.LogInformation("Refresh worker {Id} started (draining the shared queue)", scheduler.WorkerId);
        try
        {
            await scheduler.RunAsync(stoppingToken);
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (Exception ex)
        {
            _log.LogError("Refresh worker crashed: {Error}", ex.Message);
            throw;
        }
    }

    /// <summary>Executes one job over XMLA. Lifted from the old in-process worker; now the single
    /// execution path for every interface. Honours cooperative cancel (aborts the live session) and
    /// per-operation retries.</summary>
    private async Task ExecuteRefreshAsync(RefreshJob job, IRefreshProgress progress, CancellationToken ct)
    {
        var spec = job.Spec;
        var cfg = _state.ToConfig(spec.Workspace, spec.Dataset);
        ITokenProvider provider = new MsalTokenProvider(cfg, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: false);
        var factory = new AdomdXmlaSessionFactory(cfg, provider);

        progress.Event("Connecting to the model…");
        var session = await factory.CreateAsync();

        // Watch for a cross-process cancel request and abort the live session (stops a running command).
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    if (progress.CancelRequested)
                    {
                        try { session.Dispose(); } catch { /* aborting the running command */ }
                        linked.Cancel();
                        break;
                    }
                    await Task.Delay(1000, linked.Token);
                }
            }
            catch (OperationCanceledException) { /* shutting down the watcher */ }
        });

        try
        {
            var maint = new MaintenanceService(session, spec.Dataset);
            var retries = Math.Max(0, spec.Retries);

            void Run(string tmsl) => RetryPolicy.Execute(
                () => maint.Execute(tmsl), retries,
                onRetry: (n, max, ex) => progress.Event($"transient failure — retry {n}/{max}: {ex.Message}"));

            if (spec.Kind is RefreshKind.AllPartitions or RefreshKind.SomePartitions)
            {
                // One single-partition command each, executed sequentially in the requested order —
                // the engine ignores order *within* a TMSL request, so client-driven sequencing is
                // the only way to honour newest→oldest / oldest→newest.
                var parts = spec.Kind == RefreshKind.AllPartitions
                    ? maint.OrderedPartitionNames(spec.Table!, spec.Order)
                    : spec.Partitions ?? Array.Empty<string>();

                progress.Partitions(0, parts.Count);
                progress.Event($"Refreshing {parts.Count} partition(s) of '{spec.Table}', one at a time");

                for (var i = 0; i < parts.Count; i++)
                {
                    if (progress.CancelRequested) throw new OperationCanceledException();

                    var name = parts[i];
                    progress.Event($"[{i + 1}/{parts.Count}] refreshing {name}…");
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    Run(maint.BuildPartitionRefresh(spec.Table!, name, spec.Type));
                    sw.Stop();
                    progress.Partitions(i + 1, parts.Count);
                    progress.Event($"[{i + 1}/{parts.Count}] ✓ {name} — {(int)sw.Elapsed.TotalSeconds}s");
                }
            }
            else
            {
                var tmsl = spec.Kind switch
                {
                    RefreshKind.Model => maint.BuildModelRefresh(spec.Type),
                    RefreshKind.Table => maint.BuildTableRefresh(spec.Table!, spec.Type),
                    RefreshKind.Partition => maint.BuildPartitionRefresh(spec.Table!, spec.Partition!, spec.Type),
                    _ => throw new DaxterException("Unknown refresh kind."),
                };

                progress.Event("Processing (TMSL refresh)…");
                Run(tmsl);
            }

            // Record the duration so future estimates improve (success only).
            if (job.Started is { } started)
                _history.Record(JobService.Sig(spec), (DateTimeOffset.Now - started).TotalSeconds);
        }
        finally
        {
            linked.Cancel();
            try { await watcher; } catch { /* watcher already done */ }
            try { session.Dispose(); } catch { /* may already be disposed by the watcher */ }
        }
    }
}
