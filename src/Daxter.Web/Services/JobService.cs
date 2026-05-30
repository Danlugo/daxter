using System.Collections.Concurrent;
using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Maintenance;
using Microsoft.Extensions.Logging;

namespace Daxter.Web.Services;

public enum RefreshKind { Model, Table, Partition, AllPartitions }
public enum JobStatus { Queued, Running, Succeeded, Failed }

/// <summary>What a refresh job targets. Table/Partition are null where not applicable.</summary>
public sealed record RefreshSpec(
    RefreshKind Kind, string Workspace, string Dataset, string? Table, string? Partition, PartitionOrder Order);

/// <summary>A queued/running/finished refresh, shown on the Jobs page.</summary>
public sealed class Job
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required RefreshSpec Spec { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset Created { get; } = DateTimeOffset.Now;
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public string? Error { get; set; }

    public TimeSpan? Duration => Started is { } s ? (Finished ?? DateTimeOffset.Now) - s : null;
}

/// <summary>
/// Runs model/table/partition refreshes as background <see cref="Job"/>s, one at a time (a single
/// worker), so many can be queued and they execute <b>in order</b>. Refreshes are writes, so they
/// are gated exactly like the MCP server: allowed only when <see cref="ConfigState.AllowWrites"/>
/// is on, and <b>always refused</b> for PROD-looking targets. Raises <see cref="Changed"/> for the
/// live Jobs page.
/// </summary>
public sealed class JobService
{
    private readonly ConfigState _state;
    private readonly ILogger<JobService> _log;

    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();
    private readonly ConcurrentQueue<Job> _queue = new();
    private int _nextId = 1;
    private bool _workerRunning;

    public event Action? Changed;

    public JobService(ConfigState state, ILogger<JobService> log)
    {
        _state = state;
        _log = log;
    }

    /// <summary>Validates the write gate and enqueues a refresh. Throws if writes are off or the
    /// target is production.</summary>
    public Job Enqueue(RefreshSpec spec)
    {
        if (!_state.AllowWrites)
        {
            throw new DaxterException(
                "Writes are disabled. Turn on \"Allow writes\" on the Configure page to run refreshes.");
        }

        if (_state.ToConfig(spec.Workspace, spec.Dataset).IsProductionTarget())
        {
            throw new DaxterException(
                $"Refusing to refresh a production target ('{spec.Workspace}').");
        }

        Job job;
        lock (_gate)
        {
            job = new Job { Id = _nextId++, Title = TitleFor(spec), Spec = spec };
            _jobs.Add(job);
            _queue.Enqueue(job);
        }

        _log.LogInformation("Queued job #{Id}: {Title}", job.Id, job.Title);
        EnsureWorker();
        Changed?.Invoke();
        return job;
    }

    /// <summary>All jobs, newest first.</summary>
    public IReadOnlyList<Job> Snapshot()
    {
        lock (_gate) return _jobs.OrderByDescending(j => j.Id).ToList();
    }

    public int ActiveCount
    {
        get { lock (_gate) return _jobs.Count(j => j.Status is JobStatus.Queued or JobStatus.Running); }
    }

    /// <summary>Removes finished (succeeded/failed) jobs from the list.</summary>
    public void ClearFinished()
    {
        lock (_gate) _jobs.RemoveAll(j => j.Status is JobStatus.Succeeded or JobStatus.Failed);
        Changed?.Invoke();
    }

    private void EnsureWorker()
    {
        lock (_gate)
        {
            if (_workerRunning) return;
            _workerRunning = true;
        }

        _ = Task.Run(WorkerLoopAsync);
    }

    private async Task WorkerLoopAsync()
    {
        try
        {
            while (_queue.TryDequeue(out var job))
            {
                job.Status = JobStatus.Running;
                job.Started = DateTimeOffset.Now;
                _log.LogInformation("Running job #{Id}: {Title}", job.Id, job.Title);
                Changed?.Invoke();

                try
                {
                    await ExecuteAsync(job.Spec);
                    job.Status = JobStatus.Succeeded;
                    _log.LogInformation("Job #{Id} succeeded in {Ms} ms", job.Id, (long)(job.Duration?.TotalMilliseconds ?? 0));
                }
                catch (Exception ex)
                {
                    job.Status = JobStatus.Failed;
                    job.Error = ex.Message;
                    _log.LogError("Job #{Id} failed: {Error}", job.Id, ex.Message);
                }
                finally
                {
                    job.Finished = DateTimeOffset.Now;
                    Changed?.Invoke();
                }
            }
        }
        finally
        {
            lock (_gate) _workerRunning = false;
            // A job may have been enqueued between the empty-check and clearing the flag.
            if (!_queue.IsEmpty) EnsureWorker();
        }
    }

    private async Task ExecuteAsync(RefreshSpec spec)
    {
        var cfg = _state.ToConfig(spec.Workspace, spec.Dataset);
        ITokenProvider provider = new MsalTokenProvider(cfg, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: false);
        var factory = new AdomdXmlaSessionFactory(cfg, provider);
        using var session = await factory.CreateAsync();

        var maint = new MaintenanceService(session, spec.Dataset);
        var tmsl = spec.Kind switch
        {
            RefreshKind.Model => maint.BuildModelRefresh(RefreshType.Full),
            RefreshKind.Table => maint.BuildTableRefresh(spec.Table!, RefreshType.Full),
            RefreshKind.Partition => maint.BuildPartitionRefresh(spec.Table!, spec.Partition!, RefreshType.Full),
            RefreshKind.AllPartitions => maint.BuildPartitionsRefresh(spec.Table!, spec.Order, RefreshType.Full),
            _ => throw new DaxterException("Unknown refresh kind."),
        };

        maint.Execute(tmsl);
    }

    private static string TitleFor(RefreshSpec s) => s.Kind switch
    {
        RefreshKind.Model => $"Refresh model · {s.Dataset}",
        RefreshKind.Table => $"Refresh table · {s.Table}",
        RefreshKind.Partition => $"Refresh partition · {s.Table}[{s.Partition}]",
        RefreshKind.AllPartitions => $"Refresh all partitions · {s.Table} ({(s.Order == PartitionOrder.NewestFirst ? "newest→oldest" : "oldest→newest")})",
        _ => "Refresh",
    };
}
