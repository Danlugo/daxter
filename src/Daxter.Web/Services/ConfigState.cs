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
    private static string ConfigPath => PersistedSettings.FilePath;

    public string AuthMode { get; set; } = "device-code";
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    public string? PublicClientId { get; set; }
    public string? Workspace { get; set; }
    public string? Dataset { get; set; }
    public string? ProdWorkspaces { get; set; }
    public bool AllowWrites { get; set; }
    public bool AllowModelEdit { get; set; }

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
        PublicClientId = Env("DAXTER_PUBLIC_CLIENT_ID");
        Workspace = Env("DAXTER_WORKSPACE");
        Dataset = Env("DAXTER_DATASET");
        ProdWorkspaces = Env("DAXTER_PROD_WORKSPACES");
        AllowWrites = string.Equals(Env("DAXTER_MCP_ALLOW_WRITES"), "true", StringComparison.OrdinalIgnoreCase);
        AllowModelEdit = string.Equals(Env("DAXTER_MCP_ALLOW_MODEL_EDIT"), "true", StringComparison.OrdinalIgnoreCase);
    }

    private bool LoadPersisted()
    {
        if (!File.Exists(ConfigPath)) return false;
        var s = PersistedSettings.Load();   // shared shape — same file the CLI/MCP read
        AuthMode = s.AuthMode ?? AuthMode;
        TenantId = s.TenantId; ClientId = s.ClientId; ClientSecret = s.ClientSecret;
        Workspace = s.Workspace; Dataset = s.Dataset;
        ProdWorkspaces = s.ProdWorkspaces; AllowWrites = s.AllowWrites; AllowModelEdit = s.AllowModelEdit;
        return true;
    }

    /// <summary>Persists the current values to the mounted volume (the single source the CLI/MCP also read).</summary>
    public string Save()
    {
        new PersistedSettings(AuthMode, TenantId, ClientId, ClientSecret, Workspace, Dataset, ProdWorkspaces, AllowWrites, AllowModelEdit).Save();
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
        PublicClientId = PublicClientId,
        AuthMode = string.Equals(AuthMode, "service-principal", StringComparison.OrdinalIgnoreCase)
            ? Daxter.Core.Auth.AuthMode.ServicePrincipal
            : Daxter.Core.Auth.AuthMode.DeviceCode,
        ProdWorkspaces = string.IsNullOrWhiteSpace(ProdWorkspaces)
            ? Array.Empty<string>()
            : ProdWorkspaces.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
    };
}
