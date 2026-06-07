using System.Net;
using System.Text;
using Daxter.Core.Auth;
using Daxter.Core.Rest;

namespace Daxter.Core.Tests;

/// <summary>Locks in how <see cref="PowerBiRestClient.SqlEndpointsAsync"/> flattens Fabric REST's
/// warehouses + lakehouses lists into one (name, server, database, kind) table — the shape the
/// /sql page and the MCP daxter_sql_endpoints tool consume.</summary>
public class SqlEndpointsDiscoveryTests
{
    private sealed class StubHandler(Func<string, string> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(responder(request.RequestUri!.ToString()), Encoding.UTF8, "application/json"),
            });
    }

    private sealed class FakeToken : ITokenProvider
    {
        public Task<XmlaAccessToken> GetTokenAsync(CancellationToken ct = default)
            => Task.FromResult(new XmlaAccessToken("tok", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private static PowerBiRestClient Client(Func<string, string> responder)
        => new(new FakeToken(), new HttpClient(new StubHandler(responder)));

    [Fact]
    public async Task SqlEndpointsAsync_combines_warehouses_and_lakehouses_with_kind()
    {
        // Two warehouses + one lakehouse. The lakehouse exposes its SQL endpoint via
        // properties.sqlEndpointProperties.connectionString.
        var client = Client(url =>
        {
            if (url.Contains("/warehouses", StringComparison.Ordinal))
            {
                return """
                    {"value":[
                      {"displayName":"WhA","properties":{"connectionInfo":"a.datawarehouse.fabric.microsoft.com"}},
                      {"displayName":"WhB","properties":{"connectionInfo":"b.datawarehouse.fabric.microsoft.com"}}
                    ]}
                    """;
            }
            if (url.Contains("/lakehouses", StringComparison.Ordinal))
            {
                return """
                    {"value":[
                      {"displayName":"LhC","properties":{"sqlEndpointProperties":{"connectionString":"c.datawarehouse.fabric.microsoft.com","provisioningStatus":"Success"}}}
                    ]}
                    """;
            }
            return """{"value":[]}""";
        });

        var result = await client.SqlEndpointsAsync("ws1");

        Assert.Equal(["name", "server", "database", "kind"], result.Columns);
        Assert.Equal(3, result.RowCount);
        // Sort order is alphabetical (Ordinal, case-insensitive) — UI contract requires this on every picker.
        Assert.Equal("LhC", result.Rows[0][0]);
        Assert.Equal("WhA", result.Rows[1][0]);
        Assert.Equal("WhB", result.Rows[2][0]);
        // Kind preserved.
        Assert.Equal("Lakehouse", result.Rows[0][3]);
        Assert.Equal("Warehouse", result.Rows[1][3]);
        Assert.Equal("Warehouse", result.Rows[2][3]);
        // Database = display name (warehouses) and = lakehouse name (lakehouses) — what SqlConnection.InitialCatalog needs.
        Assert.Equal("LhC", result.Rows[0][2]);
        Assert.Equal("WhA", result.Rows[1][2]);
    }

    [Fact]
    public async Task SqlEndpointsAsync_skips_warehouses_with_no_connectionInfo()
    {
        // A freshly-created warehouse hasn't been provisioned yet — connectionInfo is missing. Skip it
        // rather than emit a row the SQL client can't actually connect to.
        var client = Client(url =>
        {
            if (url.Contains("/warehouses", StringComparison.Ordinal))
            {
                return """
                    {"value":[
                      {"displayName":"Ready","properties":{"connectionInfo":"r.datawarehouse.fabric.microsoft.com"}},
                      {"displayName":"NotReady","properties":{}}
                    ]}
                    """;
            }
            return """{"value":[]}""";
        });

        var result = await client.SqlEndpointsAsync("ws1");
        Assert.Single(result.Rows);
        Assert.Equal("Ready", result.Rows[0][0]);
    }

    [Fact]
    public async Task SqlEndpointsAsync_skips_lakehouses_whose_endpoint_is_not_provisioned()
    {
        // A lakehouse whose sqlEndpointProperties.provisioningStatus isn't "Success" can't accept TDS
        // connections — skip rather than fail the whole list.
        var client = Client(url =>
        {
            if (url.Contains("/lakehouses", StringComparison.Ordinal))
            {
                return """
                    {"value":[
                      {"displayName":"Good","properties":{"sqlEndpointProperties":{"connectionString":"g.datawarehouse.fabric.microsoft.com","provisioningStatus":"Success"}}},
                      {"displayName":"Pending","properties":{"sqlEndpointProperties":{"connectionString":"p.datawarehouse.fabric.microsoft.com","provisioningStatus":"InProgress"}}}
                    ]}
                    """;
            }
            return """{"value":[]}""";
        });

        var result = await client.SqlEndpointsAsync("ws1");
        Assert.Single(result.Rows);
        Assert.Equal("Good", result.Rows[0][0]);
    }
}
