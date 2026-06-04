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
        IXmlaSessionFactory factory = new AdomdXmlaSessionFactory(cfg, provider);
        var retries = Math.Max(0, spec.Retries);

        // A cross-process cancel request cancels this token; the live session is registered on it so a
        // cancel disposes the running session and aborts the in-flight command.
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watcher = Task.Run(async () =>
        {
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    if (progress.CancelRequested) { linked.Cancel(); break; }
                    await Task.Delay(1000, linked.Token);
                }
            }
            catch (OperationCanceledException) { /* shutting down the watcher */ }
        });

        try
        {
            if (spec.Kind is RefreshKind.AllPartitions or RefreshKind.SomePartitions)
            {
                // Refresh each partition on its OWN fresh session so a long serial refresh re-acquires
                // the access token before every partition (one token, captured at connect time, expires
                // mid-run on big tables — see SerialPartitionRefresh). AllPartitions needs one session
                // up front to enumerate the partition names.
                progress.Event("Connecting to the model…");
                IReadOnlyList<string> parts;
                if (spec.Kind == RefreshKind.AllPartitions)
                {
                    using var listing = await factory.CreateAsync(linked.Token);
                    parts = new MaintenanceService(listing, spec.Dataset).OrderedPartitionNames(spec.Table!, spec.Order);
                }
                else
                {
                    parts = spec.Partitions ?? Array.Empty<string>();
                }

                await SerialPartitionRefresh.RunAsync(
                    spec.Table!, parts, spec.Dataset,
                    buildRefresh: (m, name) => m.BuildPartitionRefresh(spec.Table!, name, spec.Type),
                    openSession: tok => factory.CreateAsync(tok),
                    retries, progress, linked.Token);
            }
            else
            {
                progress.Event("Connecting to the model…");
                using var session = await factory.CreateAsync(linked.Token);
                using var reg = linked.Token.Register(() => { try { session.Dispose(); } catch { /* aborting the running command */ } });

                var maint = new MaintenanceService(session, spec.Dataset);
                var tmsl = spec.Kind switch
                {
                    RefreshKind.Model => maint.BuildModelRefresh(spec.Type),
                    RefreshKind.Table => maint.BuildTableRefresh(spec.Table!, spec.Type),
                    RefreshKind.Partition => maint.BuildPartitionRefresh(spec.Table!, spec.Partition!, spec.Type),
                    _ => throw new DaxterException("Unknown refresh kind."),
                };

                progress.Event("Processing (TMSL refresh)…");
                RetryPolicy.Execute(() => maint.Execute(tmsl), retries,
                    onRetry: (n, max, ex) => progress.Event($"transient failure — retry {n}/{max}: {ex.Message}"));
            }

            // Record the duration so future estimates improve (success only).
            if (job.Started is { } started)
                _history.Record(JobService.Sig(spec), (DateTimeOffset.Now - started).TotalSeconds);
        }
        finally
        {
            linked.Cancel();
            try { await watcher; } catch { /* watcher already done */ }
        }
    }
}
