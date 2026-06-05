using System.Net;
using System.Text;
using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Rest;

namespace Daxter.Core.Tests;

public class PowerBiRestClientTests
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
    public async Task GroupsAsync_maps_value_array_to_rows()
    {
        var client = Client(_ => """{"value":[{"id":"g1","name":"Sales WS","isOnDedicatedCapacity":true}]}""");
        var result = await client.GroupsAsync();

        Assert.Equal(["id", "name", "isOnDedicatedCapacity"], result.Columns);
        Assert.Equal("g1", result.Rows[0][0]);
        Assert.Equal("Sales WS", result.Rows[0][1]);
        Assert.Equal(true, result.Rows[0][2]);
    }

    [Fact]
    public async Task LineageAsync_resolves_dataset_id_to_name()
    {
        var client = Client(url => url.Contains("/reports", StringComparison.Ordinal)
            ? """{"value":[{"name":"Exec Report","datasetId":"d1"}]}"""
            : """{"value":[{"id":"d1","name":"Sales Model"}]}""");

        var result = await client.LineageAsync("g1");

        Assert.Equal(["Report", "Dataset"], result.Columns);
        Assert.Equal("Exec Report", result.Rows[0][0]);
        Assert.Equal("Sales Model", result.Rows[0][1]); // id -> name
    }

    [Fact]
    public async Task ResolveGroupIdAsync_throws_when_not_found()
    {
        var client = Client(_ => """{"value":[]}""");
        await Assert.ThrowsAsync<DaxterException>(() => client.ResolveGroupIdAsync("Missing"));
    }

    // A GUID workspace is its own group id — no REST round-trip, no name filter needed.
    [Fact]
    public async Task ResolveGroupIdAsync_passes_through_a_guid()
    {
        var id = "11111111-2222-3333-4444-555555555555";
        var called = false;
        var client = Client(_ => { called = true; return """{"value":[]}"""; });

        Assert.Equal(id, await client.ResolveGroupIdAsync(id));
        Assert.False(called); // resolved locally, never hit the API
    }

    // A dataset can be given by name OR by id (GUID); both resolve to the dataset id.
    [Theory]
    [InlineData("Reseller's Margin")]                        // by name (apostrophe — no escaping needed)
    [InlineData("11111111-2222-3333-4444-555555555555")]     // by id
    public async Task ResolveDatasetIdAsync_accepts_name_or_id(string nameOrId)
    {
        var client = Client(_ =>
            """{"value":[{"id":"11111111-2222-3333-4444-555555555555","name":"Reseller's Margin"}]}""");

        Assert.Equal("11111111-2222-3333-4444-555555555555", await client.ResolveDatasetIdAsync("g1", nameOrId));
    }

    // GUID → canonical name, so the XMLA Initial Catalog (which addresses by name) gets the real name.
    [Fact]
    public async Task DatasetNameByIdAsync_returns_the_canonical_name()
    {
        var client = Client(_ =>
            """{"value":[{"id":"11111111-2222-3333-4444-555555555555","name":"Reseller's Margin"}]}""");

        Assert.Equal("Reseller's Margin",
            await client.DatasetNameByIdAsync("g1", "11111111-2222-3333-4444-555555555555"));
    }

    [Fact]
    public async Task GroupNameByIdAsync_returns_the_canonical_name()
    {
        var client = Client(_ => """{"value":[{"id":"g1","name":"Analytics WS"}]}""");
        Assert.Equal("Analytics WS", await client.GroupNameByIdAsync("g1"));
    }

    // ---- take ownership + gateway binding ----

    [Fact]
    public void BindBody_without_ids_is_gateway_only()
        => Assert.Equal(
            "{\"gatewayObjectId\":\"gw1\"}",
            PowerBiRestClient.BuildBindToGatewayBody("gw1", null));

    [Fact]
    public void BindBody_with_empty_list_is_gateway_only()
        => Assert.Equal(
            "{\"gatewayObjectId\":\"gw1\"}",
            PowerBiRestClient.BuildBindToGatewayBody("gw1", new List<string>()));

    [Fact]
    public void BindBody_maps_the_given_connection_ids()
        => Assert.Equal(
            "{\"gatewayObjectId\":\"gw1\",\"datasourceObjectIds\":[\"dc2f\",\"3bfe\"]}",
            PowerBiRestClient.BuildBindToGatewayBody("gw1", ["dc2f", "3bfe"]));

    [Fact]
    public async Task DiscoverGatewaysAsync_maps_value_array()
    {
        var client = Client(_ => """{"value":[{"id":"gw1","name":"VNet GW","type":"VirtualNetwork"}]}""");
        var result = await client.DiscoverGatewaysAsync("g1", "d1");

        Assert.Equal(["id", "name", "type"], result.Columns);
        Assert.Equal("gw1", result.Rows[0][0]);
        Assert.Equal("VNet GW", result.Rows[0][1]);
    }

    [Fact]
    public async Task GatewayDatasourcesAsync_flattens_connection_details()
    {
        var client = Client(_ =>
            """{"value":[{"id":"ds1","datasourceName":"Snowflake Prod","datasourceType":"Extension","connectionDetails":{"server":"acct.snowflakecomputing.com","database":"ANALYTICS_WH"}}]}""");
        var result = await client.GatewayDatasourcesAsync("gw1");

        Assert.Equal(["id", "datasourceName", "datasourceType", "server", "database"], result.Columns);
        Assert.Equal("ds1", result.Rows[0][0]);
        Assert.Equal("Snowflake Prod", result.Rows[0][1]);
        Assert.Equal("acct.snowflakecomputing.com", result.Rows[0][3]);
        Assert.Equal("ANALYTICS_WH", result.Rows[0][4]);
    }

    [Fact]
    public async Task ItemConnectionsAsync_maps_name_connectivity_and_details()
    {
        var client = Client(_ =>
            """{"value":[{"displayName":"RAP Cloud","connectivityType":"ShareableCloud","connectionDetails":{"type":"SQL","path":"host;DB"}},{"displayName":"Snow GW","connectivityType":"VirtualNetworkGateway","gatewayId":"gw-9","connectionDetails":{"type":"Snowflake","path":"acct;WH"}}]}""");
        var result = await client.ItemConnectionsAsync("ws1", "item1");

        Assert.Equal(["displayName", "connectivityType", "type", "path", "gatewayId"], result.Columns);
        // cloud row: name + connectivity + flattened details, no gateway
        Assert.Equal("RAP Cloud", result.Rows[0][0]);
        Assert.Equal("ShareableCloud", result.Rows[0][1]);
        Assert.Equal("SQL", result.Rows[0][2]);
        Assert.Equal("host;DB", result.Rows[0][3]);
        Assert.Null(result.Rows[0][4]);   // cloud connection has no gatewayId
        // gateway row: connectivity + gatewayId carried through
        Assert.Equal("VirtualNetworkGateway", result.Rows[1][1]);
        Assert.Equal("gw-9", result.Rows[1][4]);
    }

    [Fact]
    public async Task ReportInventoryAsync_classifies_thin_thick_and_downloadable()
    {
        var client = Client(url => url.Contains("/datasets", StringComparison.Ordinal)
            ? """{"value":[{"id":"d1","name":"Sales Model"},{"id":"d2","name":"Thick Report"},{"id":"d3","name":"Web Dataset"}]}"""
            : """{"value":[{"id":"r1","name":"Sales Exec","datasetId":"d1","isFromPbix":true,"reportType":"PowerBIReport"},{"id":"r2","name":"Sales Ops","datasetId":"d1","isFromPbix":true,"reportType":"PowerBIReport"},{"id":"r3","name":"Thick Report","datasetId":"d2","isFromPbix":true,"reportType":"PowerBIReport"},{"id":"r4","name":"Web Built","datasetId":"d3","isFromPbix":false,"reportType":"PowerBIReport"}]}""");
        var result = await client.ReportInventoryAsync("g1");

        Assert.Equal(["report", "dataset", "type", "fromPbix", "downloadable", "reason"], result.Columns);
        object?[] Row(string name) => result.Rows.First(r => (string?)r[0] == name);

        // r1 + r2 share dataset d1 → both thin.
        Assert.Equal("thin", Row("Sales Exec")[2]);
        Assert.Equal("thin", Row("Sales Ops")[2]);
        Assert.Equal("yes", Row("Sales Exec")[4]);
        // r3 alone on a same-named dataset → thick (XMLA-edit warning).
        Assert.Equal("thick", Row("Thick Report")[2]);
        Assert.Contains("XMLA", (string)Row("Thick Report")[5]!);
        // r4 not from a .pbix → service-authored, not downloadable.
        Assert.Equal("no (service-authored)", Row("Web Built")[4]);
    }

    [Fact]
    public async Task ConnectionsAsync_maps_rows_and_follows_pagination()
    {
        // First page returns a continuationToken; second page closes it out.
        var client = Client(url => url.Contains("continuationToken", StringComparison.Ordinal)
            ? """{"value":[{"id":"c2","displayName":"On-prem SQL","connectivityType":"OnPremisesGateway","gatewayId":"gw-1","connectionDetails":{"type":"SQL","path":"host;DB"}}]}"""
            : """{"value":[{"id":"c1","displayName":"DEV Databricks","connectivityType":"ShareableCloud","connectionDetails":{"type":"Databricks","path":"{}"}}],"continuationToken":"NEXT"}""");
        var result = await client.ConnectionsAsync();

        Assert.Equal(["id", "displayName", "connectivityType", "type", "path", "gatewayId"], result.Columns);
        Assert.Equal(2, result.RowCount);                 // both pages merged
        Assert.Equal("DEV Databricks", result.Rows[0][1]);
        Assert.Equal("ShareableCloud", result.Rows[0][2]);
        Assert.Equal("On-prem SQL", result.Rows[1][1]);
        Assert.Equal("gw-1", result.Rows[1][5]);
    }

    [Fact]
    public async Task ConnectionsAsync_calls_the_fabric_connections_endpoint()
    {
        string? seen = null;
        var client = Client(url => { seen = url; return """{"value":[]}"""; });
        await client.ConnectionsAsync();

        Assert.NotNull(seen);
        Assert.Contains("api.fabric.microsoft.com/v1/connections", seen);
    }

    [Fact]
    public async Task ItemConnectionsAsync_calls_the_fabric_item_endpoint()
    {
        string? seen = null;
        var client = Client(url => { seen = url; return """{"value":[]}"""; });
        await client.ItemConnectionsAsync("ws1", "item1");

        Assert.NotNull(seen);
        Assert.Contains("api.fabric.microsoft.com", seen);
        Assert.Contains("/workspaces/ws1/items/item1/connections", seen);
    }
}
