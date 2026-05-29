namespace Daxter.Core.Auth;

/// <summary>Acquires an Entra ID access token for the Power BI XMLA scope.</summary>
public interface ITokenProvider
{
    /// <summary>
    /// Returns a valid token, acquiring or refreshing it as needed. Implementations
    /// may prompt the user (device code) or run silently (service principal / cache).
    /// </summary>
    Task<XmlaAccessToken> GetTokenAsync(CancellationToken cancellationToken = default);
}
