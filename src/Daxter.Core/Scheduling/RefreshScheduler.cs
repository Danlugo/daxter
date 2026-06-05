using System.Collections.Concurrent;

namespace Daxter.Core.Scheduling;

/// <summary>Lets the injected executor report progress to the shared queue and observe cooperative
/// cancellation, without coupling Core's scheduler to any specific XMLA/UI implementation.</summary>
public interface IRefreshProgress
{
    /// <summary>Append an activity-log line (also becomes the job's current Step).</summary>
    void Event(string message);

    /// <summary>Report multi-partition progress ("done of total").</summary>
    void Partitions(int done, int total);

    /// <summary>Record the ordered partition list the job will process (captured before refreshing the
    /// first one) so a later resume can re-run only the not-yet-done partitions.</summary>
    void Plan(IReadOnlyList<string> orderedPartitions);

    /// <summary>True once any interface has requested cancellation of this job.</summary>
    bool CancelRequested { get; }
}

/// <summary>
/// Does the actual refresh work for one job. Hosts inject the real implementation (connect over
/// XMLA, run ordered partition commands with retry, abort the session on cancel). Tests inject a
/// fake. Throw to fail the job; honour <see cref="IRefreshProgress.CancelRequested"/> / the token to
/// cancel cooperatively.
/// </summary>
public delegate Task RefreshExecutor(RefreshJob job, IRefreshProgress progress, CancellationToken ct);

/// <summary>
/// The single shared refresh worker. Polls the <see cref="RefreshQueueStore"/>, dispatches runnable
/// jobs (one per model, several models concurrently up to <see cref="_maxConcurrentModels"/>), runs
/// each through the injected <see cref="RefreshExecutor"/>, and writes status/heartbeat back to the
/// shared volume. This is the one place refreshes execute — CLI, MCP and the web UI all enqueue and
/// let this drain the queue, so per-model serialization, ordering, retries and job status are
/// identical regardless of which interface started the refresh.
/// </summary>
public sealed class RefreshScheduler
{
    private readonly RefreshQueueStore _store;
    private readonly RefreshExecutor _executor;
    private readonly string _workerId;
    private readonly int _maxConcurrentModels;
    private readonly TimeSpan _pollInterval;
    private readonly Action<RefreshJob>? _onChanged;

    private readonly ConcurrentDictionary<int, Task> _inFlight = new();

    public RefreshScheduler(
        RefreshQueueStore store,
        RefreshExecutor executor,
        string? workerId = null,
        int maxConcurrentModels = 4,
        TimeSpan? pollInterval = null,
        Action<RefreshJob>? onChanged = null)
    {
        _store = store;
        _executor = executor;
        _workerId = workerId ?? $"worker-{Guid.NewGuid():N}";
        _maxConcurrentModels = Math.Max(1, maxConcurrentModels);
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
        _onChanged = onChanged;
    }

    /// <summary>Default number of models the worker refreshes concurrently.</summary>
    public const int DefaultMaxConcurrentModels = 4;

    /// <summary>Upper bound for the concurrency cap — concurrent refreshes consume capacity and XMLA
    /// sessions, so a runaway value is clamped to protect the capacity.</summary>
    public const int MaxConcurrentModelsCeiling = 16;

    /// <summary>The env var that overrides the concurrent-model cap.</summary>
    public const string MaxConcurrentModelsEnv = "DAXTER_REFRESH_MAX_CONCURRENT_MODELS";

    /// <summary>Parses the <see cref="MaxConcurrentModelsEnv"/> value into a concurrency cap: a valid
    /// integer clamped to [1, <see cref="MaxConcurrentModelsCeiling"/>], else the default 4.</summary>
    public static int ParseMaxConcurrentModels(string? raw)
        => int.TryParse(raw?.Trim(), out var n)
            ? Math.Clamp(n, 1, MaxConcurrentModelsCeiling)
            : DefaultMaxConcurrentModels;

    /// <summary>The effective concurrency cap this worker is running with.</summary>
    public int MaxConcurrentModels => _maxConcurrentModels;

    public string WorkerId => _workerId;

    /// <summary>Jobs currently executing in this worker.</summary>
    public int InFlightCount => _inFlight.Count;

    /// <summary>
    /// Run the poll/dispatch loop until cancelled. On startup, recovers jobs orphaned by a previous
    /// worker (marks them Interrupted) so a stale Running record never blocks a model forever.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _store.RecoverStaleRunning(_workerId);
        while (!ct.IsCancellationRequested)
        {
            _store.Heartbeat(_workerId);
            DispatchReady(ct);
            try { await Task.Delay(_pollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One dispatch pass — claim and start runnable jobs up to the concurrency cap. Exposed
    /// for deterministic testing; <see cref="RunAsync"/> calls it on every poll.</summary>
    public void Tick(CancellationToken ct = default) => DispatchReady(ct);

    /// <summary>Await all jobs this worker currently has in flight (test/shutdown helper).</summary>
    public Task DrainAsync() => Task.WhenAll(_inFlight.Values.ToArray());

    private void DispatchReady(CancellationToken ct)
    {
        while (_inFlight.Count < _maxConcurrentModels)
        {
            // ClaimRunnable enforces per-model serialization (skips a model that's already Running).
            var job = _store.ClaimRunnable(_workerId);
            if (job is null) break;

            _onChanged?.Invoke(job);
            StartJob(job, ct);
        }
    }

    private void StartJob(RefreshJob job, CancellationToken ct)
    {
        var task = Task.Run(() => RunJobAsync(job, ct), ct);
        _inFlight[job.Id] = task;
        _ = task.ContinueWith(t => _inFlight.TryRemove(job.Id, out Task? _), TaskScheduler.Default);
    }

    private async Task RunJobAsync(RefreshJob job, CancellationToken ct)
    {
        var progress = new StoreProgress(_store, job.Id, () => _onChanged?.Invoke(job));
        try
        {
            await _executor(job, progress, ct).ConfigureAwait(false);
            Finish(job.Id, JobStatus.Succeeded, null);
        }
        catch (OperationCanceledException) when (progress.CancelRequested || ct.IsCancellationRequested)
        {
            Finish(job.Id, JobStatus.Canceled, null);
        }
        catch (Exception) when (progress.CancelRequested)
        {
            Finish(job.Id, JobStatus.Canceled, null);
        }
        catch (Exception ex)
        {
            Finish(job.Id, JobStatus.Failed, ex.Message);
        }
    }

    private void Finish(int id, JobStatus status, string? error)
    {
        var job = _store.Mutate(id, j =>
        {
            j.Status = status;
            j.Finished = DateTimeOffset.Now;
            j.Error = error;
            var secs = (int)(j.Duration?.TotalSeconds ?? 0);
            j.Step = status switch
            {
                JobStatus.Succeeded => $"Completed in {secs}s",
                JobStatus.Canceled => "Canceled",
                JobStatus.Failed => "Failed",
                _ => j.Step,
            };
            j.Events.Add(new JobEvent(DateTimeOffset.Now, j.Step ?? status.ToString()));
        });
        if (job is not null) _onChanged?.Invoke(job);
    }

    /// <summary>Progress sink that writes activity/partition counts back to the shared queue and
    /// reads cancellation from it (so cancel works across processes).</summary>
    private sealed class StoreProgress : IRefreshProgress
    {
        private readonly RefreshQueueStore _store;
        private readonly int _id;
        private readonly Action _changed;

        public StoreProgress(RefreshQueueStore store, int id, Action changed)
        {
            _store = store;
            _id = id;
            _changed = changed;
        }

        public void Event(string message)
        {
            _store.Mutate(_id, j =>
            {
                j.Step = message;
                j.Events.Add(new JobEvent(DateTimeOffset.Now, message));
            });
            _changed();
        }

        public void Partitions(int done, int total)
        {
            _store.Mutate(_id, j =>
            {
                j.PartitionTotal = total;
                j.PartitionDone = done;
            });
            _changed();
        }

        public void Plan(IReadOnlyList<string> orderedPartitions)
        {
            _store.Mutate(_id, j =>
            {
                j.OrderedPartitions = orderedPartitions.ToList();
                j.PartitionTotal = orderedPartitions.Count;
                j.PartitionDone ??= 0;
            });
            _changed();
        }

        public bool CancelRequested => _store.Get(_id)?.CancelRequested ?? false;
    }
}
