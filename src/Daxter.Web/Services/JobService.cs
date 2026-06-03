using Daxter.Core;
using Daxter.Core.Maintenance;
using Daxter.Core.Scheduling;
using Microsoft.Extensions.Logging;

namespace Daxter.Web.Services;

/// <summary>
/// Web façade over the shared <see cref="RefreshQueueStore"/>. Enqueues refreshes — enforcing the
/// write gate exactly like the MCP server (only when <see cref="ConfigState.AllowWrites"/> is on,
/// and never for PROD-looking targets) — and exposes the queue to the Jobs/Refresh pages. The
/// refreshes themselves execute in <see cref="RefreshWorkerHostedService"/>, the single worker this
/// web container hosts and that every interface's refreshes (CLI, MCP, UI) route through. Because
/// the queue lives on the shared <c>~/.daxter</c> volume, the Jobs page shows jobs enqueued by the
/// CLI and MCP server too.
/// </summary>
public sealed class JobService
{
    private readonly ConfigState _state;
    private readonly ILogger<JobService> _log;
    private readonly JobHistoryStore _history;
    private readonly RefreshQueueStore _store;

    /// <summary>Raised when the queue changes (enqueue/cancel/remove or a worker status transition).</summary>
    public event Action? Changed;

    /// <summary>Lets the hosted worker notify the live Jobs/Refresh pages of a status change.</summary>
    internal void RaiseChanged() => Changed?.Invoke();

    public JobService(ConfigState state, ILogger<JobService> log, JobHistoryStore history, RefreshQueueStore store)
    {
        _state = state;
        _log = log;
        _history = history;
        _store = store;
    }

    /// <summary>History signature: same kind+model+table+type pool together (partition name ignored).</summary>
    public static string Sig(RefreshSpec s) => $"{s.Kind}|{s.Workspace}|{s.Dataset}|{s.Table}|{s.Type}";

    /// <summary>Estimated duration (seconds) for a spec, from past runs; null if none yet.</summary>
    public double? EstimateSeconds(RefreshSpec spec) => _history.EstimateSeconds(Sig(spec));

    /// <summary>A copy of a job's activity log.</summary>
    public IReadOnlyList<JobEvent> EventsOf(int id) => _store.Get(id)?.Events ?? new List<JobEvent>();

    /// <summary>Validates the write gate and enqueues a refresh. Throws if writes are off or the
    /// target is production.</summary>
    public RefreshJob Enqueue(RefreshSpec spec)
    {
        if (!_state.AllowWrites)
            throw new DaxterException(
                "Writes are disabled. Turn on \"Allow writes\" on the Configure page to run refreshes.");

        if (_state.ToConfig(spec.Workspace, spec.Dataset).IsProductionTarget())
            throw new DaxterException($"Refusing to refresh a production target ('{spec.Workspace}').");

        var job = _store.Enqueue(spec, RefreshTitle.For(spec), JobOrigin.Web, _history.EstimateSeconds(Sig(spec)));
        _log.LogInformation("Queued job #{Id}: {Title}", job.Id, job.Title);
        RaiseChanged();
        return job;
    }

    /// <summary>All jobs, newest first.</summary>
    public IReadOnlyList<RefreshJob> Snapshot() => _store.All();

    /// <summary>Jobs for one model, newest first (for the Refresh page's tracker).</summary>
    public IReadOnlyList<RefreshJob> For(string workspace, string dataset) => _store.For(workspace, dataset);

    public int ActiveCount => _store.All().Count(j => j.IsActive);

    /// <summary>Whether a worker is currently draining the shared queue: "alive" | "stale" | "none",
    /// plus the age of the last heartbeat. Lets the UI show that queued jobs will actually run.</summary>
    public (string State, TimeSpan? Age) WorkerStatus()
    {
        var age = _store.HeartbeatAge();
        var state = age is null ? "none" : age > TimeSpan.FromSeconds(30) ? "stale" : "alive";
        return (state, age);
    }

    /// <summary>True if a refresh is queued or running for this model.</summary>
    public bool IsRefreshing(string workspace, string dataset) => _store.IsRefreshing(workspace, dataset);

    /// <summary>Cancels a job (queued → canceled; running → flagged for the worker to abort).</summary>
    public void Cancel(int id)
    {
        _store.Cancel(id);
        _log.LogInformation("Cancel requested for job #{Id}", id);
        RaiseChanged();
    }

    /// <summary>Removes all finished jobs from the list.</summary>
    public void ClearFinished()
    {
        _store.RemoveFinished();
        RaiseChanged();
    }

    /// <summary>Removes one finished job (not a queued/running one).</summary>
    public void Remove(int id)
    {
        _store.Remove(id);
        RaiseChanged();
    }

    /// <summary>Re-runs a finished/interrupted job by enqueuing the same spec (re-validates gates).</summary>
    public RefreshJob? Resume(int id)
    {
        var spec = _store.Get(id)?.Spec;
        return spec is null ? null : Enqueue(spec);
    }
}
