using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Formatting;
using Daxter.Core.Metadata;
using Daxter.Core.Query;
using Daxter.Core.Rest;

namespace Daxter.Cli.Mcp;

/// <summary>
/// Bridges MCP tools to the Daxter.Core engine. Builds config from the server's
/// environment (overridable per call), runs the operation, and returns capped JSON
/// so a large result set can't blow the model's context.
/// </summary>
internal static class DaxterToolRuntime
{
    private const int RowCap = 1000;
    private static readonly JsonResultFormatter Json = new();

    public static async Task<string> XmlaAsync(
        string? workspace, string? dataset, Func<IXmlaSession, QueryResult> op, CancellationToken ct)
    {
        var config = Config(workspace, dataset);
        var factory = new AdomdXmlaSessionFactory(config, Provider(config));
        using var session = await factory.CreateAsync(ct);
        return Format(op(session));
    }

    public static Task<string> MetaAsync(
        string? workspace, string? dataset, Func<ModelMetadataService, QueryResult> op, CancellationToken ct)
        => XmlaAsync(workspace, dataset, session => op(new ModelMetadataService(session)), ct);

    public static async Task<string> RestAsync(
        string? workspace, string? dataset,
        Func<PowerBiRestClient, DaxterConfig, CancellationToken, Task<QueryResult>> op, CancellationToken ct)
    {
        var config = Config(workspace, dataset);
        using var rest = new PowerBiRestClient(Provider(config));
        return Format(await op(rest, config, ct));
    }

    public static async Task<string> DiffMeasuresAsync(
        string? workspace, string? dataset, string other, CancellationToken ct)
    {
        var config = Config(workspace, dataset);
        var left = new AdomdXmlaSessionFactory(config, Provider(config));
        var rightConfig = WithDataset(config, other);
        var right = new AdomdXmlaSessionFactory(rightConfig, Provider(rightConfig));

        using var leftSession = await left.CreateAsync(ct);
        using var rightSession = await right.CreateAsync(ct);
        return Format(ModelDiffService.DiffMeasures(leftSession, rightSession));
    }

    private static DaxterConfig Config(string? workspace, string? dataset)
        => DaxterConfig.FromEnvironment(workspace: workspace, dataset: dataset);

    private static ITokenProvider Provider(DaxterConfig config)
        // Device-code prompt → stderr so it never corrupts the stdio JSON-RPC channel.
        => new MsalTokenProvider(config, deviceCodePrompt: Console.Error.WriteLine);

    private static DaxterConfig WithDataset(DaxterConfig c, string dataset) => new()
    {
        Workspace = c.Workspace,
        Dataset = dataset,
        TenantId = c.TenantId,
        ClientId = c.ClientId,
        ClientSecret = c.ClientSecret,
        AuthMode = c.AuthMode,
        Environment = c.Environment,
    };

    private static string Format(QueryResult result)
    {
        if (result.RowCount <= RowCap)
        {
            return Json.Format(result);
        }

        var capped = new QueryResult(result.Columns, result.Rows.Take(RowCap).ToList());
        return Json.Format(capped) + $"\n/* truncated: showing {RowCap} of {result.RowCount} rows */";
    }
}
