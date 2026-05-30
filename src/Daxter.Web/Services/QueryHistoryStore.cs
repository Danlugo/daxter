using System.Text.Json;

namespace Daxter.Web.Services;

/// <summary>A remembered DAX query and where it ran.</summary>
public sealed record QueryEntry(string Dax, string Workspace, string? Dataset, int Count, DateTimeOffset LastUsed);

/// <summary>
/// Remembers DAX queries the user has run so recurrent ones can be re-run with a click.
/// Singleton; thread-safe; persisted to <c>~/.daxter/query-history.json</c> on the mounted
/// volume (survives restarts). De-dupes by (workspace, dataset, query text). Raises
/// <see cref="Changed"/> for live UI refresh.
/// </summary>
public sealed class QueryHistoryStore
{
    private const int MaxItems = 100;

    private static string StorePath => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".daxter", "query-history.json");

    private readonly object _gate = new();
    private readonly List<QueryEntry> _items;

    public event Action? Changed;

    public QueryHistoryStore() => _items = Load();

    /// <summary>Records a run of <paramref name="dax"/> (no-op for blank text).</summary>
    public void Record(string dax, string workspace, string? dataset)
    {
        if (string.IsNullOrWhiteSpace(dax)) return;
        dax = dax.Trim();

        lock (_gate)
        {
            var idx = _items.FindIndex(q =>
                q.Dax == dax && q.Workspace == workspace && q.Dataset == dataset);

            if (idx >= 0)
            {
                _items[idx] = _items[idx] with { Count = _items[idx].Count + 1, LastUsed = DateTimeOffset.Now };
            }
            else
            {
                _items.Add(new QueryEntry(dax, workspace, dataset, 1, DateTimeOffset.Now));
                if (_items.Count > MaxItems)
                {
                    var oldest = _items.OrderBy(q => q.LastUsed).First();
                    _items.Remove(oldest);
                }
            }

            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>The <paramref name="n"/> most recently run queries (newest first).</summary>
    public IReadOnlyList<QueryEntry> Recent(int n)
    {
        lock (_gate)
            return _items.OrderByDescending(q => q.LastUsed).Take(n).ToList();
    }

    public void Remove(string dax, string workspace, string? dataset)
    {
        lock (_gate)
        {
            _items.RemoveAll(q => q.Dax == dax && q.Workspace == workspace && q.Dataset == dataset);
            Save();
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    private static List<QueryEntry> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize<List<QueryEntry>>(File.ReadAllText(StorePath)) ?? new()
                : new();
        }
        catch
        {
            return new();
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_items));
        }
        catch
        {
            // Best-effort.
        }
    }
}
