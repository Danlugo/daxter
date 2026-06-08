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

    /// <summary>
    /// Service-principal (confidential) app registration id, used by client-credentials auth.
    /// <b>Not</b> used for the device-code flow — that needs a public client (see
    /// <see cref="PublicClientId"/>), because a confidential app id demands a secret at the
    /// token endpoint (AADSTS7000218).
    /// </summary>
    public string? ClientId { get; init; }

    /// <summary>Client secret. Service-principal mode only. Never hardcode — read from env.</summary>
    public string? ClientSecret { get; init; }

    /// <summary>
    /// Public-client app id for the device-code flow. Optional — defaults to
    /// <see cref="DefaultPublicClientId"/>. Set <c>DAXTER_PUBLIC_CLIENT_ID</c> only to use your
    /// own native/public app registration (with "Allow public client flows" enabled).
    /// </summary>
    public string? PublicClientId { get; init; }

    /// <summary>
    /// Public-client app id used JUST for the Fabric SQL endpoint scope (database.windows.net).
    /// Optional — defaults to <see cref="DefaultFabricSqlClientId"/> (Azure CLI's id, pre-authorized
    /// for that resource). Set <c>DAXTER_SQL_CLIENT_ID</c> to use a tenant app pre-authorized for
    /// BOTH Power BI and Azure SQL — then there's no second sign-in for SQL.
    /// </summary>
    public string? FabricSqlClientId { get; init; }

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
    public const string EnvPublicClientId = "DAXTER_PUBLIC_CLIENT_ID";
    /// <summary>Override for the public client id used JUST for the Fabric SQL endpoint scope
    /// (database.windows.net). See <see cref="DefaultFabricSqlClientId"/>.</summary>
    public const string EnvFabricSqlClientId = "DAXTER_SQL_CLIENT_ID";
    public const string EnvAuthMode = "DAXTER_AUTH_MODE";
    public const string EnvEnvironment = "DAXTER_ENV";
    public const string EnvProdWorkspaces = "DAXTER_PROD_WORKSPACES";

    /// <summary>Microsoft's first-party client id used for delegated Power BI access.</summary>
    public const string DefaultPublicClientId = "ea0616ba-638b-4df5-95b9-636659ae5121";

    /// <summary>Default public client id used JUST for the Fabric SQL endpoint scope. Microsoft's
    /// Power BI Client Integrations app id (<see cref="DefaultPublicClientId"/>) is NOT pre-authorized
    /// for <c>https://database.windows.net</c> — AAD returns AADSTS65002 ("Consent between first
    /// party application X and first party resource Y must be configured via preauthorization").
    /// The Azure CLI's public client id IS pre-authorized for that scope (it's what <c>az login</c>
    /// uses to talk to Azure SQL / Synapse / Fabric SQL endpoints), so we use it as the default for
    /// the SQL surface only. The trade-off: a one-time second device-code sign-in for the SQL scope,
    /// because MSAL refresh tokens are bound to client id — the cached Power BI sign-in can't be
    /// re-used. Override via <c>DAXTER_SQL_CLIENT_ID</c> if you have a tenant app pre-authorized for
    /// both scopes (then sign-in is one-and-done).</summary>
    public const string DefaultFabricSqlClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

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
        string? environment = null,
        bool requireWorkspace = true)
    {
        static string? Env(string key) =>
            string.IsNullOrWhiteSpace(System.Environment.GetEnvironmentVariable(key))
                ? null
                : System.Environment.GetEnvironmentVariable(key);

        static string? Nz(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

        // Single source of truth: settings the web console saved to the shared volume.
        // Precedence everywhere is: explicit arg > volume config (UI) > env var (fallback) > default.
        var saved = PersistedSettings.Load();

        // Active environment (e.g. "dev") enables per-env overrides like
        // DAXTER_WORKSPACE_DEV / DAXTER_DATASET_DEV (one SP, workspace per env).
        var activeEnv = environment ?? Env(EnvEnvironment);

        string? PerEnv(string baseKey) => string.IsNullOrWhiteSpace(activeEnv)
            ? null
            : Env($"{baseKey}_{activeEnv.Trim().ToUpperInvariant()}");

        var resolvedWorkspace = workspace ?? Nz(saved.Workspace) ?? PerEnv(EnvWorkspace) ?? Env(EnvWorkspace);
        if (string.IsNullOrWhiteSpace(resolvedWorkspace))
        {
            if (!requireWorkspace)
            {
                // Tenant-level operations (list workspaces, gateways, sign-in) need no workspace.
                resolvedWorkspace = string.Empty;
            }
            else
            {
                var hint = string.IsNullOrWhiteSpace(activeEnv)
                    ? $"Pass --workspace or set {EnvWorkspace}."
                    : $"Pass --workspace or set {EnvWorkspace}_{activeEnv!.ToUpperInvariant()} (or {EnvWorkspace}).";
                throw new DaxterException($"No workspace configured. {hint}");
            }
        }

        var resolvedAuth = authMode ?? ParseAuthMode(Nz(saved.AuthMode) ?? Env(EnvAuthMode));

        return new DaxterConfig
        {
            Workspace = resolvedWorkspace,
            Dataset = dataset ?? Nz(saved.Dataset) ?? PerEnv(EnvDataset) ?? Env(EnvDataset),
            TenantId = tenantId ?? Nz(saved.TenantId) ?? Env(EnvTenantId),
            ClientId = clientId ?? Nz(saved.ClientId) ?? Env(EnvClientId),
            ClientSecret = clientSecret ?? Nz(saved.ClientSecret) ?? Env(EnvClientSecret),
            PublicClientId = Env(EnvPublicClientId),
            FabricSqlClientId = Env(EnvFabricSqlClientId),
            AuthMode = resolvedAuth,
            Environment = string.IsNullOrWhiteSpace(activeEnv) ? null : activeEnv!.Trim().ToLowerInvariant(),
            ProdWorkspaces = ParseList(Nz(saved.ProdWorkspaces) ?? Env(EnvProdWorkspaces)),
        };
    }

    /// <summary>Returns a copy targeting a different workspace/dataset; all auth settings are preserved.</summary>
    public DaxterConfig With(string workspace, string? dataset) => new()
    {
        Workspace = workspace,
        Dataset = dataset,
        TenantId = TenantId,
        ClientId = ClientId,
        ClientSecret = ClientSecret,
        PublicClientId = PublicClientId,
        FabricSqlClientId = FabricSqlClientId,
        AuthMode = AuthMode,
        Environment = Environment,
        ProdWorkspaces = ProdWorkspaces,
    };

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
