using System.Text.Json;

namespace Daxter.Web.Services;

/// <summary>One tracked item and how often it's been opened. Tables carry their
/// <c>workspacedataset</c> in <see cref="Parent"/>; datasets carry their workspace.</summary>
public sealed record UsageItem(string Kind, string Name, string? Parent, int Count, DateTimeOffset LastUsed, string Context = "");

/// <summary>
/// Tracks how often each workspace / dataset / table is opened so the console can surface a
/// "Frequent" shortcut list. Singleton; thread-safe; persisted to <c>~/.daxter/usage.json</c>
/// on the mounted volume (survives restarts). Raises <see cref="Changed"/> so the sidebar
/// refreshes live.
/// </summary>
public sealed class UsageStore
{
    public const string Workspace = "workspace";
    public const string Dataset = "dataset";
    public const string Table = "table";

    // Page contexts (each page keeps its own Frequent list).
    public const string ExploreContext = "explore";
    public const string RefreshContext = "refresh";

    private const int MaxItems = 500;

    private static string StorePath => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".daxter", "usage.json");

    private readonly object _gate = new();
    private readonly List<UsageItem> _items;

    /// <summary>Fires after any change so a UI can re-read <see cref="Top"/>.</summary>
    public event Action? Changed;

    public UsageStore() => _items = Load();

    /// <summary>Records one open of a workspace/dataset/table within a page context (no-op for blank names).</summary>
    public void Record(string kind, string name, string? parent = null, string context = "")
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();

        lock (_gate)
        {
            var idx = _items.FindIndex(i =>
                i.Kind == kind && i.Name == name && i.Parent == parent && i.Context == context);

            if (idx >= 0)
            {
                _items[idx] = _items[idx] with { Count = _items[idx].Count + 1, LastUsed = DateTimeOffset.Now };
            }
            else
            {
                _items.Add(new UsageItem(kind, name, parent, 1, DateTimeOffset.Now, context));
                if (_items.Count > MaxItems)
                {
                    // Drop the least useful (lowest count, then oldest).
                    var victim = _items.OrderBy(i => i.Count).ThenBy(i => i.LastUsed).First();
                    _items.Remove(victim);
                }
            }

            Save();
        }

        Changed?.Invoke();
    }

    /// <summary>The <paramref name="n"/> most-used items of a kind within a page context.</summary>
    public IReadOnlyList<UsageItem> Top(string kind, int n, string context)
    {
        lock (_gate)
        {
            return _items
                .Where(i => i.Kind == kind && i.Context == context)
                .OrderByDescending(i => i.Count).ThenByDescending(i => i.LastUsed)
                .Take(n)
                .ToList();
        }
    }

    /// <summary>Forgets all tracked usage.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _items.Clear();
            Save();
        }
        Changed?.Invoke();
    }

    private static List<UsageItem> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize<List<UsageItem>>(File.ReadAllText(StorePath)) ?? new()
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
            // Best-effort: a read-only volume shouldn't break navigation.
        }
    }
}
