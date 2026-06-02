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
    bool AllowWrites = false,
    bool AllowModelEdit = false)
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
