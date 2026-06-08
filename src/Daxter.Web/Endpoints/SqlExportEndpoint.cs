using System.Text;
using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Formatting;
using Daxter.Core.Rest;
using Daxter.Core.Sql;
using Daxter.Web.Services;
using Microsoft.AspNetCore.Mvc;

namespace Daxter.Web.Endpoints;

/// <summary>The <c>/api/sql/export</c> minimal-API endpoint. The /sql page submits a hidden form
/// here to start a streaming CSV download — bypassing the Blazor SignalR circuit so a multi-million-row
/// <c>SELECT *</c> never has to fit in memory or travel through the WebSocket. The endpoint resolves
/// the Fabric workspace + endpoint name into (server, database) via the REST discovery list, opens a
/// SqlDataReader, and writes CSV directly to <c>Response.Body</c>, row-by-row, flushing periodically
/// so the browser sees progress.</summary>
public static class SqlExportEndpoint
{
    public static void Map(WebApplication app)
    {
        app.MapPost("/api/sql/export", HandleAsync).DisableAntiforgery();
    }

    private static async Task HandleAsync(
        [FromForm] string workspace,
        [FromForm] string endpoint,
        [FromForm] string sql,
        [FromForm] string? quoteAll,                          // "on" when the checkbox is checked
        [FromForm] string? crlf,                              // "on" when the checkbox is checked
        ConfigState state,
        HttpResponse response,
        HttpContext ctx,
        ILogger<Marker> log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(workspace) || string.IsNullOrWhiteSpace(endpoint) || string.IsNullOrWhiteSpace(sql))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsync("workspace, endpoint, and sql are required.", ct);
            return;
        }

        // HTML checkboxes submit as "on" when checked, omitted otherwise. Treat any non-empty
        // truthy-looking value as on so a programmatic caller can also send quoteAll=true / =1.
        var style = new CsvStyle(
            QuoteAll: IsTruthy(quoteAll),
            Crlf: IsTruthy(crlf));

        // Same gate as the live-query path — read-only by default, only Allow-writes lets non-SELECT
        // through. Exporting a DELETE/MERGE result set makes no sense anyway, but consistency matters.
        var allowWrite = state.AllowWrites && !SqlWriteGate.IsReadOnly(sql);
        if (!SqlWriteGate.IsReadOnly(sql) && !state.AllowWrites)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsync(
                "This statement is not read-only and the 'Allow writes' gate is off. " +
                "Turn it on (Configure → Allow writes) to export a write statement's result set.", ct);
            return;
        }

        var cfg = state.ToConfig(workspace, null);
        var msal = new MsalTokenProvider(cfg, deviceCodePrompt: Console.Error.WriteLine, allowInteractive: false);

        // Resolve the endpoint name → (server, database). Uses the SAME REST discovery list the
        // /sql page populated its picker from.
        string server, database;
        try
        {
            using var rest = new PowerBiRestClient(msal);
            var groupId = await rest.ResolveGroupIdAsync(cfg.Workspace!, ct);
            var list = await rest.SqlEndpointsAsync(groupId, ct);
            var match = list.Rows.FirstOrDefault(r =>
                string.Equals(r[0]?.ToString(), endpoint, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                response.StatusCode = StatusCodes.Status404NotFound;
                await response.WriteAsync($"Endpoint '{endpoint}' not found in workspace '{workspace}'.", ct);
                return;
            }
            server = match[1]?.ToString() ?? "";
            database = match[2]?.ToString() ?? "";
        }
        catch (Exception ex)
        {
            // Endpoint-resolution failures (auth, REST, not found) — send a clean 4xx before we set
            // the download headers, so the browser shows the message instead of a half-CSV.
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsync($"Could not resolve endpoint: {ex.Message}", ct);
            return;
        }

        // Headers go out BEFORE we touch the SQL endpoint — once the response has started streaming
        // there's no way to send an HTTP error code, so any post-resolution failure surfaces as a
        // partial CSV with an error line at the end (browsers accept that — the file just ends short).
        var filename = SafeFilename($"daxter-{endpoint}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.csv");
        response.ContentType = "text/csv; charset=utf-8";
        response.Headers["Content-Disposition"] = $"attachment; filename=\"{filename}\"";
        response.Headers["Cache-Control"] = "no-store";

        await using var writer = new StreamWriter(response.Body, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        try
        {
            var client = new FabricSqlClient(msal);
            var rows = await client.StreamCsvAsync(server, database, sql, allowWrite, writer, ct, style: style);
            log.LogInformation("SQL export complete: {Rows} rows → {Filename} ({Workspace}/{Endpoint}) style=QuoteAll:{QuoteAll}/Crlf:{Crlf}",
                rows, filename, workspace, endpoint, style.QuoteAll, style.Crlf);
        }
        catch (OperationCanceledException)
        {
            // Client navigated away / cancelled — the connection is closing, nothing to write.
        }
        catch (Exception ex)
        {
            // Response is already partially written (headers are committed once StreamCsvAsync writes
            // the header line). Best we can do is append a marker so the user notices in the file.
            log.LogWarning(ex, "SQL export failed mid-stream");
            try
            {
                await writer.WriteAsync(
                    $"\n\n# DAXTER-EXPORT-ERROR: {ex.GetType().Name}: {ex.Message.Replace('\n', ' ')}\n");
            }
            catch { /* connection might already be dead */ }
        }
    }

    /// <summary>HTML checkboxes submit as <c>"on"</c>; programmatic callers might pass
    /// <c>"true"</c> / <c>"1"</c>. Accept any of them as "yes".</summary>
    private static bool IsTruthy(string? v) => !string.IsNullOrEmpty(v) && v switch
    {
        "on" or "true" or "True" or "TRUE" or "1" or "yes" or "Yes" or "YES" => true,
        _ => false,
    };

    /// <summary>Strip filesystem-hostile chars from the suggested download filename. Same rules a
    /// Windows save dialog would silently apply, done explicitly so Mac/Linux clients get a sane
    /// name too.</summary>
    private static string SafeFilename(string name)
    {
        var bad = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var clean = new string(name.Select(c => bad.Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "daxter-export.csv" : clean;
    }

    /// <summary>Marker class so ILogger&lt;T&gt; categorizes log lines under
    /// <c>Daxter.Web.Endpoints.SqlExportEndpoint</c>.</summary>
    public sealed class Marker { }
}
