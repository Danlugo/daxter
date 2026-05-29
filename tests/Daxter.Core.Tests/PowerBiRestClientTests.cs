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
}
