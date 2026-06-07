namespace Daxter.Core.Auth;

/// <summary>Acquires an Entra ID access token for the Fabric SQL endpoint scope
/// (<c>https://database.windows.net/.default</c>) — the audience that Fabric Warehouse and
/// Lakehouse SQL analytics endpoints accept on the <c>SqlConnection.AccessToken</c> property.
/// Implemented by <see cref="MsalTokenProvider"/>, which reuses the same MSAL account as the
/// XMLA/REST surface so the user signs in only once.</summary>
public interface IFabricSqlTokenProvider
{
    /// <summary>Returns a valid token for the Fabric SQL endpoint scope, acquiring or refreshing
    /// it as needed. May prompt (device code) if no account is cached and interactive sign-in is
    /// allowed; otherwise throws telling the caller to log in first.</summary>
    Task<XmlaAccessToken> GetFabricSqlTokenAsync(CancellationToken cancellationToken = default);
}
