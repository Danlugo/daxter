using System.Net;
using System.Text;
using Daxter.Core.Auth;
using Daxter.Core.Rest;

namespace Daxter.Core.Tests;

/// <summary>Locks in the wire-shape for the Fabric Copy Jobs + Notebooks REST surface — list, get
/// definition (Base64), list job instances (durations, failure reasons), start job (Location header
/// → instance id), get job-instance (typed). If Microsoft ever rotates a field name, these tests
/// fail fast with a clean diff.</summary>
public class FabricItemsRestTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(responder(request));
    }

    private sealed class FakeToken : ITokenProvider
    {
        public Task<XmlaAccessToken> GetTokenAsync(CancellationToken ct = default)
            => Task.FromResult(new XmlaAccessToken("tok", DateTimeOffset.UtcNow.AddHours(1)));
    }

    private static PowerBiRestClient Client(Func<HttpRequestMessage, HttpResponseMessage> responder)
        => new(new FakeToken(), new HttpClient(new StubHandler(responder)));

    private static HttpResponseMessage Ok(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    [Fact]
    public async Task CopyJobsAsync_maps_value_array_to_alphabetical_rows()
    {
        var client = Client(_ => Ok(
            "{\"value\":[" +
            "{\"id\":\"id-z\",\"displayName\":\"Zeta load\",\"description\":\"last in alpha order\"}," +
            "{\"id\":\"id-a\",\"displayName\":\"Alpha load\",\"description\":\"first\"}" +
            "]}"));

        var r = await client.CopyJobsAsync("ws");
        Assert.Equal(["id", "displayName", "description"], r.Columns);
        Assert.Equal(2, r.RowCount);
        // Alphabetical sort by displayName (Ordinal, case-insensitive) — same UI-contract rule.
        Assert.Equal("Alpha load", r.Rows[0][1]);
        Assert.Equal("Zeta load", r.Rows[1][1]);
    }

    [Fact]
    public async Task NotebooksAsync_uses_the_notebooks_endpoint()
    {
        string? capturedUrl = null;
        var client = Client(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return Ok("{\"value\":[{\"id\":\"n1\",\"displayName\":\"Hello\",\"description\":\"\"}]}");
        });

        await client.NotebooksAsync("ws-123");
        Assert.NotNull(capturedUrl);
        Assert.Contains("/v1/workspaces/ws-123/notebooks", capturedUrl);
    }

    [Fact]
    public async Task CopyJobDefinitionAsync_base64_decodes_inline_part()
    {
        var inner = "{\"properties\":{\"mode\":\"Batch\"},\"activities\":[]}";
        var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(inner));

        var client = Client(_ => Ok(
            "{\"definition\":{\"parts\":[" +
            "{\"path\":\"copyjob-content.json\",\"payload\":\"" + b64 + "\",\"payloadType\":\"InlineBase64\"}" +
            "]}}"));

        var parts = await client.CopyJobDefinitionAsync("ws", "cj1");
        Assert.Single(parts);
        Assert.Equal("copyjob-content.json", parts[0].Path);
        Assert.Equal(inner, parts[0].Content);
    }

    [Fact]
    public async Task ListItemJobInstancesAsync_computes_duration_seconds()
    {
        var client = Client(_ => Ok(
            "{\"value\":[{" +
            "\"id\":\"run-1\"," +
            "\"status\":\"Completed\"," +
            "\"invokeType\":\"Manual\"," +
            "\"startTimeUtc\":\"2026-06-08T12:00:00Z\"," +
            "\"endTimeUtc\":\"2026-06-08T12:00:42Z\"," +
            "\"failureReason\":null" +
            "}]}"));

        var r = await client.ListItemJobInstancesAsync("ws", "item-1");
        Assert.Equal(["instanceId", "status", "invokeType", "startTimeUtc", "endTimeUtc", "durationSec", "failureReason"], r.Columns);
        Assert.Equal(1, r.RowCount);
        Assert.Equal("run-1", r.Rows[0][0]);
        Assert.Equal("Completed", r.Rows[0][1]);
        // Duration: 42 seconds — must come out culture-invariant as a double.
        Assert.Equal(42.0, (double)r.Rows[0][5]!, 0.001);
    }

    [Fact]
    public async Task StartItemJobAsync_extracts_instance_id_from_location_header()
    {
        // The Fabric API returns 202 with Location pointing at the new instance — last URL segment
        // is the instance id. This is the contract we depend on for the run path.
        var client = Client(req =>
        {
            var rsp = new HttpResponseMessage(HttpStatusCode.Accepted);
            rsp.Headers.Location = new Uri("https://api.fabric.microsoft.com/v1/workspaces/ws/items/item-1/jobs/instances/the-instance-id");
            return rsp;
        });

        var instanceId = await client.StartItemJobAsync("ws", "item-1", "Execute");
        Assert.Equal("the-instance-id", instanceId);
    }

    [Fact]
    public async Task StartItemJobAsync_falls_back_to_body_when_no_location_header()
    {
        var client = Client(_ => Ok("{\"id\":\"body-id\",\"status\":\"NotStarted\"}"));
        var instanceId = await client.StartItemJobAsync("ws", "item-1", "RunNotebook");
        Assert.Equal("body-id", instanceId);
    }

    [Fact]
    public async Task GetItemJobInstanceAsync_returns_typed_record()
    {
        var client = Client(_ => Ok(
            "{" +
            "\"id\":\"run-1\"," +
            "\"itemId\":\"item-1\"," +
            "\"jobType\":\"Execute\"," +
            "\"invokeType\":\"Manual\"," +
            "\"status\":\"Completed\"," +
            "\"startTimeUtc\":\"2026-06-08T12:00:00Z\"," +
            "\"endTimeUtc\":\"2026-06-08T12:00:30Z\"," +
            "\"failureReason\":null" +
            "}"));

        var inst = await client.GetItemJobInstanceAsync("ws", "item-1", "run-1");
        Assert.Equal("run-1", inst.Id);
        Assert.Equal("Execute", inst.JobType);
        Assert.Equal("Completed", inst.Status);
        Assert.Equal(new DateTimeOffset(2026, 6, 8, 12, 0, 0, TimeSpan.Zero), inst.StartTimeUtc);
        Assert.Null(inst.FailureReason);
    }

    [Fact]
    public async Task GetItemJobInstanceAsync_extracts_error_message_when_failed()
    {
        // Newer Fabric LROs return the failure reason nested under "error" rather than the legacy
        // top-level "failureReason" string. Both shapes must surface cleanly.
        var client = Client(_ => Ok(
            "{" +
            "\"id\":\"run-2\"," +
            "\"status\":\"Failed\"," +
            "\"error\":{\"code\":\"DataMovementFailed\",\"message\":\"Source connection refused\"}" +
            "}"));
        var inst = await client.GetItemJobInstanceAsync("ws", "item-1", "run-2");
        Assert.Equal("Failed", inst.Status);
        Assert.Equal("Source connection refused", inst.FailureReason);
    }

    // ── UpdateItemDefinitionAsync — Phase 3, the Fabric write-back ────────────────────────────
    // Pins the wire-shape: POST URL for each item kind, body shape (parts → InlineBase64),
    // and the empty-parts refusal. The 202+poll path isn't covered here — that's exercised
    // by the existing GetItemDefinitionAsync tests (same PollOperationResultAsync helper).

    [Fact]
    public async Task UpdateItemDefinitionAsync_posts_to_reports_endpoint_for_report_kind()
    {
        string? capturedUrl = null;
        string? capturedBody = null;
        var client = Client(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);    // 200 short-circuits the poll
        });

        var parts = new[]
        {
            new FabricItemPart("report.json", "{\"x\":1}"),
            new FabricItemPart("definition/pages/01/visual.json", "{\"y\":2}"),
        };
        await client.UpdateItemDefinitionAsync("ws-7", "rpt-9",
            PowerBiRestClient.FabricItemKinds.Report, parts);

        Assert.NotNull(capturedUrl);
        Assert.Contains("/v1/workspaces/ws-7/reports/rpt-9/updateDefinition", capturedUrl);
        Assert.NotNull(capturedBody);
        var b64first = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"x\":1}"));
        Assert.Contains($"\"path\":\"report.json\",\"payload\":\"{b64first}\",\"payloadType\":\"InlineBase64\"", capturedBody);
        var b64second = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"y\":2}"));
        Assert.Contains("\"path\":\"definition/pages/01/visual.json\"", capturedBody);
        Assert.Contains(b64second, capturedBody);
    }

    [Fact]
    public async Task UpdateItemDefinitionAsync_uses_notebooks_url_for_notebook_kind()
    {
        string? capturedUrl = null;
        var client = Client(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await client.UpdateItemDefinitionAsync("ws-7", "nb-1",
            PowerBiRestClient.FabricItemKinds.Notebook,
            new[] { new FabricItemPart("notebook-content.py", "print('hi')") });
        Assert.Contains("/v1/workspaces/ws-7/notebooks/nb-1/updateDefinition", capturedUrl);
    }

    [Fact]
    public async Task UpdateItemDefinitionAsync_uses_copyJobs_url_for_copy_job_kind()
    {
        string? capturedUrl = null;
        var client = Client(req =>
        {
            capturedUrl = req.RequestUri!.ToString();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await client.UpdateItemDefinitionAsync("ws-7", "cj-1",
            PowerBiRestClient.FabricItemKinds.CopyJob,
            new[] { new FabricItemPart("copyjob-content.json", "{}") });
        Assert.Contains("/v1/workspaces/ws-7/copyJobs/cj-1/updateDefinition", capturedUrl);
    }

    [Fact]
    public async Task UpdateItemDefinitionAsync_refuses_empty_parts_list()
    {
        // Catches the "agent extracted into the wrong prefix" footgun before we POST a nonsense
        // body to Fabric. The runtime tool turns this into a clean "run extract first" message.
        var client = Client(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await Assert.ThrowsAsync<Daxter.Core.DaxterException>(() =>
            client.UpdateItemDefinitionAsync("ws", "rpt",
                PowerBiRestClient.FabricItemKinds.Report,
                Array.Empty<FabricItemPart>()));
    }

    [Fact]
    public async Task UpdateItemDefinitionAsync_escapes_paths_containing_quotes_in_json()
    {
        // Paths theoretically can carry unusual chars — the JSON serializer escapes them; if we
        // accidentally use string concat instead, the body becomes malformed and Fabric rejects.
        string? capturedBody = null;
        var client = Client(req =>
        {
            capturedBody = req.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        await client.UpdateItemDefinitionAsync("ws", "rpt",
            PowerBiRestClient.FabricItemKinds.Report,
            new[] { new FabricItemPart("weird\"path/file.json", "{}") });
        Assert.NotNull(capturedBody);
        using var doc = System.Text.Json.JsonDocument.Parse(capturedBody!);
        var first = doc.RootElement.GetProperty("definition").GetProperty("parts")[0];
        Assert.Equal("weird\"path/file.json", first.GetProperty("path").GetString());
    }
}
