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
}
