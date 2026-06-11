using Daxter.Core.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Daxter.Core.Auth;

/// <summary>
/// A started device-code sign-in: the <paramref name="Message"/> (URL + code) to show the user,
/// a <paramref name="Completion"/> task that completes once they authenticate (token cached) or
/// faults with the failure reason, and the structured <paramref name="VerificationUrl"/> +
/// <paramref name="UserCode"/> so a UI can render a clickable link and a copyable code.
/// (URL/code are <c>null</c> when already signed in.)
/// </summary>
public sealed record DeviceLogin(
    string Message, Task Completion, string? VerificationUrl = null, string? UserCode = null);

/// <summary>
/// Acquires Power BI XMLA tokens via MSAL. Supports the device-code flow
/// (interactive, with a persisted token cache so you sign in once) and the
/// client-credentials flow (service principal). This is the Mac-supported path:
/// ADOMD.NET's built-in interactive login only works on Windows.
/// </summary>
public sealed class MsalTokenProvider : ITokenProvider, IFabricSqlTokenProvider
{
    /// <summary>The resource scope for Power BI / Azure Analysis Services.</summary>
    public static readonly string[] Scopes =
        ["https://analysis.windows.net/powerbi/api/.default"];

    /// <summary>The resource scope for Fabric Warehouse / Lakehouse SQL endpoints (TDS over AAD).
    /// Same MSAL account as <see cref="Scopes"/>; the token is acquired silently from the cache,
    /// so the user signs in once for everything DAXter does.</summary>
    public static readonly string[] FabricSqlScopes =
        ["https://database.windows.net/.default"];

    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    private readonly DaxterConfig _config;
    private readonly Func<DeviceCodeResult, Task> _deviceCodeCallback;
    private readonly Action<string> _statusWriter;   // stderr-style status line (warnings, prompts)
    private readonly string _cacheDirectory;
    private readonly bool _allowInteractive;

    private XmlaAccessToken? _cached;
    // Second-scope cache (Fabric SQL endpoint). Same account, different audience.
    private XmlaAccessToken? _cachedSql;

    public MsalTokenProvider(
        DaxterConfig config,
        Action<string>? deviceCodePrompt = null,
        string? cacheDirectory = null,
        bool allowInteractive = true)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _statusWriter = deviceCodePrompt ?? Console.WriteLine;
        _deviceCodeCallback = result =>
        {
            _statusWriter(result.Message);
            return Task.CompletedTask;
        };
        _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory();
        _allowInteractive = allowInteractive;
    }

    public async Task<XmlaAccessToken> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cached is { } token && !token.IsExpired(ExpirySkew))
        {
            return token;
        }

        _config.Validate();

        try
        {
            var result = _config.AuthMode == AuthMode.ServicePrincipal
                ? await AcquireForServicePrincipalAsync(Scopes, cancellationToken)
                : await AcquireForUserAsync(Scopes, cancellationToken);

            var acquired = new XmlaAccessToken(result.AccessToken, result.ExpiresOn);
            _cached = acquired;
            return acquired;
        }
        catch (MsalException ex)
        {
            throw new DaxterException(
                $"Authentication failed ({ex.ErrorCode}): {ex.Message}", ex);
        }
    }

    /// <summary>Returns a valid token for the <b>Fabric SQL endpoint</b> scope
    /// (<c>https://database.windows.net/.default</c>) — used by <c>SqlConnection.AccessToken</c>
    /// to query a Warehouse or Lakehouse SQL endpoint over TDS. Reuses the same MSAL account that
    /// the XMLA/REST surface already signed in (silent from the cache); if the cache is empty the
    /// device-code prompt runs the same way the XMLA path does. Service-principal mode goes through
    /// AcquireTokenForClient with the SQL scope.</summary>
    public async Task<XmlaAccessToken> GetFabricSqlTokenAsync(CancellationToken cancellationToken = default)
    {
        if (_cachedSql is { } token && !token.IsExpired(ExpirySkew))
        {
            return token;
        }

        _config.Validate();

        try
        {
            var result = _config.AuthMode == AuthMode.ServicePrincipal
                ? await AcquireForServicePrincipalAsync(FabricSqlScopes, cancellationToken)
                : await AcquireForUserAsync(FabricSqlScopes, cancellationToken);

            var acquired = new XmlaAccessToken(result.AccessToken, result.ExpiresOn);
            _cachedSql = acquired;
            return acquired;
        }
        catch (MsalException ex)
        {
            throw new DaxterException(
                $"Fabric SQL authentication failed ({ex.ErrorCode}): {ex.Message}", ex);
        }
    }

    private async Task<AuthenticationResult> AcquireForServicePrincipalAsync(string[] scopes, CancellationToken ct)
    {
        var app = ConfidentialClientApplicationBuilder
            .Create(_config.ClientId)
            .WithClientSecret(_config.ClientSecret)
            .WithAuthority(AuthorityFor(_config.TenantId))
            .Build();

        return await app.AcquireTokenForClient(scopes).ExecuteAsync(ct);
    }

    private async Task<AuthenticationResult> AcquireForUserAsync(string[] scopes, CancellationToken ct)
    {
        var isSqlScope = scopes == FabricSqlScopes;
        var app = await BuildUserAppAsync(isSqlScope);

        // Try the cache first so a previously signed-in user isn't re-prompted. MSAL refresh tokens
        // are bound to (client_id, account), so the SQL-scope app sees its own cache slice — separate
        // from the Power BI one. Microsoft's Power BI public client id is NOT pre-authorized for
        // database.windows.net (AADSTS65002), so the SQL surface uses the Azure CLI's public client
        // id by default (see DaxterConfig.DefaultFabricSqlClientId). Trade-off: one extra device-code
        // sign-in the first time you query SQL.
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        if (account is not null)
        {
            try
            {
                return await app.AcquireTokenSilent(scopes, account).ExecuteAsync(ct);
            }
            catch (MsalUiRequiredException)
            {
                // Fall through.
            }
        }

        if (!_allowInteractive)
        {
            // MCP/headless: don't block on a device-code prompt the user can't see.
            var which = isSqlScope ? "Fabric SQL endpoints" : "Power BI";
            throw new DaxterException(
                $"Not signed in to {which}. Use the daxter_login tool to sign in (or `daxter login`)" +
                (isSqlScope ? " — SQL needs a separate one-time sign-in for the database.windows.net scope." : "."));
        }

        return await app
            .AcquireTokenWithDeviceCode(scopes, _deviceCodeCallback)
            .ExecuteAsync(ct);
    }

    /// <summary>
    /// Starts the device-code flow and returns the sign-in message (URL + code)
    /// <b>immediately</b>; the token is acquired and cached in the background once the user
    /// authenticates. For the in-chat <c>daxter_login</c> tool, so the code can be shown
    /// without the call blocking. Returns "Already signed in." if a cached token is valid.
    /// </summary>
    public async Task<string> BeginInteractiveLoginAsync(CancellationToken cancellationToken = default)
        => (await StartDeviceLoginAsync(cancellationToken)).Message;

    /// <summary>
    /// Starts the device-code flow and returns both the sign-in <see cref="DeviceLogin.Message"/>
    /// (URL + code, available immediately) and a <see cref="DeviceLogin.Completion"/> task that
    /// finishes when the user has authenticated (the token is then cached) or faults with the
    /// reason sign-in failed. Lets a UI show the code, then await completion to auto-refresh or
    /// surface the failure — instead of silently swallowing background errors.
    /// </summary>
    public Task<DeviceLogin> StartDeviceLoginAsync(CancellationToken cancellationToken = default)
        => StartDeviceLoginInternalAsync(Scopes, forFabricSql: false, cancellationToken);

    /// <summary>Like <see cref="StartDeviceLoginAsync"/> but for the Fabric SQL endpoint scope
    /// (database.windows.net) — uses the SQL-side client id (Azure CLI's by default; override via
    /// DAXTER_SQL_CLIENT_ID). Returns "Already signed in." if a usable cached token exists for that
    /// client id. The Power BI sign-in does NOT cover this one — MSAL refresh tokens are bound to
    /// client_id, so the user signs in once for Power BI and once for SQL.</summary>
    public Task<DeviceLogin> StartFabricSqlDeviceLoginAsync(CancellationToken cancellationToken = default)
        => StartDeviceLoginInternalAsync(FabricSqlScopes, forFabricSql: true, cancellationToken);

    private async Task<DeviceLogin> StartDeviceLoginInternalAsync(
        string[] scopes, bool forFabricSql, CancellationToken cancellationToken)
    {
        if (_config.AuthMode == AuthMode.ServicePrincipal)
        {
            throw new DaxterException(
                "This server uses a service principal — no interactive sign-in is needed.");
        }

        var app = await BuildUserAppAsync(forFabricSql);
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        if (account is not null)
        {
            try
            {
                await app.AcquireTokenSilent(scopes, account).ExecuteAsync(cancellationToken);
                return new DeviceLogin("Already signed in.", Task.CompletedTask);
            }
            catch (MsalUiRequiredException)
            {
                // Need a fresh sign-in.
            }
        }

        var promptReady = new TaskCompletionSource<DeviceCodeResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var completion = Task.Run(async () =>
        {
            try
            {
                await app.AcquireTokenWithDeviceCode(scopes, result =>
                {
                    promptReady.TrySetResult(result);
                    return Task.CompletedTask;
                }).ExecuteAsync(CancellationToken.None);
            }
            catch (MsalException ex)
            {
                var wrapped = new DaxterException($"Sign-in failed ({ex.ErrorCode}): {ex.Message}", ex);
                promptReady.TrySetException(wrapped);
                throw wrapped;
            }
            catch (Exception ex)
            {
                promptReady.TrySetException(ex);
                throw;
            }
        }, CancellationToken.None);

        var prompt = await promptReady.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        return new DeviceLogin(prompt.Message, completion, prompt.VerificationUrl, prompt.UserCode);
    }

    private async Task<IPublicClientApplication> BuildUserAppAsync(bool forFabricSql = false)
    {
        // Device-code requires a PUBLIC client. Never use _config.ClientId here — that is the
        // service-principal (confidential) app id and the token endpoint would reject the
        // public device-code request with AADSTS7000218 ("must contain client_secret").
        // Two faces:
        //   - Power BI / XMLA / Fabric REST: uses DefaultPublicClientId (Power BI Client Integrations).
        //   - Fabric SQL endpoint (database.windows.net): uses DefaultFabricSqlClientId (Azure CLI's
        //     id, pre-authorized for that resource). The Power BI id is NOT pre-authorized for the
        //     SQL audience, so AAD returns AADSTS65002 if we try. Override via DAXTER_SQL_CLIENT_ID.
        var clientId = forFabricSql
            ? (string.IsNullOrWhiteSpace(_config.FabricSqlClientId)
                ? DaxterConfig.DefaultFabricSqlClientId
                : _config.FabricSqlClientId)
            : (string.IsNullOrWhiteSpace(_config.PublicClientId)
                ? DaxterConfig.DefaultPublicClientId
                : _config.PublicClientId);

        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AuthorityFor(_config.TenantId ?? "organizations"))
            .WithDefaultRedirectUri()
            .Build();

        await TryAttachPersistentCacheAsync(app);
        return app;
    }

    /// <summary>Tracks whether we've already emitted the "unprotected cache" warning this process,
    /// so it appears once at startup rather than on every token acquisition.</summary>
    private static int _warnedUnprotected;

    private async Task TryAttachPersistentCacheAsync(IPublicClientApplication app)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);

            // v1.41.0 — ENCRYPT AT REST when DAXTER_CACHE_KEY is set. The MSAL cache helper falls
            // back to an UNPROTECTED file on the Linux container (no keyring), leaving AAD refresh
            // tokens in plaintext on the volume. When a key is supplied (Semantix/hosted set it
            // per container from their secrets store) we wrap the cache in AES-256-GCM ourselves
            // via SetBeforeAccess/SetAfterAccess, so the volume alone can't yield the tokens.
            var encryptedPath = Path.Combine(_cacheDirectory, "msal_cache.enc");
            var encrypted = EncryptedTokenCache.FromEnv(encryptedPath);
            if (encrypted is not null)
            {
                encrypted.Attach(app.UserTokenCache);
                return;
            }

            // No key → keep the platform default: macOS keychain / Windows DPAPI (both already
            // encrypted), Linux unprotected file. Warn ONCE on the unprotected path so the
            // operator knows to set DAXTER_CACHE_KEY (or treat the volume as a secret store).
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS()
                && Interlocked.Exchange(ref _warnedUnprotected, 1) == 0)
            {
                _statusWriter(
                    "DAXter: the token cache is stored UNENCRYPTED on this platform. Set " +
                    "DAXTER_CACHE_KEY to encrypt it at rest, and treat the ~/.daxter volume as a secret store.");
            }

            var properties = new StorageCreationPropertiesBuilder("msal_cache.bin", _cacheDirectory)
                .WithMacKeyChain("com.daxter.tokencache", "MSALCache")
                .WithLinuxUnprotectedFile()
                .Build();

            var helper = await MsalCacheHelper.CreateAsync(properties);
            helper.RegisterCache(app.UserTokenCache);
        }
        catch
        {
            // Best-effort: fall back to the in-memory cache for this process.
        }
    }

    private static string AuthorityFor(string? tenant)
        => $"https://login.microsoftonline.com/{tenant ?? "organizations"}";

    private static string DefaultCacheDirectory()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".daxter");
}
