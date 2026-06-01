using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Microsoft.AnalysisServices.AdomdClient;

namespace Daxter.Core.Connection;

/// <summary>
/// Builds and opens a real ADOMD.NET connection to the Power BI XMLA endpoint.
/// The Entra ID token is acquired via the <see cref="ITokenProvider"/> and injected
/// through <c>AdomdConnection.AccessToken</c> — the only auth path supported on macOS.
/// </summary>
public sealed class AdomdXmlaSessionFactory : IXmlaSessionFactory
{
    private readonly DaxterConfig _config;
    private readonly ITokenProvider _tokenProvider;
    private readonly string? _roles;
    private readonly string? _effectiveUserName;

    public AdomdXmlaSessionFactory(
        DaxterConfig config,
        ITokenProvider tokenProvider,
        string? roles = null,
        string? effectiveUserName = null)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _tokenProvider = tokenProvider ?? throw new ArgumentNullException(nameof(tokenProvider));
        _roles = roles;
        _effectiveUserName = effectiveUserName;
    }

    public async Task<IXmlaSession> CreateAsync(CancellationToken cancellationToken = default)
    {
        var connectionString = XmlaConnectionString.Build(
            _config.Workspace, _config.Dataset, _roles, _effectiveUserName);
        var token = await _tokenProvider.GetTokenAsync(cancellationToken);

        // The AdomdConnection ctor parses the connection string and can throw on its own
        // (e.g. a value the OLE-DB parser rejects) — keep it inside the try so the failure is
        // wrapped in a DaxterException and the real reason reaches the caller, not a generic one.
        AdomdConnection? connection = null;
        try
        {
            connection = new AdomdConnection(connectionString)
            {
                // On net8.0 the AccessToken property type lives in Microsoft.AnalysisServices
                // (Runtime.Core); ctor is (token, expiration, userContext).
                AccessToken = new Microsoft.AnalysisServices.AccessToken(
                    token.Token, token.ExpiresOn, userContext: null),
            };
            connection.Open();
        }
        catch (AdomdException ex)
        {
            connection?.Dispose();
            throw new DaxterException(
                $"Could not connect to XMLA endpoint for workspace '{_config.Workspace}'. " +
                "Confirm the workspace is on Premium/PPU/Fabric capacity with the XMLA endpoint " +
                $"enabled, and that the identity has access. Detail: {ex.Message}", ex);
        }
        catch (DaxterException)
        {
            connection?.Dispose();
            throw;
        }
        catch (Exception ex)
        {
            connection?.Dispose();
            throw new DaxterException($"Connection failed: {ex.Message}", ex);
        }

        return new AdomdXmlaSession(connection);
    }
}
