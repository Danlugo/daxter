using System.Text.Json;
using Daxter.Core.Artifacts;
using Daxter.Web.Services;

namespace Daxter.Web.Endpoints;

/// <summary>The <c>/api/artifacts</c> minimal-API endpoints — the streaming transport layer for
/// the artifact store. The Blazor circuit (SignalR) can't carry multi-GB binary payloads, so the
/// /artifacts Razor page (and external agents) hit these endpoints directly when they need to
/// move bytes. Read endpoints in Phase 1; write endpoints land in Phase 2.</summary>
public static class ArtifactsEndpoint
{
    /// <summary>Cap inline JSON listing at this many entries. Above this the list endpoint asks
    /// the caller to narrow with ?prefix=. Same self-protection pattern the inventory/lineage
    /// endpoints use.</summary>
    private const int ListCap = 5000;

    public static void Map(WebApplication app)
    {
        // List artifacts (JSON). Optional ?prefix= narrows the scan.
        app.MapGet("/api/artifacts", ListAsync).DisableAntiforgery();

        // Stream a single artifact OR a zip bundle of a prefix when ?bundle=1.
        // The {key} route value captures the full slash-separated path — that's why the route
        // template has the catch-all {*key}, mirroring how S3 / Azure Blob key paths work.
        app.MapGet("/api/artifacts/{**key}", GetAsync).DisableAntiforgery();
    }

    /// <summary>List endpoint. Returns the same ArtifactRef shape the bridge exposes, as JSON.
    /// The /artifacts Razor page calls this initially, then again on every search-box keystroke
    /// + every prefix-tree click.</summary>
    private static async Task ListAsync(
        string? prefix,
        DaxterUi ui,
        HttpResponse response,
        ILogger<Marker> log,
        CancellationToken ct)
    {
        try
        {
            var items = await ui.ArtifactsListAsync(prefix, ct);
            if (items.Count > ListCap)
            {
                response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                await response.WriteAsync(
                    $"Listing too large ({items.Count} entries > {ListCap}). Pass ?prefix= to narrow it.", ct);
                return;
            }
            var usage = await ui.ArtifactsUsageBytesAsync(ct);
            response.ContentType = "application/json; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            await JsonSerializer.SerializeAsync(response.Body, new
            {
                items,
                usage_bytes = usage,
                quota_bytes = ui.ArtifactStore.QuotaBytes,
            }, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            log.LogError(ex, "List artifacts failed");
            response.StatusCode = StatusCodes.Status500InternalServerError;
            await response.WriteAsync($"List failed: {ex.Message}", ct);
        }
    }

    /// <summary>Stream a single artifact's content OR a zipped prefix bundle when ?bundle=1. The
    /// Content-Disposition is set so the browser triggers a download with a sensible filename.</summary>
    private static async Task GetAsync(
        string key,
        bool? bundle,
        DaxterUi ui,
        HttpResponse response,
        ILogger<Marker> log,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsync("key is required.", ct);
            return;
        }

        try
        {
            if (bundle == true)
            {
                // Zip-stream a whole prefix. Bytes flow temp-file → response → client; the
                // temp file is deleted on stream close (FileOptions.DeleteOnClose).
                await using var zipStream = await ui.ArtifactStore.OpenBundleAsync(key, ct);
                response.ContentType = "application/zip";
                response.Headers["Content-Disposition"] =
                    $"attachment; filename=\"{SafeFilename(LastSegment(key))}.zip\"";
                response.Headers["Cache-Control"] = "no-store";
                await zipStream.CopyToAsync(response.Body, ct);
                log.LogInformation("Artifact bundle streamed: prefix={Key}", key);
                return;
            }

            await using var file = await ui.ArtifactStore.OpenReadAsync(key, ct);
            // Best-guess content type — most artifacts are JSON (PBIR parts, copy-job defs) but
            // .pbix is application/octet-stream and CSV is text/csv. Browser will sniff if needed.
            response.ContentType = GuessContentType(key);
            response.Headers["Content-Disposition"] =
                $"attachment; filename=\"{SafeFilename(LastSegment(key))}\"";
            response.Headers["Cache-Control"] = "no-store";
            // Set Content-Length so the browser can show a download progress bar.
            if (file.CanSeek) response.ContentLength = file.Length;
            await file.CopyToAsync(response.Body, ct);
            log.LogInformation("Artifact streamed: key={Key}", key);
        }
        catch (InvalidArtifactKeyException ex)
        {
            response.StatusCode = StatusCodes.Status400BadRequest;
            await response.WriteAsync(ex.Message, ct);
        }
        catch (FileNotFoundException)
        {
            response.StatusCode = StatusCodes.Status404NotFound;
            await response.WriteAsync($"Artifact not found: {key}", ct);
        }
        catch (OperationCanceledException)
        {
            // Client navigated away mid-stream — nothing to send back.
        }
        catch (Exception ex)
        {
            // After the first byte hits the wire the headers are committed, so we can't switch
            // to a 5xx. Best effort: log + let the response close short. The HTTP client sees a
            // truncated body.
            log.LogWarning(ex, "Artifact stream failed mid-flight: {Key}", key);
        }
    }

    /// <summary>Last path segment, used to name the download. For a bundle of <c>reports/sales</c>
    /// the filename becomes <c>sales.zip</c>; for a single file <c>reports/sales/page.json</c> it
    /// becomes <c>page.json</c>.</summary>
    private static string LastSegment(string key) =>
        key.TrimEnd('/').Split('/').LastOrDefault() ?? "artifact";

    /// <summary>Strip filesystem-hostile chars from the suggested download filename. Same rule the
    /// SQL export uses — explicit so cross-OS clients get a sane name.</summary>
    private static string SafeFilename(string name)
    {
        var bad = new[] { '/', '\\', ':', '*', '?', '"', '<', '>', '|' };
        var clean = new string(name.Select(c => bad.Contains(c) ? '-' : c).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "artifact" : clean;
    }

    /// <summary>Map common extensions to a Content-Type so the browser does the right thing.
    /// Defaults to application/octet-stream — the safe binary fallback.</summary>
    private static string GuessContentType(string key)
    {
        var ext = Path.GetExtension(key).ToLowerInvariant();
        return ext switch
        {
            ".json" => "application/json; charset=utf-8",
            ".csv"  => "text/csv; charset=utf-8",
            ".txt"  => "text/plain; charset=utf-8",
            ".xml"  => "application/xml; charset=utf-8",
            ".zip"  => "application/zip",
            ".pbix" => "application/octet-stream",
            ".ipynb"=> "application/x-ipynb+json",
            _       => "application/octet-stream",
        };
    }

    /// <summary>Marker class so ILogger&lt;T&gt; categorizes log lines under
    /// <c>Daxter.Web.Endpoints.ArtifactsEndpoint</c>.</summary>
    public sealed class Marker { }
}
