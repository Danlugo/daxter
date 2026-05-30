using System.Collections.Concurrent;
using System.Text.Json;
using Daxter.Core.Rest;

namespace Daxter.Web.Services;

/// <summary>
/// Holds in-flight + completed pipeline scans, keyed by pipeline id. Singleton — survives page
/// navigation, so a long scan kicked off on the Audit page is still there when the user comes
/// back. Completed scans are persisted to <c>~/.daxter/pipeline-scans.json</c> on the mounted
/// volume so they also survive container restarts. Read-only audit work; no write-safety gates.
/// Raises <see cref="Changed"/> so subscribed pages re-render as progress updates and the scan
/// completes.
/// </summary>
public sealed class PipelineScanStore
{
    public sealed record Entry(
        string PipelineId,
        DateTimeOffset Started,
        DateTimeOffset? Completed,
        int Done,
        int Total,
        PipelineScan? Result,
        string? Error,
        bool Cancelled)
    {
        public bool Running => Completed is null && Error is null && !Cancelled;
        public TimeSpan Elapsed => (Completed ?? DateTimeOffset.Now) - Started;
    }

    // Only completed-with-result entries are persisted (no point saving errors or empties).
    private sealed record Saved(string PipelineId, DateTimeOffset Started, DateTimeOffset Completed, PipelineScan Result);

    private const int MaxSaved = 20;

    private static string StorePath => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(),
        ".daxter", "pipeline-scans.json");

    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cts = new();
    private readonly object _diskGate = new();

    public event Action? Changed;

    public PipelineScanStore() => LoadFromDisk();

    public Entry? Get(string pipelineId)
        => _entries.TryGetValue(pipelineId, out var e) ? e : null;

    /// <summary>
    /// Starts a scan if none is running for this pipeline. Returns the current entry either way
    /// (call <see cref="Get"/> later for progress updates). The <paramref name="work"/> callback
    /// receives a progress reporter and a cancellation token; it should return the final scan.
    /// </summary>
    public Entry StartOrGet(
        string pipelineId,
        Func<Action<int, int>, CancellationToken, Task<PipelineScan>> work)
    {
        if (_entries.TryGetValue(pipelineId, out var existing) && existing.Running)
            return existing;

        var cts = new CancellationTokenSource();
        _cts[pipelineId] = cts;
        var entry = new Entry(pipelineId, DateTimeOffset.Now, null, 0, 0, null, null, false);
        _entries[pipelineId] = entry;
        Changed?.Invoke();

        _ = RunAsync(pipelineId, work, cts.Token);
        return entry;
    }

    private async Task RunAsync(
        string pipelineId,
        Func<Action<int, int>, CancellationToken, Task<PipelineScan>> work,
        CancellationToken ct)
    {
        try
        {
            void Progress(int done, int total)
            {
                if (_entries.TryGetValue(pipelineId, out var cur))
                {
                    _entries[pipelineId] = cur with { Done = done, Total = total };
                    Changed?.Invoke();
                }
            }

            var result = await work(Progress, ct);
            if (_entries.TryGetValue(pipelineId, out var cur))
                _entries[pipelineId] = cur with { Completed = DateTimeOffset.Now, Result = result };
            SaveToDisk(); // success → persist so it survives a container restart
        }
        catch (OperationCanceledException)
        {
            if (_entries.TryGetValue(pipelineId, out var cur))
                _entries[pipelineId] = cur with { Completed = DateTimeOffset.Now, Cancelled = true };
        }
        catch (Exception ex)
        {
            if (_entries.TryGetValue(pipelineId, out var cur))
                _entries[pipelineId] = cur with { Completed = DateTimeOffset.Now, Error = ex.Message };
        }
        finally
        {
            _cts.TryRemove(pipelineId, out _);
            Changed?.Invoke();
        }
    }

    /// <summary>Cancels a running scan (no-op if not running).</summary>
    public void Cancel(string pipelineId)
    {
        if (_cts.TryGetValue(pipelineId, out var cts)) cts.Cancel();
    }

    /// <summary>Forgets the result for a pipeline so the next call starts a fresh scan.</summary>
    public void Clear(string pipelineId)
    {
        if (_entries.TryRemove(pipelineId, out _))
        {
            SaveToDisk();
            Changed?.Invoke();
        }
    }

    private void LoadFromDisk()
    {
        try
        {
            if (!File.Exists(StorePath)) return;
            var saved = JsonSerializer.Deserialize<List<Saved>>(File.ReadAllText(StorePath));
            if (saved is null) return;
            foreach (var s in saved)
            {
                _entries[s.PipelineId] = new Entry(
                    s.PipelineId, s.Started, s.Completed, 0, 0, s.Result, null, false);
            }
        }
        catch
        {
            // Corrupt file shouldn't break startup — best-effort load.
        }
    }

    private void SaveToDisk()
    {
        lock (_diskGate)
        {
            try
            {
                var keep = _entries.Values
                    .Where(e => e.Completed is not null && e.Result is not null && e.Error is null && !e.Cancelled)
                    .OrderByDescending(e => e.Completed)
                    .Take(MaxSaved)
                    .Select(e => new Saved(e.PipelineId, e.Started, e.Completed!.Value, e.Result!))
                    .ToList();
                Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
                File.WriteAllText(StorePath, JsonSerializer.Serialize(keep));
            }
            catch
            {
                // Best-effort — a read-only volume shouldn't break the audit run.
            }
        }
    }
}
