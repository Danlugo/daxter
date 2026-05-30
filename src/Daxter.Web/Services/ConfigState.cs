using System.Text.Json;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;

namespace Daxter.Web.Services;

/// <summary>
/// The web console's effective configuration. Loaded from the environment at startup and
/// overlaid with a persisted file (on the mounted ~/.daxter volume, so it survives restarts).
/// The Configure page edits this live; the Explore/Status pages use it immediately.
/// Note: this is the console's own config — the MCP server (a separate process) still reads
/// its own <c>--env-file</c>.
/// </summary>
public sealed class ConfigState
{
    private static string ConfigPath => Path.Combine(
        Environment.GetEnvironmentVariable("HOME") ?? Path.GetTempPath(), ".daxter", "console-config.json");

    public string AuthMode { get; set; } = "device-code";
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? Workspace { get; set; }
    public string? Dataset { get; set; }
    public string? ProdWorkspaces { get; set; }
    public bool AllowWrites { get; set; }

    public bool Persisted { get; private set; }

    public ConfigState()
    {
        LoadFromEnvironment();
        Persisted = LoadPersisted();
    }

    private void LoadFromEnvironment()
    {
        static string? Env(string key)
        {
            var v = Environment.GetEnvironmentVariable(key);
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }

        AuthMode = Env("DAXTER_AUTH_MODE") ?? "device-code";
        TenantId = Env("DAXTER_TENANT_ID");
        ClientId = Env("DAXTER_CLIENT_ID");
        ClientSecret = Env("DAXTER_CLIENT_SECRET");
        Workspace = Env("DAXTER_WORKSPACE");
        Dataset = Env("DAXTER_DATASET");
        ProdWorkspaces = Env("DAXTER_PROD_WORKSPACES");
        AllowWrites = string.Equals(Env("DAXTER_MCP_ALLOW_WRITES"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool LoadPersisted()
    {
        try
        {
            if (!File.Exists(ConfigPath)) return false;
            var dto = JsonSerializer.Deserialize<Dto>(File.ReadAllText(ConfigPath));
            if (dto is null) return false;

            AuthMode = dto.AuthMode ?? AuthMode;
            TenantId = dto.TenantId; ClientId = dto.ClientId; ClientSecret = dto.ClientSecret;
            Workspace = dto.Workspace; Dataset = dto.Dataset;
            ProdWorkspaces = dto.ProdWorkspaces; AllowWrites = dto.AllowWrites;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Persists the current values to the mounted volume. Returns the path or an error.</summary>
    public string Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
        var dto = new Dto(AuthMode, TenantId, ClientId, ClientSecret, Workspace, Dataset, ProdWorkspaces, AllowWrites);
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true }));
        Persisted = true;
        return ConfigPath;
    }

    /// <summary>Builds a Core config from the current values; ws/ds args override the defaults.</summary>
    public DaxterConfig ToConfig(string? workspace = null, string? dataset = null) => new()
    {
        Workspace = workspace ?? Workspace ?? string.Empty,
        Dataset = dataset ?? Dataset,
        TenantId = TenantId,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        AuthMode = string.Equals(AuthMode, "service-principal", StringComparison.OrdinalIgnoreCase)
            ? Daxter.Core.Auth.AuthMode.ServicePrincipal
            : Daxter.Core.Auth.AuthMode.DeviceCode,
        ProdWorkspaces = string.IsNullOrWhiteSpace(ProdWorkspaces)
            ? Array.Empty<string>()
            : ProdWorkspaces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };

    private sealed record Dto(
        string? AuthMode, string? TenantId, string? ClientId, string? ClientSecret,
        string? Workspace, string? Dataset, string? ProdWorkspaces, bool AllowWrites);
}
