using System.Text.Json;

namespace Daxter.Core.Audit;

/// <summary>A named, reusable pipeline parameter-value check, saved by the user.</summary>
public sealed record SavedAuditCheck(
    string Name, string PipelineId, string Stage, string Param, string Value, bool NotEquals, DateTimeOffset SavedAt);

/// <summary>
/// Shared store for saved audit checks — written by the web console, read (and runnable) by the
/// CLI and MCP. Lives in <c>Daxter.Core</c> so all three surfaces can use it; persisted to
/// <c>~/.daxter/audit-saved.json</c> on the common volume. Thread-safe; best-effort file IO.
/// </summary>
public sealed class SavedAuditCheckStore
{
    public const int MaxItems = 30;

    private static string Dir => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".daxter");
    private static string FilePath => Path.Combine(Dir, "audit-saved.json");
    private static string LegacyPath => Path.Combine(Dir, "audit-history.json");

    private readonly object _gate = new();
    private List<SavedAuditCheck> _items;

    /// <summary>Raised after a change so a UI can refresh.</summary>
    public event Action? Changed;

    public SavedAuditCheckStore() => _items = Load();

    /// <summary>Upserts a saved check (dedup by spec), bumps to top, caps at <see cref="MaxItems"/>.</summary>
    public void Save(string name, string pipelineId, string stage, string param, string value, bool notEquals)
    {
        if (string.IsNullOrWhiteSpace(pipelineId) || string.IsNullOrWhiteSpace(param)) return;
        lock (_gate)
        {
            _items.RemoveAll(c => Same(c, pipelineId, stage, param, value, notEquals));
            _items.Insert(0, new SavedAuditCheck(
                string.IsNullOrWhiteSpace(name) ? $"{param} {(notEquals ? "!=" : "=")} {value}" : name.Trim(),
                pipelineId, stage ?? "", param, value ?? "", notEquals, DateTimeOffset.Now));
            if (_items.Count > MaxItems) _items = _items.Take(MaxItems).ToList();
            Persist();
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<SavedAuditCheck> All()
    {
        lock (_gate) { return _items.ToList(); }
    }

    /// <summary>Finds a saved check by name (case-insensitive); null if none.</summary>
    public SavedAuditCheck? FindByName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (_gate)
        {
            return _items.FirstOrDefault(c => string.Equals(c.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Remove(string pipelineId, string? stage, string? param, string? value, bool notEquals)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(c => Same(c, pipelineId, stage ?? "", param ?? "", value ?? "", notEquals)) == 0) return;
            Persist();
        }
        Changed?.Invoke();
    }

    private static bool Same(SavedAuditCheck c, string pipelineId, string stage, string param, string value, bool notEquals)
        => c.PipelineId == pipelineId
           && string.Equals(c.Stage, stage, StringComparison.OrdinalIgnoreCase)
           && string.Equals(c.Param, param, StringComparison.OrdinalIgnoreCase)
           && string.Equals(c.Value, value, StringComparison.Ordinal)
           && c.NotEquals == notEquals;

    private List<SavedAuditCheck> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<List<SavedAuditCheck>>(File.ReadAllText(FilePath)) ?? new();

            // One-time migration: lift Saved=true param-checks out of the old web audit-history.json.
            var migrated = MigrateLegacy();
            if (migrated.Count > 0) { _items = migrated; Persist(); }
            return migrated;
        }
        catch
        {
            return new();
        }
    }

    private static List<SavedAuditCheck> MigrateLegacy()
    {
        var list = new List<SavedAuditCheck>();
        try
        {
            if (!File.Exists(LegacyPath)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(LegacyPath));
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                if (!(e.TryGetProperty("Saved", out var s) && s.ValueKind == JsonValueKind.True)) continue;
                if (!e.TryGetProperty("Param", out var p) || p.ValueKind != JsonValueKind.String) continue;
                string Str(string k) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
                var ne = e.TryGetProperty("NotEquals", out var n) && n.ValueKind == JsonValueKind.True;
                list.Add(new SavedAuditCheck(Str("TargetName"), Str("TargetId"), Str("Stage"), Str("Param"), Str("Value"), ne, DateTimeOffset.Now));
            }
        }
        catch { /* best-effort */ }
        return list;
    }

    private void Persist()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_items));
        }
        catch { /* best-effort */ }
    }
}
