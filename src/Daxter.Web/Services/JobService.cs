using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Maintenance;
using Microsoft.Extensions.Logging;

namespace Daxter.Web.Services;

public enum RefreshKind { Model, Table, Partition, AllPartitions, SomePartitions }
public enum JobStatus { Queued, Running, Succeeded, Failed, Canceled, Interrupted }

/// <summary>What a refresh job targets. Table/Partition are null where not applicable.</summary>
public sealed record RefreshSpec(
    RefreshKind Kind, string Workspace, string Dataset, string? Table, string? Partition,
    PartitionOrder Order, RefreshType Type = RefreshType.Full, IReadOnlyList<string>? Partitions = null);

/// <summary>One timestamped step in a job's activity log.</summary>
public sealed record JobEvent(DateTimeOffset Time, string Message);

/// <summary>A queued/running/finished refresh, shown on the Jobs page.</summary>
public sealed class Job
{
    public required int Id { get; init; }
    public required string Title { get; init; }
    public required RefreshSpec Spec { get; init; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public DateTimeOffset Created { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? Started { get; set; }
    public DateTimeOffset? Finished { get; set; }
    public string? Error { get; set; }

    // Cancellation: the live session is aborted (disposed) to stop a running refresh. Runtime-only.
    [JsonIgnore] internal IDisposable? Session { get; set; }
    [JsonIgnore] internal bool CancelRequested { get; set; }

    /// <summary>Timestamped activity log (Queued → Started → Connecting → Executing → Done/…).</summary>
    public List<JobEvent> Events { get; } = new();

    /// <summary>The latest activity message (what the job is doing now).</summary>
    public string? Step { get; set; }

    /// <summary>Estimated total seconds from history (set at enqueue); null if no history yet.</summary>
    public double? EstimateSeconds { get; set; }

    [JsonIgnore] public bool CanCancel => Status is JobStatus.Queued or JobStatus.Running;
    [JsonIgnore] public TimeSpan? Duration => Started is { } s ? (Finished ?? DateTimeOffset.Now) - s : null;

    /// <summary>0..0.99 progress while running, from elapsed vs estimate; null if no estimate.</summary>
    [JsonIgnore] public double? Progress
    {
        get
        {
            if (Status != JobStatus.Running || EstimateSeconds is not { } est || est <= 0 || Duration is not { } d)
                return null;
            return Math.Clamp(d.TotalSeconds / est, 0, 0.99);
        }
    }
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
    private readonly JobHistoryStore _history;

    private readonly object _gate = new();
    private readonly List<Job> _jobs = new();
    private readonly ConcurrentQueue<Job> _queue = new();
    private int _nextId = 1;
    private bool _workerRunning;

    public event Action? Changed;

    private static string StorePath => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".daxter", "jobs.json");

    public JobService(ConfigState state, ILogger<JobService> log, JobHistoryStore history)
    {
        _state = state;
        _log = log;
        _history = history;
        LoadPersisted();
    }

    private void LoadPersisted()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var loaded = JsonSerializer.Deserialize<List<Job>>(File.ReadAllText(StorePath));
            if (loaded is null) return;

            foreach (var j in loaded)
            {
                // Queued/Running jobs can't survive a restart — the worker is gone. Mark them
                // Interrupted (don't auto-resume a write) so the record is kept but accurate.
                if (j.Status is JobStatus.Queued or JobStatus.Running)
                {
                    j.Status = JobStatus.Interrupted;
                    j.Finished ??= j.Started ?? j.Created;
                    j.Step = "Interrupted by restart";
                }
                _jobs.Add(j);
            }
            _nextId = (_jobs.Count > 0 ? _jobs.Max(j => j.Id) : 0) + 1;
        }
        catch
        {
            // Best-effort: corrupt file shouldn't break the app.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            List<Job> snapshot;
            lock (_gate) snapshot = _jobs.ToList();
            File.WriteAllText(StorePath, JsonSerializer.Serialize(snapshot));
        }
        catch
        {
            // Best-effort persistence; never let it break a refresh.
        }
    }

    private void NotifyChanged()
    {
        Save();
        Changed?.Invoke();
    }

    /// <summary>History signature: same kind+dataset+table+type pool together (partition name ignored).</summary>
    private static string Sig(RefreshSpec s) => $"{s.Kind}|{s.Workspace}|{s.Dataset}|{s.Table}|{s.Type}";

    /// <summary>Estimated duration (seconds) for a spec, from past runs; null if none yet.</summary>
    public double? EstimateSeconds(RefreshSpec spec) => _history.EstimateSeconds(Sig(spec));

    /// <summary>A copy of a job's activity log (thread-safe).</summary>
    public IReadOnlyList<JobEvent> EventsOf(int id)
    {
        lock (_gate)
            return _jobs.FirstOrDefault(j => j.Id == id)?.Events.ToList() ?? new List<JobEvent>();
    }

    private void AddEvent(Job job, string message)
    {
        lock (_gate)
        {
            job.Events.Add(new JobEvent(DateTimeOffset.Now, message));
            job.Step = message;
        }
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
            job.EstimateSeconds = _history.EstimateSeconds(Sig(spec));
            _jobs.Add(job);
            _queue.Enqueue(job);
        }

        AddEvent(job, "Queued");
        _log.LogInformation("Queued job #{Id}: {Title}", job.Id, job.Title);
        EnsureWorker();
        NotifyChanged();
        return job;
    }

    /// <summary>All jobs, newest first.</summary>
    public IReadOnlyList<Job> Snapshot()
    {
        lock (_gate) return _jobs.OrderByDescending(j => j.Id).ToList();
    }

    /// <summary>Jobs for one dataset, newest first (for the Refresh page's tracker).</summary>
    public IReadOnlyList<Job> For(string workspace, string dataset)
    {
        lock (_gate)
            return _jobs
                .Where(j => j.Spec.Workspace == workspace && j.Spec.Dataset == dataset)
                .OrderByDescending(j => j.Id).ToList();
    }

    public int ActiveCount
    {
        get { lock (_gate) return _jobs.Count(j => j.Status is JobStatus.Queued or JobStatus.Running); }
    }

    /// <summary>True if a refresh is queued or running for this dataset (drives the "Refreshing…" state).</summary>
    public bool IsRefreshing(string workspace, string dataset)
    {
        lock (_gate)
            return _jobs.Any(j => j.Spec.Workspace == workspace && j.Spec.Dataset == dataset
                && j.Status is JobStatus.Queued or JobStatus.Running);
    }

    /// <summary>Cancels a job: queued jobs are skipped; a running refresh is aborted (connection closed).</summary>
    public void Cancel(int id)
    {
        Job? job;
        IDisposable? session = null;
        lock (_gate)
        {
            job = _jobs.FirstOrDefault(j => j.Id == id);
            if (job is null) return;

            if (job.Status == JobStatus.Queued)
            {
                job.Status = JobStatus.Canceled;
                job.Finished = DateTimeOffset.Now;   // worker skips it when dequeued
            }
            else if (job.Status == JobStatus.Running)
            {
                job.CancelRequested = true;
                session = job.Session;               // abort outside the lock
            }
            else
            {
                return;
            }
        }

        _log.LogInformation("Cancel requested for job #{Id}", id);
        if (session is not null)
        {
            try { session.Dispose(); } catch { /* aborting the running command */ }
        }

        NotifyChanged();
    }

    /// <summary>Removes finished (succeeded/failed/canceled) jobs from the list.</summary>
    public void ClearFinished()
    {
        lock (_gate) _jobs.RemoveAll(j => j.Status is JobStatus.Succeeded or JobStatus.Failed or JobStatus.Canceled);
        NotifyChanged();
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
                if (job.Status == JobStatus.Canceled) continue; // canceled while queued

                job.Status = JobStatus.Running;
                job.Started = DateTimeOffset.Now;
                AddEvent(job, "Started");
                _log.LogInformation("Running job #{Id}: {Title}", job.Id, job.Title);
                NotifyChanged();

                try
                {
                    await ExecuteAsync(job);
                    job.Status = JobStatus.Succeeded;
                    AddEvent(job, $"Completed in {(int)(job.Duration?.TotalSeconds ?? 0)}s");
                    _history.Record(Sig(job.Spec), job.Duration?.TotalSeconds ?? 0);
                    _log.LogInformation("Job #{Id} succeeded in {Ms} ms", job.Id, (long)(job.Duration?.TotalMilliseconds ?? 0));
                }
                catch (Exception) when (job.CancelRequested)
                {
                    job.Status = JobStatus.Canceled;
                    AddEvent(job, "Canceled");
                    _log.LogInformation("Job #{Id} canceled", job.Id);
                }
                catch (Exception ex)
                {
                    job.Status = JobStatus.Failed;
                    job.Error = ex.Message;
                    AddEvent(job, "Failed");
                    _log.LogError("Job #{Id} failed: {Error}", job.Id, ex.Message);
                }
                finally
                {
                    job.Finished = DateTimeOffset.Now;
                    NotifyChanged();
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

    private async Task ExecuteAsync(Job job)
    {
        var spec = job.Spec;
        var cfg = _state.ToConfig(spec.Workspace, spec.Dataset);
        ITokenProvider provider = new MsalTokenProvider(cfg, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: false);
        var factory = new AdomdXmlaSessionFactory(cfg, provider);

        AddEvent(job, "Connecting to the model…");
        NotifyChanged();
        var session = await factory.CreateAsync();
        lock (_gate) job.Session = session;   // so Cancel can abort the running command

        try
        {
            var maint = new MaintenanceService(session, spec.Dataset);
            var tmsl = spec.Kind switch
            {
                RefreshKind.Model => maint.BuildModelRefresh(spec.Type),
                RefreshKind.Table => maint.BuildTableRefresh(spec.Table!, spec.Type),
                RefreshKind.Partition => maint.BuildPartitionRefresh(spec.Table!, spec.Partition!, spec.Type),
                RefreshKind.AllPartitions => maint.BuildPartitionsRefresh(spec.Table!, spec.Order, spec.Type, maxParallelism: 1),
                RefreshKind.SomePartitions => maint.BuildPartitionsRefresh(spec.Table!, spec.Partitions!, spec.Type, maxParallelism: 1),
                _ => throw new DaxterException("Unknown refresh kind."),
            };

            AddEvent(job, "Processing (TMSL refresh)…");
            NotifyChanged();
            maint.Execute(tmsl);
        }
        finally
        {
            lock (_gate) job.Session = null;
            try { session.Dispose(); } catch { /* may already be disposed by Cancel */ }
        }
    }

    private static string TitleFor(RefreshSpec s)
    {
        var t = s.Type == RefreshType.Full ? "" : $" [{s.Type}]";
        return s.Kind switch
        {
            RefreshKind.Model => $"Refresh model · {s.Dataset}{t}",
            RefreshKind.Table => $"Refresh table · {s.Table}{t}",
            RefreshKind.Partition => $"Refresh partition · {s.Table}[{s.Partition}]{t}",
            RefreshKind.AllPartitions => $"Refresh all partitions · {s.Table} ({(s.Order == PartitionOrder.NewestFirst ? "newest→oldest" : "oldest→newest")}){t}",
            RefreshKind.SomePartitions => $"Refresh {s.Partitions?.Count ?? 0} partitions · {s.Table}{t}",
            _ => "Refresh",
        };
    }
}
