using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Daxter.Core.Auth;
using Daxter.Core.Query;

namespace Daxter.Core.Rest;

/// <summary>One file of a report's definition: its relative <paramref name="Path"/> (e.g.
/// <c>report.json</c> or <c>definition/pages/&lt;id&gt;/visuals/&lt;id&gt;/visual.json</c>) and decoded text
/// <paramref name="Content"/> (empty for binary parts).</summary>
public sealed record ReportPart(string Path, string Content);

/// <summary>
/// Thin client over the Power BI REST API (<c>https://api.powerbi.com/v1.0/myorg/</c>).
/// Uses the same Entra ID token as XMLA. Responses are mapped to <see cref="QueryResult"/>
/// for uniform formatting. Reused by the Workspace and Pipeline modules.
/// </summary>
public sealed class PowerBiRestClient : IDisposable
{
    private const string BaseUrl = "https://api.powerbi.com/v1.0/myorg/";

    private readonly HttpClient _http;
    private readonly ITokenProvider _tokens;
    private readonly bool _ownsHttp;

    public PowerBiRestClient(ITokenProvider tokens, HttpClient? http = null)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _http = http ?? new HttpClient();
        _ownsHttp = http is null;
    }

    // ---- workspaces / datasets ----

    public async Task<QueryResult> GroupsAsync(CancellationToken ct = default)
        => ToTable(await GetAsync("groups", ct), "id", "name", "isOnDedicatedCapacity");

    public async Task<QueryResult> DatasetsAsync(string groupId, CancellationToken ct = default)
        => ToTable(await GetAsync($"groups/{groupId}/datasets", ct), "id", "name", "configuredBy");

    /// <summary>Resolves a workspace given either its name or its id (a GUID is its own group id).</summary>
    public async Task<string> ResolveGroupIdAsync(string workspaceNameOrId, CancellationToken ct = default)
    {
        if (Guid.TryParse(workspaceNameOrId.Trim(), out _))
        {
            return workspaceNameOrId.Trim();
        }

        var root = await GetAsync($"groups?$filter=name eq '{Odata(workspaceNameOrId)}'", ct);
        var id = FirstValue(root, "id");
        return id ?? throw new DaxterException($"Workspace not found via REST: {workspaceNameOrId}");
    }

    /// <summary>Resolves a dataset id given either its name or its id (a GUID), within a workspace.</summary>
    public async Task<string> ResolveDatasetIdAsync(string groupId, string datasetNameOrId, CancellationToken ct = default)
    {
        var needle = datasetNameOrId.Trim();
        var root = await GetAsync($"groups/{groupId}/datasets", ct);
        foreach (var item in Value(root).EnumerateArray())
        {
            var byName = item.TryGetProperty("name", out var n) && string.Equals(n.GetString(), needle, StringComparison.OrdinalIgnoreCase);
            var byId = item.TryGetProperty("id", out var i) && string.Equals(i.GetString(), needle, StringComparison.OrdinalIgnoreCase);
            if (byName || byId)
            {
                return item.GetProperty("id").GetString()!;
            }
        }

        throw new DaxterException($"Dataset not found in workspace: {datasetNameOrId}");
    }

    /// <summary>Canonical workspace name for an id (used to build the XMLA endpoint, which addresses by name).</summary>
    public async Task<string> GroupNameByIdAsync(string groupId, CancellationToken ct = default)
    {
        var root = await GetAsync($"groups?$filter=id eq '{Odata(groupId)}'", ct);
        return FirstValue(root, "name") ?? throw new DaxterException($"Workspace id not found via REST: {groupId}");
    }

    /// <summary>Canonical dataset name for an id within a workspace (XMLA Initial Catalog addresses by name).</summary>
    public async Task<string> DatasetNameByIdAsync(string groupId, string datasetId, CancellationToken ct = default)
    {
        var needle = datasetId.Trim();
        var root = await GetAsync($"groups/{groupId}/datasets", ct);
        foreach (var item in Value(root).EnumerateArray())
        {
            if (item.TryGetProperty("id", out var i) && string.Equals(i.GetString(), needle, StringComparison.OrdinalIgnoreCase)
                && item.TryGetProperty("name", out var n))
            {
                return n.GetString() ?? throw new DaxterException($"Dataset id has no name: {datasetId}");
            }
        }

        throw new DaxterException($"Dataset id not found in workspace: {datasetId}");
    }

    public async Task<QueryResult> ReportsAsync(string groupId, CancellationToken ct = default)
        => ToTable(await GetAsync($"groups/{groupId}/reports", ct), "id", "name", "datasetId");

    /// <summary>Classifies every report in a workspace as <b>thin</b> (decoupled from its model — a shared
    /// dataset), <b>thick</b> (embeds its own model — XMLA-editing that model permanently blocks future
    /// <c>.pbix</c> download), or <b>paginated</b>, and whether it's <b>downloadable</b> as a <c>.pbix</c>
    /// (<c>isFromPbix</c> — service-authored reports can't be exported). Signals come straight from the
    /// <c>/reports</c> response (<c>datasetWorkspaceId</c>, <c>isFromPbix</c>, <c>reportType</c>) plus the
    /// reports-per-dataset fan-out. Thin + downloadable reports are the safe ones to pull for analysis.</summary>
    public async Task<QueryResult> ReportInventoryAsync(string groupId, CancellationToken ct = default)
    {
        var datasetsJson = await GetAsync($"groups/{groupId}/datasets", ct);
        var dsName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in Value(datasetsJson).EnumerateArray())
            if (d.TryGetProperty("id", out var id) && id.GetString() is { } k)
                dsName[k] = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

        var reportsJson = await GetAsync($"groups/{groupId}/reports", ct);
        var reports = Value(reportsJson).EnumerateArray().ToList();

        // Reports-per-dataset fan-out: a dataset backing >1 report is shared (its reports are thin).
        var perDataset = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var r in reports)
            if (Str(r, "datasetId") is { } did && did.Length > 0)
                perDataset[did] = perDataset.GetValueOrDefault(did) + 1;

        var rows = new List<object?[]>();
        foreach (var r in reports)
        {
            var name = Str(r, "name") ?? "";
            var datasetId = Str(r, "datasetId") ?? "";
            var datasetWs = Str(r, "datasetWorkspaceId") ?? "";
            var fromPbix = r.TryGetProperty("isFromPbix", out var fp) && fp.ValueKind == JsonValueKind.True;
            var reportType = Str(r, "reportType") ?? "";
            var datasetNm = dsName.TryGetValue(datasetId, out var nm) ? nm : datasetId;

            var crossWs = datasetWs.Length > 0 && !string.Equals(datasetWs, groupId, StringComparison.OrdinalIgnoreCase);
            var shared = perDataset.GetValueOrDefault(datasetId) > 1 || crossWs;

            string type;
            if (string.Equals(reportType, "PaginatedReport", StringComparison.OrdinalIgnoreCase)) type = "paginated";
            else if (shared) type = "thin";
            else if (string.Equals(name, datasetNm, StringComparison.OrdinalIgnoreCase)) type = "thick";
            else type = "thin";

            var downloadable = type == "paginated" ? "no (paginated)"
                : fromPbix ? "yes" : "no (service-authored)";
            var reason = type switch
            {
                "thin" => crossWs ? "shared dataset (other workspace)"
                          : perDataset.GetValueOrDefault(datasetId) > 1 ? "shared dataset"
                          : "decoupled from model",
                "thick" => "embeds its model — XMLA edits block future .pbix",
                _ => "",
            };
            rows.Add([name, datasetNm, type, fromPbix ? "yes" : "no", downloadable, reason]);
        }
        rows.Sort((a, b) => string.Compare(a[0]?.ToString(), b[0]?.ToString(), StringComparison.OrdinalIgnoreCase));
        return new QueryResult(["report", "dataset", "type", "fromPbix", "downloadable", "reason"], rows);
    }

    /// <summary>Resolves a report NAME (or GUID) to its id within a workspace.</summary>
    public async Task<string> ResolveReportIdAsync(string groupId, string reportNameOrId, CancellationToken ct = default)
    {
        if (Guid.TryParse(reportNameOrId.Trim(), out _)) return reportNameOrId.Trim();
        var root = await GetAsync($"groups/{groupId}/reports", ct);
        foreach (var r in Value(root).EnumerateArray())
            if (string.Equals(Str(r, "name"), reportNameOrId, StringComparison.OrdinalIgnoreCase)
                && Str(r, "id") is { } id)
                return id;
        throw new DaxterException($"Report not found in workspace: {reportNameOrId}");
    }

    /// <summary>Fetches a report's <b>definition</b> from the Fabric API (PBIR or PBIR-Legacy) — the
    /// per-visual JSON that carries every field reference, the substrate for column-usage analysis.
    /// Needs only report read/write. Handles the long-running-operation (202 + poll) path. Base64 parts
    /// are decoded to text; binary parts come back with empty content.</summary>
    public async Task<IReadOnlyList<ReportPart>> ReportDefinitionAsync(string workspaceId, string reportId, CancellationToken ct = default)
    {
        var url = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/reports/{reportId}/getDefinition";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);

        JsonElement root;
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            root = await PollOperationResultAsync(response, ct);
        }
        else
        {
            await EnsureSuccessAsync(response, ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            root = doc.RootElement.Clone();
        }

        var parts = new List<ReportPart>();
        if (root.TryGetProperty("definition", out var def) && def.TryGetProperty("parts", out var partsEl)
            && partsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in partsEl.EnumerateArray())
            {
                var path = Str(p, "path") ?? "";
                var payload = Str(p, "payload") ?? "";
                var type = Str(p, "payloadType") ?? "";
                var content = "";
                if (string.Equals(type, "InlineBase64", StringComparison.OrdinalIgnoreCase) && payload.Length > 0)
                {
                    try { content = Encoding.UTF8.GetString(Convert.FromBase64String(payload)); }
                    catch { content = ""; }   // binary part (e.g. an image) — leave empty
                }
                parts.Add(new ReportPart(path, content));
            }
        }
        return parts;
    }

    /// <summary>Downloads a report as a <c>.pbix</c> via the Power BI <i>Export Report In Group</i> API.
    /// Fails (surfaced as an error) for service-authored reports or models edited over XMLA — that's the
    /// engine refusing, not a bug. Returns the raw <c>.pbix</c> bytes.</summary>
    public async Task<byte[]> ExportReportPbixAsync(string groupId, string reportId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + $"groups/{groupId}/reports/{reportId}/Export");
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsByteArrayAsync(ct);
    }

    /// <summary>Polls a Fabric long-running operation (from a 202 response) to completion, then returns
    /// the operation result body.</summary>
    private async Task<JsonElement> PollOperationResultAsync(HttpResponseMessage accepted, CancellationToken ct)
    {
        var opUrl = accepted.Headers.Location?.ToString()
            ?? (accepted.Headers.TryGetValues("Operation-Location", out var v) ? v.FirstOrDefault() : null)
            ?? throw new DaxterException("Long-running operation returned no Location header.");
        var retry = accepted.Headers.RetryAfter?.Delta is { } d && d > TimeSpan.Zero ? d : TimeSpan.FromSeconds(2);

        for (var i = 0; i < 90; i++)
        {
            await Task.Delay(retry, ct);
            using var poll = new HttpRequestMessage(HttpMethod.Get, opUrl);
            await Authorize(poll, ct);
            using var pr = await _http.SendAsync(poll, ct);
            await EnsureSuccessAsync(pr, ct);
            using var doc = JsonDocument.Parse(await pr.Content.ReadAsStringAsync(ct));
            var status = Str(doc.RootElement, "status") ?? "";
            if (string.Equals(status, "Succeeded", StringComparison.OrdinalIgnoreCase))
            {
                using var resReq = new HttpRequestMessage(HttpMethod.Get, opUrl.TrimEnd('/') + "/result");
                await Authorize(resReq, ct);
                using var res = await _http.SendAsync(resReq, ct);
                await EnsureSuccessAsync(res, ct);
                using var resDoc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
                return resDoc.RootElement.Clone();
            }
            if (string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase))
                throw new DaxterException("getDefinition operation failed on the service.");
        }
        throw new DaxterException("getDefinition timed out waiting for the operation to complete.");
    }

    /// <summary>Report → dataset lineage (dataset ids resolved to names).</summary>
    public async Task<QueryResult> LineageAsync(string groupId, CancellationToken ct = default)
    {
        var datasets = await GetAsync($"groups/{groupId}/datasets", ct);
        var idToName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var d in Value(datasets).EnumerateArray())
        {
            if (d.TryGetProperty("id", out var id) && id.GetString() is { } key)
            {
                idToName[key] = d.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
            }
        }

        var reports = await GetAsync($"groups/{groupId}/reports", ct);
        var rows = new List<object?[]>();
        foreach (var r in Value(reports).EnumerateArray())
        {
            var reportName = r.TryGetProperty("name", out var n) ? n.GetString() : null;
            var datasetId = r.TryGetProperty("datasetId", out var d) ? d.GetString() : null;
            var datasetName = datasetId is not null && idToName.TryGetValue(datasetId, out var nm) ? nm : datasetId;
            rows.Add([reportName, datasetName]);
        }

        return new QueryResult(["Report", "Dataset"], rows);
    }

    public async Task<QueryResult> WorkspaceUsersAsync(string groupId, CancellationToken ct = default)
        => ToTable(await GetAsync($"groups/{groupId}/users", ct),
            "displayName", "emailAddress", "groupUserAccessRight", "principalType");

    public async Task<QueryResult> DatasetUsersAsync(string groupId, string datasetId, CancellationToken ct = default)
        => ToTable(await GetAsync($"groups/{groupId}/datasets/{datasetId}/users", ct),
            "identifier", "principalType", "datasetUserAccessRight");

    public async Task<QueryResult> GatewaysAsync(CancellationToken ct = default)
        => ToTable(await GetAsync("gateways", ct), "id", "name", "type");

    /// <summary>All connections the calling identity can access from the Fabric API — display name +
    /// connectivity type (cloud / on-prem / VNet gateway) + details. Paginated via continuationToken.
    /// Use to list the shareable <em>cloud</em> connections (the cloud half of the Service's "Gateway and
    /// cloud connections" screen), independent of any one model.</summary>
    public async Task<QueryResult> ConnectionsAsync(CancellationToken ct = default)
    {
        string[] columns = ["id", "displayName", "connectivityType", "type", "path", "gatewayId"];
        var rows = new List<object?[]>();
        string? token = null;
        do
        {
            var url = "https://api.fabric.microsoft.com/v1/connections";
            if (!string.IsNullOrEmpty(token)) url += "?continuationToken=" + Uri.EscapeDataString(token);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var root = await SendJsonAsync(request, ct);
            foreach (var item in Value(root).EnumerateArray())
            {
                JsonElement cd = item.TryGetProperty("connectionDetails", out var c) && c.ValueKind == JsonValueKind.Object
                    ? c : default;
                rows.Add(
                [
                    Str(item, "id"),
                    Str(item, "displayName"),
                    Str(item, "connectivityType"),
                    Str(cd, "type"),
                    Str(cd, "path"),
                    Str(item, "gatewayId"),
                ]);
            }
            token = root.TryGetProperty("continuationToken", out var t) && t.ValueKind == JsonValueKind.String
                ? t.GetString() : null;
        } while (!string.IsNullOrEmpty(token));
        return new QueryResult(columns, rows);
    }

    /// <summary>A model's current connections from the Fabric API — display name + connectivity type
    /// (cloud / on-prem / VNet gateway) + the underlying connection details — using only model
    /// read/write (no gateway-admin), so it can name bindings to gateways the caller can't manage.
    /// <paramref name="itemId"/> is the semantic-model (dataset) id; <paramref name="workspaceId"/> the group id.</summary>
    public async Task<QueryResult> ItemConnectionsAsync(string workspaceId, string itemId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get,
            $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/items/{itemId}/connections");
        var root = await SendJsonAsync(request, ct);
        string[] columns = ["displayName", "connectivityType", "type", "path", "gatewayId"];
        var rows = new List<object?[]>();
        foreach (var item in Value(root).EnumerateArray())
        {
            JsonElement cd = item.TryGetProperty("connectionDetails", out var c) && c.ValueKind == JsonValueKind.Object
                ? c : default;
            rows.Add(
            [
                Str(item, "displayName"),
                Str(item, "connectivityType"),
                Str(cd, "type"),
                Str(cd, "path"),
                Str(item, "gatewayId"),
            ]);
        }
        return new QueryResult(columns, rows);
    }

    // ---- take ownership + gateway binding (service-level config; XMLA can't do these) ----

    /// <summary>Takes over ownership of a dataset in a workspace (the "owner left" flow) — required
    /// before you can rebind its gateway or set credentials. POST Default.TakeOver, no body.</summary>
    public async Task TakeOverAsync(string groupId, string datasetId, CancellationToken ct = default)
        => await PostAsync($"groups/{groupId}/datasets/{datasetId}/Default.TakeOver", null, ct);

    /// <summary>Gateways the dataset can be bound to (those with matching data sources).</summary>
    public async Task<QueryResult> DiscoverGatewaysAsync(string groupId, string datasetId, CancellationToken ct = default)
        => ToTable(await GetAsync($"groups/{groupId}/datasets/{datasetId}/Default.DiscoverGateways", ct),
            "id", "name", "type");

    /// <summary>The data sources defined on a gateway — their object ids are what BindToGateway maps to.</summary>
    public async Task<QueryResult> GatewayDatasourcesAsync(string gatewayId, CancellationToken ct = default)
    {
        var root = await GetAsync($"gateways/{gatewayId}/datasources", ct);
        string[] columns = ["id", "datasourceName", "datasourceType", "server", "database"];
        var rows = new List<object?[]>();
        foreach (var item in Value(root).EnumerateArray())
        {
            JsonElement details = item.TryGetProperty("connectionDetails", out var cd) && cd.ValueKind == JsonValueKind.Object
                ? cd : default;
            rows.Add(
            [
                Str(item, "id"),
                Str(item, "datasourceName"),
                Str(item, "datasourceType"),
                Str(details, "server"),
                Str(details, "database"),
            ]);
        }
        return new QueryResult(columns, rows);
    }

    /// <summary>Binds the dataset to a gateway, optionally mapping its sources to specific gateway
    /// data-source/connection ids. With no ids, binds to the first matching data source per source.
    /// (Public REST supports the gateway/VNet binding; shareable-cloud-connection "Maps to" is UI-only.)</summary>
    public async Task BindToGatewayAsync(string groupId, string datasetId, string gatewayObjectId,
        IReadOnlyList<string>? datasourceObjectIds, CancellationToken ct = default)
        => await PostAsync($"groups/{groupId}/datasets/{datasetId}/Default.BindToGateway",
            BuildBindToGatewayBody(gatewayObjectId, datasourceObjectIds), ct);

    /// <summary>The BindToGateway request body. <c>datasourceObjectIds</c> is omitted when none are given
    /// (then the service binds the first matching data source per source). Ids are GUIDs from the API, so
    /// direct interpolation is safe (matches the manual-JSON pattern, e.g. TriggerRefreshAsync).</summary>
    public static string BuildBindToGatewayBody(string gatewayObjectId, IReadOnlyList<string>? datasourceObjectIds)
    {
        var ids = datasourceObjectIds is { Count: > 0 }
            ? ",\"datasourceObjectIds\":[" + string.Join(",", datasourceObjectIds.Select(id => $"\"{id}\"")) + "]"
            : "";
        return $"{{\"gatewayObjectId\":\"{gatewayObjectId}\"{ids}}}";
    }

    // ---- deployment pipelines (the dev/qa/prod stages and their workspaces) ----

    public async Task<QueryResult> PipelinesAsync(CancellationToken ct = default)
        => ToTable(await GetAsync("pipelines", ct), "id", "displayName");

    public async Task<QueryResult> PipelineStagesAsync(string pipelineId, CancellationToken ct = default)
        => ToTable(await GetAsync($"pipelines/{pipelineId}/stages", ct), "order", "workspaceId", "workspaceName");

    public async Task<QueryResult> PipelineOperationsAsync(string pipelineId, CancellationToken ct = default)
        => ToTable(await GetAsync($"pipelines/{pipelineId}/operations", ct),
            "id", "type", "status", "executionStartTime");

    public async Task<QueryResult> DatasourcesAsync(string groupId, string datasetId, CancellationToken ct = default)
    {
        var root = await GetAsync($"groups/{groupId}/datasets/{datasetId}/datasources", ct);
        string[] columns = ["datasourceType", "server", "database", "path", "url", "gatewayId"];
        var rows = new List<object?[]>();

        foreach (var item in Value(root).EnumerateArray())
        {
            JsonElement details = item.TryGetProperty("connectionDetails", out var cd)
                && cd.ValueKind == JsonValueKind.Object ? cd : default;

            rows.Add(
            [
                Str(item, "datasourceType"),
                Str(details, "server"),
                Str(details, "database"),
                Str(details, "path"),
                Str(details, "url"),
                Str(item, "gatewayId"),
            ]);
        }

        return new QueryResult(columns, rows);
    }

    private static string? Str(JsonElement element, string property)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(property, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    // ---- refresh ----

    public async Task<QueryResult> RefreshHistoryAsync(string groupId, string datasetId, int top = 10, CancellationToken ct = default)
        => ToTable(
            await GetAsync($"groups/{groupId}/datasets/{datasetId}/refreshes?$top={top}", ct),
            "refreshType", "startTime", "endTime", "status");

    public async Task TriggerRefreshAsync(string groupId, string datasetId, CancellationToken ct = default)
        => await PostAsync(
            $"groups/{groupId}/datasets/{datasetId}/refreshes",
            "{\"notifyOption\":\"NoNotification\"}", ct);

    // ---- HTTP plumbing ----

    private async Task<JsonElement> GetAsync(string relative, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + relative);
        return await SendJsonAsync(request, ct);
    }

    private async Task PostAsync(string relative, string? json, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + relative);
        await Authorize(request, ct);
        if (json is not null)
        {
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        }

        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
    }

    private async Task<JsonElement> SendJsonAsync(HttpRequestMessage request, CancellationToken ct)
    {
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    private async Task Authorize(HttpRequestMessage request, CancellationToken ct)
    {
        var token = await _tokens.GetTokenAsync(ct);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(ct);
        var detail = body.Length > 300 ? body[..300] + "…" : body;
        throw new DaxterException($"Power BI REST {(int)response.StatusCode} {response.ReasonPhrase}: {detail}");
    }

    // ---- JSON → QueryResult ----

    private static JsonElement Value(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty("value", out var v) ? v : root;

    private static QueryResult ToTable(JsonElement root, params string[] columns)
    {
        var array = Value(root);
        if (array.ValueKind != JsonValueKind.Array)
        {
            return new QueryResult(columns, []);
        }

        var rows = new List<object?[]>();
        foreach (var item in array.EnumerateArray())
        {
            var row = new object?[columns.Length];
            for (var i = 0; i < columns.Length; i++)
            {
                row[i] = item.TryGetProperty(columns[i], out var prop) ? FromJson(prop) : null;
            }

            rows.Add(row);
        }

        return new QueryResult(columns, rows);
    }

    private static string? FirstValue(JsonElement root, string property)
    {
        var array = Value(root);
        if (array.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in array.EnumerateArray())
            {
                if (item.TryGetProperty(property, out var p))
                {
                    return p.GetString();
                }
            }
        }

        return null;
    }

    private static object? FromJson(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.GetRawText(),
    };

    private static string Odata(string value) => value.Replace("'", "''", StringComparison.Ordinal);

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
