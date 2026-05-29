using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Tom = Microsoft.AnalysisServices.Tabular;

namespace Daxter.Core.Export;

/// <summary>
/// Exports a model's full metadata definition via TOM — the equivalent of "Save as .bim".
/// Connects with the injected Entra ID token (cross-platform, like the XMLA query path).
/// </summary>
public sealed class ModelExportService
{
    private readonly DaxterConfig _config;
    private readonly XmlaAccessToken _token;

    public ModelExportService(DaxterConfig config, XmlaAccessToken token)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _token = token;
    }

    /// <summary>Returns the model definition as a Tabular (.bim) JSON string.</summary>
    public string ExportBim()
    {
        using var server = Connect();
        var database = FindDatabase(server);
        return Tom.JsonSerializer.SerializeDatabase(database);
    }

    private Tom.Server Connect()
    {
        if (string.IsNullOrWhiteSpace(_config.Dataset))
        {
            throw new DaxterException("Export requires a dataset (--dataset or DAXTER_DATASET).");
        }

        var server = new Tom.Server
        {
            AccessToken = new Microsoft.AnalysisServices.AccessToken(_token.Token, _token.ExpiresOn, null),
        };

        try
        {
            server.Connect(XmlaConnectionString.Build(_config.Workspace, null));
        }
        catch (Exception ex)
        {
            server.Dispose();
            throw new DaxterException($"Could not connect for export: {ex.Message}", ex);
        }

        return server;
    }

    private Tom.Database FindDatabase(Tom.Server server)
    {
        var database = server.Databases.FindByName(_config.Dataset);
        return database ?? throw new DaxterException($"Dataset not found in workspace: {_config.Dataset}");
    }
}
