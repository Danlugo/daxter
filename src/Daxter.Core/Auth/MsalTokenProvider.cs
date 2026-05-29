using Daxter.Core.Configuration;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Extensions.Msal;

namespace Daxter.Core.Auth;

/// <summary>
/// Acquires Power BI XMLA tokens via MSAL. Supports the device-code flow
/// (interactive, with a persisted token cache so you sign in once) and the
/// client-credentials flow (service principal). This is the Mac-supported path:
/// ADOMD.NET's built-in interactive login only works on Windows.
/// </summary>
public sealed class MsalTokenProvider : ITokenProvider
{
    /// <summary>The resource scope for Power BI / Azure Analysis Services.</summary>
    public static readonly string[] Scopes =
        ["https://analysis.windows.net/powerbi/api/.default"];

    private static readonly TimeSpan ExpirySkew = TimeSpan.FromMinutes(5);

    private readonly DaxterConfig _config;
    private readonly Func<DeviceCodeResult, Task> _deviceCodeCallback;
    private readonly string _cacheDirectory;

    private XmlaAccessToken? _cached;

    public MsalTokenProvider(
        DaxterConfig config,
        Action<string>? deviceCodePrompt = null,
        string? cacheDirectory = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _deviceCodeCallback = result =>
        {
            (deviceCodePrompt ?? Console.WriteLine)(result.Message);
            return Task.CompletedTask;
        };
        _cacheDirectory = cacheDirectory ?? DefaultCacheDirectory();
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
                ? await AcquireForServicePrincipalAsync(cancellationToken)
                : await AcquireForUserAsync(cancellationToken);

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

    private async Task<AuthenticationResult> AcquireForServicePrincipalAsync(CancellationToken ct)
    {
        var app = ConfidentialClientApplicationBuilder
            .Create(_config.ClientId)
            .WithClientSecret(_config.ClientSecret)
            .WithAuthority(AuthorityFor(_config.TenantId))
            .Build();

        return await app.AcquireTokenForClient(Scopes).ExecuteAsync(ct);
    }

    private async Task<AuthenticationResult> AcquireForUserAsync(CancellationToken ct)
    {
        var clientId = string.IsNullOrWhiteSpace(_config.ClientId)
            ? DaxterConfig.DefaultPublicClientId
            : _config.ClientId;

        var app = PublicClientApplicationBuilder
            .Create(clientId)
            .WithAuthority(AuthorityFor(_config.TenantId ?? "organizations"))
            .WithDefaultRedirectUri()
            .Build();

        await TryAttachPersistentCacheAsync(app);

        // Try the cache first so a previously signed-in user isn't re-prompted.
        var account = (await app.GetAccountsAsync()).FirstOrDefault();
        if (account is not null)
        {
            try
            {
                return await app.AcquireTokenSilent(Scopes, account).ExecuteAsync(ct);
            }
            catch (MsalUiRequiredException)
            {
                // Fall through to interactive device code.
            }
        }

        return await app
            .AcquireTokenWithDeviceCode(Scopes, _deviceCodeCallback)
            .ExecuteAsync(ct);
    }

    private async Task TryAttachPersistentCacheAsync(IPublicClientApplication app)
    {
        try
        {
            Directory.CreateDirectory(_cacheDirectory);
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
