using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Daxter.Core.Auth;
using Daxter.Core.Query;

namespace Daxter.Core.Rest;

/// <summary>One object's status within an enhanced refresh (a table or a specific partition).</summary>
public sealed record RefreshObjectStatus(string Table, string Partition, string Status);

/// <summary>The status of an enhanced-refresh operation: overall <paramref name="Status"/>
/// (NotStarted | InProgress | Completed | Failed | Cancelled | Unknown), the per-object/per-partition
/// statuses, and an error message when failed.</summary>
public sealed record EnhancedRefreshStatus(string Status, IReadOnlyList<RefreshObjectStatus> Objects, string? Error);

/// <summary>One file of a report's definition: its relative <paramref name="Path"/> (e.g.
/// <c>report.json</c> or <c>definition/pages/&lt;id&gt;/visuals/&lt;id&gt;/visual.json</c>) and decoded text
/// <paramref name="Content"/> (empty for binary parts).</summary>
public sealed record ReportPart(string Path, string Content);

/// <summary>One file of a Fabric item's definition (returned by <c>getDefinition</c>) — a path like
/// <c>copyjob-content.json</c> / <c>notebook-content.py</c> / <c>artifact.content.ipynb</c> /
/// <c>.platform</c> plus the decoded text content. Binary parts come back with empty content.</summary>
public sealed record FabricItemPart(string Path, string Content);

/// <summary>A Fabric item-job run instance — what <c>POST /items/{id}/jobs/instances</c> creates and
/// <c>GET .../jobs/instances/{instanceId}</c> reports on. <paramref name="Status"/> is one of
/// <c>NotStarted | InProgress | Completed | Failed | Cancelled | Unknown</c>; <paramref name="JobType"/>
/// echoes what the caller asked for (<c>Execute</c> for Copy Job, <c>RunNotebook</c> for Notebook).
/// <paramref name="FailureReason"/> is populated when the run failed.</summary>
public sealed record FabricJobInstance(
    string Id, string ItemId, string JobType, string InvokeType,
    string Status, DateTimeOffset? StartTimeUtc, DateTimeOffset? EndTimeUtc, string? FailureReason);

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

    /// <summary>Binds (or unbinds) a SINGLE semantic-model data source to a connection via the Fabric
    /// <i>Bind Semantic Model Connection</i> API. Supports ALL connectivity types — including
    /// <c>ShareableCloud</c> (the cloud "Maps to") and per-source gateway binding — superseding the
    /// model-level <see cref="BindToGatewayAsync"/>. The caller must OWN the model. One source per call;
    /// the source is identified by <paramref name="sourceType"/> + <paramref name="sourcePath"/>
    /// (e.g. <c>SQL</c> + <c>server;database</c>). Pass <c>connectivityType="None"</c> with no
    /// <paramref name="connectionId"/> to unbind (revert to default SSO).</summary>
    public async Task BindConnectionAsync(string workspaceId, string semanticModelId,
        string? connectionId, string connectivityType, string sourceType, string sourcePath, CancellationToken ct = default)
    {
        var binding = new Dictionary<string, object?>
        {
            ["connectivityType"] = connectivityType,
            ["connectionDetails"] = new { type = sourceType, path = sourcePath },
        };
        if (!string.IsNullOrWhiteSpace(connectionId)) binding["id"] = connectionId;
        var body = JsonSerializer.Serialize(new { connectionBinding = binding });

        var url = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/semanticModels/{semanticModelId}/bindConnection";
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);   // 200/202 both succeed
    }

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

    /// <summary>All Fabric SQL endpoints in a workspace — every Warehouse and every Lakehouse's SQL
    /// analytics endpoint — flattened into one table the picker can show as
    /// "&lt;name&gt; (Warehouse | Lakehouse)". Read-only; the caller needs only workspace read.
    /// Columns: <c>name, server, database, kind</c>. The server is the fully-qualified TDS hostname
    /// (e.g. <c>&lt;ws&gt;.datawarehouse.fabric.microsoft.com</c>); the database is what
    /// <c>SqlConnection.InitialCatalog</c> targets — the warehouse display name for warehouses, the
    /// lakehouse name for lakehouse SQL endpoints.</summary>
    public async Task<QueryResult> SqlEndpointsAsync(string workspaceId, CancellationToken ct = default)
    {
        string[] columns = ["name", "server", "database", "kind"];
        var rows = new List<object?[]>();

        // Warehouses: properties.connectionInfo carries the TDS hostname.
        var whUrl = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/warehouses";
        using (var req = new HttpRequestMessage(HttpMethod.Get, whUrl))
        {
            var root = await SendJsonAsync(req, ct);
            foreach (var item in Value(root).EnumerateArray())
            {
                var props = item.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                var name = Str(item, "displayName") ?? "";
                var server = Str(props, "connectionInfo") ?? "";
                if (string.IsNullOrEmpty(server)) continue;   // not provisioned yet
                rows.Add([name, server, name, "Warehouse"]);
            }
        }

        // Lakehouses: each lakehouse exposes a SQL analytics endpoint under
        // properties.sqlEndpointProperties.{connectionString, id, provisioningStatus}. The database
        // name on that endpoint is the lakehouse name itself.
        var lhUrl = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/lakehouses";
        using (var req = new HttpRequestMessage(HttpMethod.Get, lhUrl))
        {
            var root = await SendJsonAsync(req, ct);
            foreach (var item in Value(root).EnumerateArray())
            {
                var props = item.TryGetProperty("properties", out var p) && p.ValueKind == JsonValueKind.Object ? p : default;
                var sep = props.ValueKind == JsonValueKind.Object && props.TryGetProperty("sqlEndpointProperties", out var s)
                    && s.ValueKind == JsonValueKind.Object ? s : default;
                var name = Str(item, "displayName") ?? "";
                var server = Str(sep, "connectionString") ?? "";
                var status = Str(sep, "provisioningStatus") ?? "";
                if (string.IsNullOrEmpty(server)) continue;
                if (!string.IsNullOrEmpty(status) && !string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
                    continue;   // not yet usable — skip rather than fail the whole list
                rows.Add([name, server, name, "Lakehouse"]);
            }
        }

        // Sort alphabetically — same convention every DAXter picker uses (see UI contract).
        rows.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a[0]?.ToString(), b[0]?.ToString()));
        return new QueryResult(columns, rows);
    }

    // ---- Fabric items: Copy Jobs + Notebooks (list, definition, run, status, cancel) ----

    /// <summary>All Copy Jobs in a workspace — id, displayName, description. Columns:
    /// <c>id, displayName, description</c>. Read-only Fabric REST.</summary>
    public async Task<QueryResult> CopyJobsAsync(string workspaceId, CancellationToken ct = default)
        => await FetchFabricItemsAsync(
            $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/copyJobs", ct);

    /// <summary>All Notebooks in a workspace — id, displayName, description. Columns:
    /// <c>id, displayName, description</c>. Read-only Fabric REST.</summary>
    public async Task<QueryResult> NotebooksAsync(string workspaceId, CancellationToken ct = default)
        => await FetchFabricItemsAsync(
            $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/notebooks", ct);

    /// <summary>Fetches a Copy Job's full <c>copyjob-content.json</c> definition (and any platform
    /// metadata parts) — the source/destination/connection/mapping payload. Handles the 202 +
    /// long-running-operation poll the Fabric API uses for getDefinition. Base64 parts decoded;
    /// binary parts come back with empty content.</summary>
    public Task<IReadOnlyList<FabricItemPart>> CopyJobDefinitionAsync(string workspaceId, string copyJobId, CancellationToken ct = default)
        => GetItemDefinitionAsync(
            $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/copyJobs/{copyJobId}/getDefinition", ct);

    /// <summary>Fetches a Notebook's definition — the cells as <c>artifact.content.ipynb</c>
    /// (or <c>notebook-content.py</c> in <c>FabricGitSource</c> format) plus the <c>.platform</c>
    /// metadata. Pass <paramref name="format"/> = "ipynb" to force the standard Jupyter format
    /// (otherwise the service returns the language-specific source file).</summary>
    public Task<IReadOnlyList<FabricItemPart>> NotebookDefinitionAsync(
        string workspaceId, string notebookId, string? format = null, CancellationToken ct = default)
    {
        var url = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/notebooks/{notebookId}/getDefinition";
        if (!string.IsNullOrEmpty(format)) url += $"?format={Uri.EscapeDataString(format)}";
        return GetItemDefinitionAsync(url, ct);
    }

    /// <summary>Starts an on-demand item job — <c>POST .../items/{itemId}/jobs/instances?jobType=…</c>.
    /// <paramref name="jobType"/> is <c>Execute</c> for Copy Jobs, <c>RunNotebook</c> for Notebooks
    /// (and <c>DefaultJob</c> for pipelines). The API returns 202 with a <c>Location</c> header
    /// pointing at the new instance — we extract and return the instance id so the caller can poll
    /// it. Pass <paramref name="executionData"/> JSON for notebook parameter/session config
    /// (e.g. <c>{"parameters":{"x":{"value":"…","type":"string"}}}</c>).</summary>
    public async Task<string> StartItemJobAsync(
        string workspaceId, string itemId, string jobType,
        string? executionData = null, CancellationToken ct = default)
    {
        var url = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/items/{itemId}/jobs/instances?jobType={Uri.EscapeDataString(jobType)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        if (!string.IsNullOrWhiteSpace(executionData))
        {
            request.Content = new StringContent(executionData, Encoding.UTF8, "application/json");
        }
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);   // 200 or 202

        // The Location header carries the new instance URL — last path segment is the instance id.
        // Some responses return a 200 with the job body instead; fall back to that.
        var loc = response.Headers.Location?.ToString();
        if (string.IsNullOrEmpty(loc) && response.Headers.TryGetValues("Location", out var v))
            loc = v.FirstOrDefault();
        if (!string.IsNullOrEmpty(loc))
        {
            var seg = loc.TrimEnd('/').Split('/').LastOrDefault();
            if (!string.IsNullOrEmpty(seg)) return seg;
        }

        // No Location → try the body.
        var body = await response.Content.ReadAsStringAsync(ct);
        if (!string.IsNullOrWhiteSpace(body))
        {
            using var doc = JsonDocument.Parse(body);
            var id = Str(doc.RootElement, "id");
            if (!string.IsNullOrEmpty(id)) return id!;
        }
        throw new DaxterException("Job started but no instance id was returned by the service.");
    }

    /// <summary>Lists recent job instances for an item — every run with status, kind (Manual /
    /// Scheduled / OnDemand), start/end times, and failure reason. Default limit 50; pagination not
    /// surfaced (the Fabric API supports a continuationToken but ops dashboards rarely need it).</summary>
    public async Task<QueryResult> ListItemJobInstancesAsync(string workspaceId, string itemId, CancellationToken ct = default)
    {
        var url = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/items/{itemId}/jobs/instances";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var root = await SendJsonAsync(request, ct);

        string[] columns = ["instanceId", "status", "invokeType", "startTimeUtc", "endTimeUtc", "durationSec", "failureReason"];
        var rows = new List<object?[]>();
        foreach (var item in Value(root).EnumerateArray())
        {
            var start = TryDate(item, "startTimeUtc");
            var end = TryDate(item, "endTimeUtc");
            double? duration = start is { } s && end is { } e ? (e - s).TotalSeconds : null;
            rows.Add(
            [
                Str(item, "id"),
                Str(item, "status"),
                Str(item, "invokeType"),
                start,
                end,
                duration,
                ExtractFailureReason(item),
            ]);
        }
        return new QueryResult(columns, rows);
    }

    /// <summary>Gets a single item-job instance — typed via <see cref="FabricJobInstance"/>. Used
    /// by the Web page to refresh "the run we just started" without re-fetching the whole list.</summary>
    public async Task<FabricJobInstance> GetItemJobInstanceAsync(
        string workspaceId, string itemId, string instanceId, CancellationToken ct = default)
    {
        var url = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/items/{itemId}/jobs/instances/{instanceId}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var root = await SendJsonAsync(request, ct);
        return new FabricJobInstance(
            Id: Str(root, "id") ?? instanceId,
            ItemId: Str(root, "itemId") ?? itemId,
            JobType: Str(root, "jobType") ?? "",
            InvokeType: Str(root, "invokeType") ?? "",
            Status: Str(root, "status") ?? "Unknown",
            StartTimeUtc: TryDate(root, "startTimeUtc"),
            EndTimeUtc: TryDate(root, "endTimeUtc"),
            FailureReason: ExtractFailureReason(root));
    }

    /// <summary>Cancels a running item-job instance. The Fabric API returns 202 + Location header
    /// pointing back at the same instance — the next status poll will report <c>Cancelled</c> once
    /// the engine accepts the cancel.</summary>
    public async Task CancelItemJobInstanceAsync(
        string workspaceId, string itemId, string instanceId, CancellationToken ct = default)
    {
        var url = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/items/{itemId}/jobs/instances/{instanceId}/cancel";
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);   // 200 or 202
    }

    /// <summary>The Fabric item kinds for which DAXter exposes <c>updateDefinition</c>. The string
    /// values match the URL segments the Fabric API uses.</summary>
    public static class FabricItemKinds
    {
        public const string Report   = "reports";
        public const string Notebook = "notebooks";
        public const string CopyJob  = "copyJobs";
    }

    /// <summary>Generic Fabric updateDefinition. Pushes a list of definition parts (path + UTF-8 text
    /// content) to the item via <c>POST /v1/workspaces/{ws}/{kind}/{itemId}/updateDefinition</c>,
    /// base64-encoding each payload inline. The API returns 202 with a Location header pointing at
    /// the long-running operation; we poll until the LRO succeeds (or fails / times out).
    ///
    /// PHASE 3 — closes the round-trip the artifact store opened in phases 1+2: PBIR parts (or
    /// IPYNB cells, or copy-job JSON) flow agent → artifact store → here → Fabric. Same shape for
    /// reports, notebooks, and copy jobs — the Fabric API normalised them under one verb.
    ///
    /// CALLER OWNS THE PARTS. The list typically comes from reading an artifact prefix via
    /// <see cref="LocalArtifactStore.ListAsync"/> + OpenReadAsync; the caller has already
    /// sanitised + bundled the content. We don't try to validate the shape — Fabric does.</summary>
    public async Task UpdateItemDefinitionAsync(
        string workspaceId, string itemId, string kind, IReadOnlyList<FabricItemPart> parts,
        CancellationToken ct = default)
    {
        if (parts.Count == 0)
            throw new DaxterException("updateDefinition requires at least one part — the artifact prefix appears empty.");

        var url = $"https://api.fabric.microsoft.com/v1/workspaces/{workspaceId}/{kind}/{itemId}/updateDefinition";
        // Build the body. Each part: { path, payload (base64 utf-8), payloadType: "InlineBase64" }.
        // We send text content base64'd; Fabric also accepts binary parts the same way.
        var sb = new StringBuilder();
        sb.Append("{\"definition\":{\"parts\":[");
        for (var i = 0; i < parts.Count; i++)
        {
            if (i > 0) sb.Append(',');
            var p = parts[i];
            var b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(p.Content));
            sb.Append('{');
            sb.Append("\"path\":").Append(System.Text.Json.JsonSerializer.Serialize(p.Path)).Append(',');
            sb.Append("\"payload\":\"").Append(b64).Append("\",");
            sb.Append("\"payloadType\":\"InlineBase64\"");
            sb.Append('}');
        }
        sb.Append("]}}");

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(sb.ToString(), Encoding.UTF8, "application/json"),
        };
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            // Block until the LRO finishes. UpdateDefinition has no useful result payload — we
            // just need success/failure. PollOperationResultAsync throws on Failed; on Succeeded
            // it tries to fetch /result, which may 404 for updateDefinition. Swallow that — the
            // status poll already proved success.
            try { await PollOperationResultAsync(response, ct); }
            catch (DaxterException ex) when (ex.Message.Contains("getDefinition operation failed"))
            {
                // Re-message — the helper hard-codes "getDefinition" in its failure text.
                throw new DaxterException("updateDefinition operation failed on the service.");
            }
            catch (HttpRequestException) { /* /result 404 is fine for updateDefinition */ }
            return;
        }
        await EnsureSuccessAsync(response, ct);   // 200 success path (rare; most are 202)
    }

    // ---- shared helpers for Fabric items (Copy Jobs + Notebooks share the shape) ----

    private async Task<QueryResult> FetchFabricItemsAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        var root = await SendJsonAsync(request, ct);
        string[] columns = ["id", "displayName", "description"];
        var rows = new List<object?[]>();
        foreach (var item in Value(root).EnumerateArray())
        {
            rows.Add([Str(item, "id"), Str(item, "displayName"), Str(item, "description")]);
        }
        rows.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a[1]?.ToString(), b[1]?.ToString()));
        return new QueryResult(columns, rows);
    }

    private async Task<IReadOnlyList<FabricItemPart>> GetItemDefinitionAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);

        JsonElement root;
        if (response.StatusCode == System.Net.HttpStatusCode.Accepted)
        {
            // Long-running op — same poll helper Report.getDefinition uses.
            root = await PollOperationResultAsync(response, ct);
        }
        else
        {
            await EnsureSuccessAsync(response, ct);
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            root = doc.RootElement.Clone();
        }

        var parts = new List<FabricItemPart>();
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
                    catch { content = ""; }
                }
                parts.Add(new FabricItemPart(path, content));
            }
        }
        return parts;
    }

    /// <summary>Pulls a failure message out of either the legacy top-level <c>failureReason</c>
    /// string OR the nested <c>error</c> object Fabric returns on newer LROs.</summary>
    private static string? ExtractFailureReason(JsonElement el)
    {
        var s = Str(el, "failureReason");
        if (!string.IsNullOrEmpty(s)) return s;
        if (el.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var msg = Str(err, "message");
            if (!string.IsNullOrEmpty(msg)) return msg;
        }
        return null;
    }

    private static DateTimeOffset? TryDate(JsonElement el, string property)
    {
        var s = Str(el, property);
        if (string.IsNullOrEmpty(s)) return null;
        return DateTimeOffset.TryParse(s, out var d) ? d : null;
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

    // ---- enhanced (asynchronous, server-managed) refresh ----

    /// <summary>Starts an <b>enhanced refresh</b> (Power BI async refresh API) with the given JSON body
    /// (type, commitMode, maxParallelism, retryCount, timeout, objects). The refresh runs ON THE SERVER —
    /// no long-lived client connection — so it can't hang/drop like a client XMLA refresh. Returns the
    /// <c>requestId</c> (from the <c>Location</c> header) to poll/cancel.</summary>
    public async Task<string> StartEnhancedRefreshAsync(string groupId, string datasetId, string bodyJson, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BaseUrl + $"groups/{groupId}/datasets/{datasetId}/refreshes")
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json"),
        };
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);
        await EnsureSuccessAsync(response, ct);   // 202 Accepted

        var loc = response.Headers.Location?.ToString();
        if (!string.IsNullOrEmpty(loc))
            return loc.TrimEnd('/').Split('/').Last();
        if (response.Headers.TryGetValues("x-ms-request-id", out var ids) && ids.FirstOrDefault() is { } id)
            return id;
        throw new DaxterException("Enhanced refresh accepted but no requestId was returned.");
    }

    /// <summary>Polls an enhanced refresh's status — overall + per-object (per-partition) — by requestId.</summary>
    public async Task<EnhancedRefreshStatus> GetEnhancedRefreshAsync(string groupId, string datasetId, string requestId, CancellationToken ct = default)
    {
        var root = await GetAsync($"groups/{groupId}/datasets/{datasetId}/refreshes/{requestId}", ct);
        var status = Str(root, "status") ?? "Unknown";

        var objects = new List<RefreshObjectStatus>();
        if (root.TryGetProperty("objects", out var objs) && objs.ValueKind == JsonValueKind.Array)
            foreach (var o in objs.EnumerateArray())
                objects.Add(new RefreshObjectStatus(Str(o, "table") ?? "", Str(o, "partition") ?? "", Str(o, "status") ?? ""));

        string? error = null;
        if (root.TryGetProperty("messages", out var msgs) && msgs.ValueKind == JsonValueKind.Array)
            error = string.Join("; ", msgs.EnumerateArray().Select(m => Str(m, "message")).Where(s => !string.IsNullOrEmpty(s)));
        if (string.IsNullOrEmpty(error)) error = Str(root, "extendedStatus");

        return new EnhancedRefreshStatus(status, objects, string.IsNullOrEmpty(error) ? null : error);
    }

    /// <summary>Cancels an in-progress enhanced refresh by requestId (best-effort).</summary>
    public async Task CancelEnhancedRefreshAsync(string groupId, string datasetId, string requestId, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Delete, BaseUrl + $"groups/{groupId}/datasets/{datasetId}/refreshes/{requestId}");
        await Authorize(request, ct);
        using var response = await _http.SendAsync(request, ct);
        _ = response.IsSuccessStatusCode;   // best-effort: ignore cancel failures
    }

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
