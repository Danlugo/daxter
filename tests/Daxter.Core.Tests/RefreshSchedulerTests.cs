using System.Collections.Concurrent;
using Daxter.Core.Maintenance;
using Daxter.Core.Scheduling;

namespace Daxter.Core.Tests;

/// <summary>
/// Tests the shared refresh scheduler engine: the file-backed queue round-trips, per-model
/// serialization holds (one refresh per model at a time), different models run in parallel, and
/// status transitions / cancellation behave.
/// </summary>
public sealed class RefreshSchedulerTests : IDisposable
{
    private readonly string _dir;
    private readonly RefreshQueueStore _store;

    public RefreshSchedulerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "daxter-queue-test", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new RefreshQueueStore(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best-effort */ }
    }

    private RefreshSpec Spec(string ws, string ds, RefreshKind kind = RefreshKind.Model) =>
        new(kind, ws, ds, null, null, PartitionOrder.NewestFirst);

    private static RefreshExecutor Noop => (_, _, _) => Task.CompletedTask;

    // ---- store ----------------------------------------------------------

    [Fact]
    public void ResumeSpec_remaining_only_skips_the_done_partitions()
    {
        // A partition job that recorded its order and stopped at 2 of 5.
        var job = _store.Enqueue(Spec("WS - Dev", "M1", RefreshKind.AllPartitions), "all parts", JobOrigin.Mcp);
        _store.Mutate(job.Id, j =>
        {
            j.Spec = j.Spec with { Table = "FACT" };
            j.OrderedPartitions = new() { "p1", "p2", "p3", "p4", "p5" };
            j.PartitionDone = 2;
            j.Status = JobStatus.Interrupted;
        });

        // remaining-only → SomePartitions for just p3, p4, p5
        var (spec, count, partial) = _store.ResumeSpec(job.Id, remainingOnly: true)!.Value;
        Assert.True(partial);
        Assert.Equal(3, count);
        Assert.Equal(RefreshKind.SomePartitions, spec.Kind);
        Assert.Equal(new[] { "p3", "p4", "p5" }, spec.Partitions);
        Assert.Equal("FACT", spec.Table);

        // full → original AllPartitions spec
        var full = _store.ResumeSpec(job.Id, remainingOnly: false)!.Value;
        Assert.False(full.Partial);
        Assert.Equal(RefreshKind.AllPartitions, full.Spec.Kind);
    }

    [Fact]
    public void ResumeSpec_falls_back_to_full_when_nothing_recorded()
    {
        // Done=0 (failed before any partition) → full re-run even when remaining-only is asked.
        var job = _store.Enqueue(Spec("WS - Dev", "M2", RefreshKind.AllPartitions), "all parts", JobOrigin.Mcp);
        _store.Mutate(job.Id, j => { j.Status = JobStatus.Failed; });

        var (spec, _, partial) = _store.ResumeSpec(job.Id, remainingOnly: true)!.Value;
        Assert.False(partial);
        Assert.Equal(RefreshKind.AllPartitions, spec.Kind);
        Assert.Null(_store.ResumeSpec(999, remainingOnly: true));   // unknown id → null
    }

    [Theory]
    [InlineData(null, 4)]          // unset → default
    [InlineData("", 4)]            // blank → default
    [InlineData("not-a-number", 4)]// junk → default
    [InlineData("1", 1)]
    [InlineData("8", 8)]
    [InlineData(" 6 ", 6)]         // trimmed
    [InlineData("0", 1)]           // clamped up to the floor
    [InlineData("-3", 1)]          // negative → floor
    [InlineData("999", 16)]        // clamped down to the ceiling
    public void ParseMaxConcurrentModels_clamps_and_defaults(string? raw, int expected)
        => Assert.Equal(expected, RefreshScheduler.ParseMaxConcurrentModels(raw));

    [Fact]
    public void Enqueue_assigns_incrementing_ids_and_persists()
    {
        var a = _store.Enqueue(Spec("WS - Dev", "M1"), "m1", JobOrigin.Cli);
        var b = _store.Enqueue(Spec("WS - Dev", "M2"), "m2", JobOrigin.Mcp);

        Assert.Equal(1, a.Id);
        Assert.Equal(2, b.Id);

        // A fresh store instance (separate "process") sees the same jobs.
        var reopened = new RefreshQueueStore(_dir);
        Assert.Equal(2, reopened.All().Count);
        Assert.All(reopened.All(), j => Assert.Equal(JobStatus.Queued, j.Status));
    }

    [Fact]
    public void ClaimRunnable_serializes_same_model_but_allows_other_models()
    {
        _store.Enqueue(Spec("WS - Dev", "Sales"), "s1", JobOrigin.Cli); // id 1
        _store.Enqueue(Spec("WS - Dev", "Sales"), "s2", JobOrigin.Cli); // id 2 (same model)
        _store.Enqueue(Spec("WS - Dev", "Inv"), "i1", JobOrigin.Cli);   // id 3 (other model)

        var first = _store.ClaimRunnable("w1");
        var second = _store.ClaimRunnable("w1");
        var third = _store.ClaimRunnable("w1");

        Assert.Equal(1, first!.Id);          // oldest Sales job
        Assert.Equal(3, second!.Id);         // Sales now busy → skip id 2, take the other model
        Assert.Null(third);                  // nothing else runnable (Sales still busy)

        // Finish the Sales job → its second job becomes runnable.
        _store.Mutate(1, j => { j.Status = JobStatus.Succeeded; j.Finished = DateTimeOffset.Now; });
        var fourth = _store.ClaimRunnable("w1");
        Assert.Equal(2, fourth!.Id);
    }

    [Fact]
    public void Cancel_queued_job_is_canceled_and_never_claimed()
    {
        var j = _store.Enqueue(Spec("WS - Dev", "Sales"), "s1", JobOrigin.Cli);
        _store.Cancel(j.Id);

        Assert.Equal(JobStatus.Canceled, _store.Get(j.Id)!.Status);
        Assert.Null(_store.ClaimRunnable("w1"));
    }

    [Fact]
    public void RecoverStaleRunning_interrupts_jobs_from_a_previous_worker()
    {
        var j = _store.Enqueue(Spec("WS - Dev", "Sales"), "s1", JobOrigin.Cli);
        _store.ClaimRunnable("old-worker");     // marks Running under old-worker
        Assert.Equal(JobStatus.Running, _store.Get(j.Id)!.Status);

        _store.RecoverStaleRunning("new-worker");
        Assert.Equal(JobStatus.Interrupted, _store.Get(j.Id)!.Status);
    }

    // ---- scheduler ------------------------------------------------------

    [Fact]
    public async Task Tick_runs_a_queued_job_to_success()
    {
        var ran = false;
        var sched = new RefreshScheduler(_store,
            executor: (_, p, _) => { p.Event("working"); ran = true; return Task.CompletedTask; },
            maxConcurrentModels: 4);

        var job = _store.Enqueue(Spec("WS - Dev", "Sales"), "s1", JobOrigin.Cli);
        sched.Tick();
        await sched.DrainAsync();

        Assert.True(ran);
        Assert.Equal(JobStatus.Succeeded, _store.Get(job.Id)!.Status);
        Assert.Contains(_store.Get(job.Id)!.Events, e => e.Message == "working");
    }

    [Fact]
    public async Task Executor_throwing_marks_job_failed_with_error()
    {
        var sched = new RefreshScheduler(_store,
            executor: (_, _, _) => throw new InvalidOperationException("boom"));

        var job = _store.Enqueue(Spec("WS - Dev", "Sales"), "s1", JobOrigin.Cli);
        sched.Tick();
        await sched.DrainAsync();

        var done = _store.Get(job.Id)!;
        Assert.Equal(JobStatus.Failed, done.Status);
        Assert.Equal("boom", done.Error);
    }

    [Fact]
    public async Task Same_model_jobs_never_run_concurrently()
    {
        var concurrentForModel = new ConcurrentDictionary<string, int>();
        var maxObserved = 0;
        var gate = new object();

        RefreshExecutor exec = async (job, _, _) =>
        {
            var n = concurrentForModel.AddOrUpdate(job.ModelKey, 1, (_, v) => v + 1);
            lock (gate) maxObserved = Math.Max(maxObserved, n);
            await Task.Delay(40);
            concurrentForModel.AddOrUpdate(job.ModelKey, 0, (_, v) => v - 1);
        };

        var sched = new RefreshScheduler(_store, exec, maxConcurrentModels: 8);

        // 3 jobs for the SAME model — must run strictly one at a time.
        for (var i = 0; i < 3; i++)
            _store.Enqueue(Spec("WS - Dev", "Sales"), $"s{i}", JobOrigin.Cli);

        // Drive several dispatch passes as slots free up.
        for (var pass = 0; pass < 6; pass++)
        {
            sched.Tick();
            await Task.Delay(30);
        }
        await sched.DrainAsync();
        sched.Tick();
        await sched.DrainAsync();

        Assert.Equal(1, maxObserved);  // never two of the same model at once
        Assert.All(_store.All(), j => Assert.Equal(JobStatus.Succeeded, j.Status));
    }

    [Fact]
    public async Task Different_models_run_in_parallel()
    {
        var running = 0;
        var maxParallel = 0;
        var gate = new object();

        RefreshExecutor exec = async (_, _, _) =>
        {
            lock (gate) { running++; maxParallel = Math.Max(maxParallel, running); }
            await Task.Delay(60);
            lock (gate) running--;
        };

        var sched = new RefreshScheduler(_store, exec, maxConcurrentModels: 4);

        _store.Enqueue(Spec("WS - Dev", "Sales"), "a", JobOrigin.Cli);
        _store.Enqueue(Spec("WS - Dev", "Inventory"), "b", JobOrigin.Cli);
        _store.Enqueue(Spec("WS - Dev", "Finance"), "c", JobOrigin.Cli);

        sched.Tick();             // claims all three (distinct models)
        await Task.Delay(20);
        Assert.True(maxParallel >= 2, $"expected parallel models, saw {maxParallel}");
        await sched.DrainAsync();
    }

    [Fact]
    public async Task Concurrency_cap_limits_models_in_flight()
    {
        RefreshExecutor exec = async (_, _, _) => await Task.Delay(80);
        var sched = new RefreshScheduler(_store, exec, maxConcurrentModels: 2);

        for (var i = 0; i < 5; i++)
            _store.Enqueue(Spec("WS - Dev", $"Model{i}"), $"m{i}", JobOrigin.Cli);

        sched.Tick();
        Assert.True(sched.InFlightCount <= 2, $"cap is 2, saw {sched.InFlightCount}");
        await sched.DrainAsync();
    }
}
