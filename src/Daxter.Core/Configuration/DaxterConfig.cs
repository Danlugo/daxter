using Daxter.Core.Auth;

namespace Daxter.Core.Configuration;

/// <summary>
/// Resolved connection + auth settings for a DAXter session. Values come from
/// environment variables (see <see cref="FromEnvironment"/>) and may be
/// overridden by CLI options.
/// </summary>
public sealed class DaxterConfig
{
    /// <summary>
    /// Workspace name or full XMLA data source. A bare name such as
    /// <c>"Sales Analytics"</c> is expanded to
    /// <c>powerbi://api.powerbi.com/v1.0/myorg/Sales Analytics</c>.
    /// </summary>
    public required string Workspace { get; init; }

    /// <summary>Dataset / semantic model name (XMLA <c>Initial Catalog</c>). Optional.</summary>
    public string? Dataset { get; init; }

    /// <summary>Entra ID tenant id (GUID) or domain. Required for service principal.</summary>
    public string? TenantId { get; init; }

    /// <summary>App registration (client) id used by MSAL.</summary>
    public string? ClientId { get; init; }

    /// <summary>Client secret. Service-principal mode only. Never hardcode — read from env.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>Authentication mode. Defaults to <see cref="AuthMode.DeviceCode"/>.</summary>
    public AuthMode AuthMode { get; init; } = AuthMode.DeviceCode;

    /// <summary>Active environment name (e.g. "dev"), if one was selected.</summary>
    public string? Environment { get; init; }

    /// <summary>
    /// Workspace names explicitly designated production (from <c>DAXTER_PROD_WORKSPACES</c>).
    /// Used by the write-safety guard for tenants whose prod workspaces aren't named "...prod".
    /// </summary>
    public IReadOnlyCollection<string> ProdWorkspaces { get; init; } = [];

    // Environment variable names (single source of truth).
    public const string EnvWorkspace = "DAXTER_WORKSPACE";
    public const string EnvDataset = "DAXTER_DATASET";
    public const string EnvTenantId = "DAXTER_TENANT_ID";
    public const string EnvClientId = "DAXTER_CLIENT_ID";
    public const string EnvClientSecret = "DAXTER_CLIENT_SECRET";
    public const string EnvAuthMode = "DAXTER_AUTH_MODE";
    public const string EnvEnvironment = "DAXTER_ENV";
    public const string EnvProdWorkspaces = "DAXTER_PROD_WORKSPACES";

    /// <summary>Microsoft's first-party client id used for delegated Power BI access.</summary>
    public const string DefaultPublicClientId = "ea0616ba-638b-4df5-95b9-636659ae5121";

    /// <summary>
    /// Builds a config from environment variables. CLI option values, when present,
    /// take precedence over the environment.
    /// </summary>
    public static DaxterConfig FromEnvironment(
        string? workspace = null,
        string? dataset = null,
        string? tenantId = null,
        string? clientId = null,
        string? clientSecret = null,
        AuthMode? authMode = null,
        string? environment = null)
    {
        static string? Env(string key) =>
            string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(key))
                ? null
                : System.Environment.GetEnvironmentVariable(key);

        // Active environment (e.g. "dev") enables per-env overrides like
        // DAXTER_WORKSPACE_DEV / DAXTER_DATASET_DEV (one SP, workspace per env).
        var activeEnv = environment ?? Env(EnvEnvironment);

        string? PerEnv(string baseKey) => string.IsNullOrWhiteSpace(activeEnv)
            ? null
            : Env($"{baseKey}_{activeEnv.Trim().ToUpperInvariant()}");

        var resolvedWorkspace = workspace ?? PerEnv(EnvWorkspace) ?? Env(EnvWorkspace);
        if (string.IsNullOrWhiteSpace(resolvedWorkspace))
        {
            var hint = string.IsNullOrWhiteSpace(activeEnv)
                ? $"Pass --workspace or set {EnvWorkspace}."
                : $"Pass --workspace or set {EnvWorkspace}_{activeEnv!.ToUpperInvariant()} (or {EnvWorkspace}).";
            throw new DaxterException($"No workspace configured. {hint}");
        }

        var resolvedAuth = authMode ?? ParseAuthMode(Env(EnvAuthMode));

        return new DaxterConfig
        {
            Workspace = resolvedWorkspace,
            Dataset = dataset ?? PerEnv(EnvDataset) ?? Env(EnvDataset),
            TenantId = tenantId ?? Env(EnvTenantId),
            ClientId = clientId ?? Env(EnvClientId),
            ClientSecret = clientSecret ?? Env(EnvClientSecret),
            AuthMode = resolvedAuth,
            Environment = string.IsNullOrWhiteSpace(activeEnv) ? null : activeEnv!.Trim().ToLowerInvariant(),
            ProdWorkspaces = ParseList(Env(EnvProdWorkspaces)),
        };
    }

    /// <summary>
    /// True if the current target is production — used to block writes. Production is
    /// detected by the active env being "prod", the workspace name containing "prod", or
    /// the workspace appearing in <c>DAXTER_PROD_WORKSPACES</c> (for unsuffixed prod names).
    /// </summary>
    public bool IsProductionTarget()
    {
        if (string.Equals(Environment, "prod", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (Workspace.Contains("prod", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var current = Workspace.Trim();
        return ProdWorkspaces.Any(w => string.Equals(w, current, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyCollection<string> ParseList(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? []
            : value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Lists configured environment names by scanning for <c>DAXTER_WORKSPACE_&lt;ENV&gt;</c>
    /// variables. Used by <c>daxter env ls</c>.
    /// </summary>
    public static IReadOnlyList<string> ListEnvironments()
    {
        var prefix = EnvWorkspace + "_";
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            var key = entry.Key.ToString() ?? string.Empty;
            if (key.StartsWith(prefix, StringComparison.Ordinal) && key.Length > prefix.Length)
            {
                names.Add(key[prefix.Length..].ToLowerInvariant());
            }
        }

        return names.ToList();
    }

    /// <summary>Validates that the fields required by the chosen <see cref="AuthMode"/> are present.</summary>
    public void Validate()
    {
        if (AuthMode == AuthMode.ServicePrincipal)
        {
            var missing = new List<string>();
            if (string.IsNullOrWhiteSpace(TenantId)) missing.Add(EnvTenantId);
            if (string.IsNullOrWhiteSpace(ClientId)) missing.Add(EnvClientId);
            if (string.IsNullOrWhiteSpace(ClientSecret)) missing.Add(EnvClientSecret);
            if (missing.Count > 0)
            {
                throw new DaxterException(
                    "Service-principal auth requires: " + string.Join(", ", missing) + ".");
            }
        }
    }

    private static AuthMode ParseAuthMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "devicecode" or "device-code" or "device" or "interactive" => AuthMode.DeviceCode,
        "serviceprincipal" or "service-principal" or "sp" or "app" => AuthMode.ServicePrincipal,
        _ => throw new DaxterException(
            $"Unknown {EnvAuthMode} value '{value}'. Use 'device-code' or 'service-principal'."),
    };
}
