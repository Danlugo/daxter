using System.Text.Json;

namespace Daxter.Web.Services;

/// <summary>
/// One audit shortcut. Two flavors share this record:
///  • a <b>recent</b> audit (auto, chronological) — <see cref="Saved"/> = false; just Type+Target.
///  • a <b>saved</b> param-check (named, pinned) — <see cref="Saved"/> = true; carries the full
///    spec (Stage/Param/Value/NotEquals) so a click re-runs that exact check.
/// </summary>
public sealed record AuditEntry(
    string Type, string TargetId, string TargetName, DateTimeOffset LastRan,
    string? Stage = null, string? Param = null, string? Value = null,
    bool NotEquals = false, bool Saved = false);

/// <summary>
/// Audit shortcuts for the sidebar: recently-opened pipelines (chronological, auto) and
/// user-saved named param-checks (pinned). Singleton; thread-safe; persisted to
/// <c>~/.daxter/audit-history.json</c> on the mounted volume so both survive restarts.
/// </summary>
public sealed class AuditHistoryStore
{
    public const int MaxRecent = 10;

    public const string TypePipelineRules = "pipeline-rules";

    private static string StorePath => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(),
        ".daxter", "audit-history.json");

    private readonly object _gate = new();
    private List<AuditEntry> _items;

    public event Action? Changed;

    public AuditHistoryStore() => _items = Load();

    // ---- recents (auto) ----

    /// <summary>Records (or bumps) a recently-opened audit target. Trims recents to MaxRecent.</summary>
    public void Record(string type, string targetId, string targetName)
    {
        if (string.IsNullOrWhiteSpace(type) || string.IsNullOrWhiteSpace(targetId)) return;
        lock (_gate)
        {
            _items.RemoveAll(e => !e.Saved && e.Type == type && e.TargetId == targetId);
            _items.Insert(0, new AuditEntry(type, targetId, targetName ?? "", DateTimeOffset.Now));
            TrimRecents();
            Save();
        }
        Changed?.Invoke();
    }

    public IReadOnlyList<AuditEntry> Recent()
    {
        lock (_gate) { return _items.Where(e => !e.Saved).Take(MaxRecent).ToList(); }
    }

    public void Remove(string type, string targetId)
    {
        lock (_gate)
        {
            if (_items.RemoveAll(e => !e.Saved && e.Type == type && e.TargetId == targetId) == 0) return;
            Save();
        }
        Changed?.Invoke();
    }

    /// <summary>Clears the recents only — saved checks are kept.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (_items.RemoveAll(e => !e.Saved) == 0) return;
            Save();
        }
        Changed?.Invoke();
    }

    // Saved param-checks moved to Daxter.Core.Audit.SavedAuditCheckStore so the CLI + MCP can read
    // and run them too. (Saved=true entries in the legacy file are ignored here and migrated there.)

    private void TrimRecents()
    {
        foreach (var extra in _items.Where(e => !e.Saved).Skip(MaxRecent).ToList())
            _items.Remove(extra);
    }

    private static List<AuditEntry> Load()
    {
        try
        {
            return File.Exists(StorePath)
                ? JsonSerializer.Deserialize<List<AuditEntry>>(File.ReadAllText(StorePath)) ?? new()
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
