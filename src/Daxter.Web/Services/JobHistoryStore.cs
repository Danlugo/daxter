using System.Text.Json;

namespace Daxter.Web.Services;

/// <summary>
/// Remembers how long past refreshes took, keyed by a signature (kind + dataset + table), so the
/// Jobs page can show an estimated duration. Singleton; thread-safe; persisted to
/// <c>~/.daxter/job-history.json</c> on the mounted volume. Keeps the last few durations per
/// signature and estimates with their average.
/// </summary>
public sealed class JobHistoryStore
{
    private const int KeepPerSignature = 8;

    private static string StorePath => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".daxter", "job-history.json");

    private readonly object _gate = new();
    private readonly Dictionary<string, List<double>> _durations; // signature -> recent seconds

    public JobHistoryStore() => _durations = Load();

    public void Record(string signature, double seconds)
    {
        if (seconds <= 0) return;
        lock (_gate)
        {
            if (!_durations.TryGetValue(signature, out var list))
                _durations[signature] = list = new();
            list.Add(seconds);
            if (list.Count > KeepPerSignature) list.RemoveAt(0);
            Save();
        }
    }

    /// <summary>Average of recent durations for the signature, or null if none recorded yet.</summary>
    public double? EstimateSeconds(string signature)
    {
        lock (_gate)
            return _durations.TryGetValue(signature, out var list) && list.Count > 0
                ? list.Average()
                : null;
    }

    private static Dictionary<string, List<double>> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize<Dictionary<string, List<double>>>(File.ReadAllText(StorePath)) ?? new()
                : new();
        }
        catch { return new(); }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_durations));
        }
        catch { /* best-effort */ }
    }
}
