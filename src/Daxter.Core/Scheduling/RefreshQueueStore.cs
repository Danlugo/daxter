using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daxter.Core.Scheduling;

/// <summary>
/// Cross-process, file-backed refresh queue on the shared <c>~/.daxter</c> volume. Every DAXter
/// interface (CLI, MCP, web) enqueues here; the single worker — hosted by the long-running web
/// container — drains it. All read-modify-write access is serialized by an exclusive lock file so
/// concurrent processes never corrupt the queue, and writes go through a temp file + atomic replace.
///
/// <para>Per-model serialization is enforced at <see cref="ClaimRunnable"/>: a job is only handed to
/// the worker if no other job for the same model (<see cref="RefreshJob.ModelKey"/>) is already
/// Running. Different models run concurrently.</para>
/// </summary>
public sealed class RefreshQueueStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _dir;
    private readonly string _file;
    private readonly string _lockPath;

    public RefreshQueueStore(string? baseDir = null)
    {
        _dir = baseDir ?? Path.Combine(
            Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".daxter");
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "queue.json");
        _lockPath = Path.Combine(_dir, "queue.lock");
    }

    /// <summary>The directory the queue lives in (the shared volume root).</summary>
    public string BaseDir => _dir;

    /// <summary>Heartbeat file the worker touches each poll so surfaces can detect a live worker.</summary>
    public string HeartbeatPath => Path.Combine(_dir, "worker.heartbeat");

    // The on-disk shape: a monotonic id counter + the job list.
    private sealed class QueueFile
    {
        public int NextId { get; set; } = 1;
        public List<RefreshJob> Jobs { get; set; } = new();
    }

    // ---- locking & io ---------------------------------------------------

    private FileStream AcquireLock()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException)
            {
                // Another process holds the lock; back off briefly and retry (~5s budget).
                if (attempt >= 200)
                    throw new DaxterException("Could not acquire the refresh queue lock (busy).");
                Thread.Sleep(25);
            }
        }
    }

    private QueueFile Load()
    {
        if (!File.Exists(_file)) return new QueueFile();
        try
        {
            return JsonSerializer.Deserialize<QueueFile>(File.ReadAllText(_file), JsonOpts) ?? new QueueFile();
        }
        catch
        {
            // A corrupt file must not wedge the queue; start clean (best-effort durability).
            return new QueueFile();
        }
    }

    private void Persist(QueueFile qf)
    {
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(qf, JsonOpts));
        File.Move(tmp, _file, overwrite: true);
    }

    private T WithLock<T>(Func<QueueFile, T> mutate)
    {
        using var _ = AcquireLock();
        var qf = Load();
        var result = mutate(qf);
        Persist(qf);
        return result;
    }

    // ---- public API -----------------------------------------------------

    /// <summary>Adds a job to the queue (assigns its id + Queued status) and returns it.</summary>
    public RefreshJob Enqueue(RefreshSpec spec, string title, JobOrigin origin, double? estimateSeconds = null)
    {
        return WithLock(qf =>
        {
            var job = new RefreshJob
            {
                Id = qf.NextId++,
                Title = title,
                Spec = spec,
                Origin = origin,
                Status = JobStatus.Queued,
                EstimateSeconds = estimateSeconds,
            };
            job.Events.Add(new JobEvent(DateTimeOffset.Now, $"Queued ({origin})"));
            job.Step = "Queued";
            qf.Jobs.Add(job);
            return job;
        });
    }

    /// <summary>All jobs, newest first (lock-free-ish read).</summary>
    public IReadOnlyList<RefreshJob> All()
    {
        using var _ = AcquireLock();
        return Load().Jobs.OrderByDescending(j => j.Id).ToList();
    }

    /// <summary>Jobs for one model, newest first.</summary>
    public IReadOnlyList<RefreshJob> For(string workspace, string dataset)
    {
        var key = $"{workspace?.Trim()}{dataset?.Trim()}".ToLowerInvariant();
        return All().Where(j => j.ModelKey == key).ToList();
    }

    /// <summary>A single job by id, or null.</summary>
    public RefreshJob? Get(int id) => All().FirstOrDefault(j => j.Id == id);

    /// <summary>True if a refresh is queued or running for this model.</summary>
    public bool IsRefreshing(string workspace, string dataset)
    {
        var key = $"{workspace?.Trim()}{dataset?.Trim()}".ToLowerInvariant();
        return All().Any(j => j.ModelKey == key && j.IsActive);
    }

    /// <summary>Read-modify-write a single job under the lock. No-op if the id is gone.</summary>
    public RefreshJob? Mutate(int id, Action<RefreshJob> mutate)
    {
        return WithLock(qf =>
        {
            var job = qf.Jobs.FirstOrDefault(j => j.Id == id);
            if (job is not null) mutate(job);
            return job;
        });
    }

    /// <summary>
    /// Atomically claim the next runnable job for <paramref name="workerId"/>: the oldest Queued job
    /// whose model has no Running job (per-model serialization) and that isn't cancel-requested. Marks
    /// it Running and returns it, or null if nothing is runnable right now. Cancel-requested queued
    /// jobs are swept to Canceled in the same pass.
    /// </summary>
    public RefreshJob? ClaimRunnable(string workerId)
    {
        return WithLock(qf =>
        {
            // Sweep queued+canceled first so they don't block their model slot.
            foreach (var c in qf.Jobs.Where(j => j.Status == JobStatus.Queued && j.CancelRequested))
            {
                c.Status = JobStatus.Canceled;
                c.Finished = DateTimeOffset.Now;
                c.Step = "Canceled";
                c.Events.Add(new JobEvent(DateTimeOffset.Now, "Canceled while queued"));
            }

            var runningKeys = qf.Jobs
                .Where(j => j.Status == JobStatus.Running)
                .Select(j => j.ModelKey)
                .ToHashSet();

            var next = qf.Jobs
                .Where(j => j.Status == JobStatus.Queued && !j.CancelRequested && !runningKeys.Contains(j.ModelKey))
                .OrderBy(j => j.Id)
                .FirstOrDefault();

            if (next is null) return null;

            next.Status = JobStatus.Running;
            next.Started = DateTimeOffset.Now;
            next.WorkerId = workerId;
            next.Step = "Started";
            next.Events.Add(new JobEvent(DateTimeOffset.Now, "Started"));
            return next;
        });
    }

    /// <summary>Request cancellation: a queued job is canceled immediately; a running one is flagged
    /// for the worker to abort cooperatively.</summary>
    public void Cancel(int id)
    {
        Mutate(id, j =>
        {
            if (j.Status == JobStatus.Queued)
            {
                j.Status = JobStatus.Canceled;
                j.Finished = DateTimeOffset.Now;
                j.Step = "Canceled";
                j.Events.Add(new JobEvent(DateTimeOffset.Now, "Canceled while queued"));
            }
            else if (j.Status == JobStatus.Running)
            {
                j.CancelRequested = true;
                j.Events.Add(new JobEvent(DateTimeOffset.Now, "Cancellation requested"));
            }
        });
    }

    /// <summary>Re-enqueue a finished/interrupted job's spec as a new job.</summary>
    public RefreshJob? Resume(int id, string title, JobOrigin origin, double? estimateSeconds = null)
    {
        var spec = Get(id)?.Spec;
        return spec is null ? null : Enqueue(spec, title, origin, estimateSeconds);
    }

    /// <summary>Remove all finished jobs (succeeded/failed/canceled/interrupted).</summary>
    public void RemoveFinished()
    {
        WithLock<object?>(qf =>
        {
            qf.Jobs.RemoveAll(j => !j.IsActive);
            return null;
        });
    }

    /// <summary>Remove one finished job (never a queued/running one).</summary>
    public void Remove(int id)
    {
        WithLock<object?>(qf =>
        {
            var j = qf.Jobs.FirstOrDefault(x => x.Id == id);
            if (j is not null && !j.IsActive) qf.Jobs.Remove(j);
            return null;
        });
    }

    /// <summary>
    /// On worker startup, mark any jobs left Running/Queued by a *previous* worker as Interrupted —
    /// a write must never be auto-resumed across a restart. Pass the keep-queued flag false to also
    /// interrupt queued jobs (default keeps them so a freshly-started worker drains them).
    /// </summary>
    public void RecoverStaleRunning(string workerId)
    {
        WithLock<object?>(qf =>
        {
            foreach (var j in qf.Jobs.Where(j => j.Status == JobStatus.Running && j.WorkerId != workerId))
            {
                j.Status = JobStatus.Interrupted;
                j.Finished ??= j.Started ?? j.Created;
                j.Step = "Interrupted (worker restarted)";
                j.Events.Add(new JobEvent(DateTimeOffset.Now, "Interrupted by worker restart"));
            }
            return null;
        });
    }

    /// <summary>Write the worker heartbeat (UTC ticks) so surfaces can tell a worker is alive.</summary>
    public void Heartbeat(string workerId)
    {
        try
        {
            File.WriteAllText(HeartbeatPath, $"{workerId}\n{DateTimeOffset.UtcNow:O}");
        }
        catch { /* best-effort */ }
    }

    /// <summary>The age of the last heartbeat, or null if none / unreadable. Used to warn when no
    /// worker is running to drain the queue.</summary>
    public TimeSpan? HeartbeatAge()
    {
        try
        {
            if (!File.Exists(HeartbeatPath)) return null;
            var lines = File.ReadAllText(HeartbeatPath).Split('\n');
            if (lines.Length < 2) return null;
            if (!DateTimeOffset.TryParse(lines[1], out var when)) return null;
            return DateTimeOffset.UtcNow - when;
        }
        catch { return null; }
    }
}
