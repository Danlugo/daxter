using System.Text.Json;

namespace Daxter.Core.Configuration;

/// <summary>
/// Settings persisted to the shared volume (<c>~/.daxter/console-config.json</c>) — the single
/// source of truth the web console writes and the CLI/MCP read. JSON keys are PascalCase, matching
/// what the console has always written. Read everywhere via <see cref="DaxterConfig.FromEnvironment"/>;
/// env vars remain a lower-precedence fallback for headless/CI use.
/// </summary>
public sealed record PersistedSettings(
    string? AuthMode = null,
    string? TenantId = null,
    string? ClientId = null,
    string? ClientSecret = null,
    string? Workspace = null,
    string? Dataset = null,
    string? ProdWorkspaces = null,
    // v1.46.0 — the operator's chosen ACTIVE permission level (read | execute | modify | full),
    // capped by the DAXTER_LEVEL env ceiling at resolution time. Null ⇒ "read" (safe default).
    // Replaces the old AllowWrites/AllowModelEdit bools — see PermissionPolicy / PermissionLevel.
    string? Level = null,
    // Comma-separated glob patterns for the new writes-gate. ReadOnlyWorkspaces is a deny-list
    // (anything matching is locked); WriteWorkspaces is an allow-list (when non-empty, anything
    // NOT matching is locked). Both null when the user hasn't configured the new model yet —
    // the legacy ProdWorkspaces is still honored.
    string? ReadOnlyWorkspaces = null,
    string? WriteWorkspaces = null)
{
    /// <summary>Path on the shared volume. Honors HOME (the token volume is mounted at <c>$HOME/.daxter</c>).</summary>
    public static string FilePath => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".daxter", "console-config.json");

    /// <summary>Reads the persisted settings; returns an empty instance if the file is absent or unreadable.</summary>
    public static PersistedSettings Load()
    {
        try
        {
            return File.Exists(FilePath)
                ? JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(FilePath)) ?? new PersistedSettings()
                : new PersistedSettings();
        }
        catch
        {
            return new PersistedSettings(); // best-effort: a garbled file must not break config resolution
        }
    }

    /// <summary>Writes the settings to the shared volume (UTF-8, no BOM).</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
