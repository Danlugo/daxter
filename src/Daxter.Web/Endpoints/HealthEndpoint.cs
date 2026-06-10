using System.Diagnostics;
using System.Text.Json;
using Daxter.Core;
using Daxter.Core.Artifacts;
using Daxter.Core.Context;
using Daxter.Web.Services;

namespace Daxter.Web.Endpoints;

/// <summary>The <c>/api/health</c> minimal-API endpoint — the Semantics-friendly fleet probe.
/// One unauthenticated GET per tenant container returns enough JSON for an orchestration
/// dashboard to populate a "which tenant is on what" grid without exec'ing into the
/// container or scraping the home page. Designed to be cheap and read-only — never touches
/// the Power BI / Fabric APIs, never blocks on a sign-in flow.
///
/// SHAPE.
/// <code>
/// {
///   "tenant_id":  "inspire",      // null when DAXTER_TENANT_ID is unset
///   "label":      "Inspire Brands — Prod",   // null when DAXTER_LABEL is unset
///   "version":    "v1.36.0",
///   "image":      "ghcr.io/danlugo/daxter:v1.36.0",
///   "uptime_seconds": 12345,
///   "artifacts":  { "used_bytes": …, "quota_bytes": …, "count": … },
///   "context":    { "entry_count": …, "namespace_count": … }
/// }
/// </code>
///
/// WHY NO AUTH. Semantics dashboards live inside the same trusted network as the tenant
/// containers; the response carries NO secrets (no signed-in identity, no Power BI / Fabric
/// data, no tokens). Anything that would leak something sensitive is deliberately absent.
/// A reverse proxy in front of Semantics handles per-tenant access control.</summary>
public static class HealthEndpoint
{
    /// <summary>Process-start instant — used to compute uptime. Initialised lazily on first
    /// request so the field is meaningful even when the endpoint is hit immediately after
    /// container boot.</summary>
    private static readonly DateTime ProcessStartUtc =
        Process.GetCurrentProcess().StartTime.ToUniversalTime();

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/health", HandleAsync).DisableAntiforgery();
    }

    private static async Task HandleAsync(
        IArtifactStore artifacts,
        HttpResponse response,
        ILogger<Marker> log,
        CancellationToken ct)
    {
        try
        {
            var body = new Dictionary<string, object?>();

            // Tenant identity first — same shape daxter_version and daxter_capabilities use,
            // so a Semantics dashboard can grep these three response sources interchangeably.
            TenantInfo.MergeInto(body);

            body["version"] = Environment.GetEnvironmentVariable("DAXTER_VERSION") ?? "dev";
            var repo = Environment.GetEnvironmentVariable("DAXTER_REPO") ?? "Danlugo/daxter";
            body["image"] = $"ghcr.io/{repo.ToLowerInvariant()}:{body["version"]}";
            body["uptime_seconds"] = (int)(DateTime.UtcNow - ProcessStartUtc).TotalSeconds;

            // Artifact store fleet info. List + sum is cheap on the typical store; the
            // operations have their own internal bounds so a multi-GB store doesn't
            // tail-bomb the health probe.
            try
            {
                var allArtifacts = await artifacts.ListAsync(null, ct);
                body["artifacts"] = new
                {
                    used_bytes = await artifacts.CurrentUsageBytesAsync(ct),
                    quota_bytes = artifacts.QuotaBytes,
                    count = allArtifacts.Count,
                };
            }
            catch (Exception ex)
            {
                // Don't fail the whole health check if the artifact store is unreadable —
                // surface it as a string so Semantics sees something is off without losing
                // the version / tenant fields.
                log.LogWarning(ex, "/api/health: artifact-store stats failed");
                body["artifacts_error"] = ex.Message;
            }

            // Context plane stats — for the Semantics UI to show "this tenant has curated X
            // entries across Y namespaces". Same defensive-catch story.
            try
            {
                var ctxService = new ContextService(artifacts);
                var entries = await ctxService.ListAsync(null, ct);
                var namespaces = await ctxService.NamespacesAsync(ct);
                body["context"] = new
                {
                    entry_count = entries.Count,
                    namespace_count = namespaces.Count,
                };
            }
            catch (Exception ex)
            {
                log.LogWarning(ex, "/api/health: context-store stats failed");
                body["context_error"] = ex.Message;
            }

            response.ContentType = "application/json; charset=utf-8";
            response.Headers["Cache-Control"] = "no-store";
            await JsonSerializer.SerializeAsync(response.Body, body,
                new JsonSerializerOptions { WriteIndented = true }, ct);
        }
        catch (Exception ex)
        {
            // Last-resort catch. We deliberately don't propagate to 500 because a Semantics
            // dashboard reading the body for a structured error message is more useful than
            // a generic ASP.NET error page.
            log.LogError(ex, "/api/health failed");
            response.StatusCode = StatusCodes.Status500InternalServerError;
            response.ContentType = "application/json; charset=utf-8";
            await JsonSerializer.SerializeAsync(response.Body,
                new { error = ex.Message }, cancellationToken: ct);
        }
    }

    /// <summary>Marker class so ILogger&lt;T&gt; categorizes log lines under
    /// <c>Daxter.Web.Endpoints.HealthEndpoint</c>.</summary>
    public sealed class Marker { }
}
