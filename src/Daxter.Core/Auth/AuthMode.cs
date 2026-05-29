namespace Daxter.Core.Auth;

/// <summary>
/// How DAXter authenticates to the Power BI / Analysis Services XMLA endpoint.
/// Interactive browser login is Windows-only in ADOMD.NET, so on macOS both
/// supported modes acquire an Entra ID token via MSAL and inject it through
/// <c>AdomdConnection.AccessToken</c>.
/// </summary>
public enum AuthMode
{
    /// <summary>Interactive user sign-in via the OAuth device-code flow (token cached).</summary>
    DeviceCode,

    /// <summary>Non-interactive app sign-in via client credentials (service principal).</summary>
    ServicePrincipal,
}
