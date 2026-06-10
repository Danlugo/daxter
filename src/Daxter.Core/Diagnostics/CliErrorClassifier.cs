using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Daxter.Core.Diagnostics;

// ──────────────────────────────────────────────────────────────────────────────────────────────
// CLI error classification — the Semantix wishlist #3 hook.
//
// PROBLEM. Semantix (the L60 control plane that wraps DAXter) validates a client's service-
// principal credentials by running `daxter ws ls` inside the gateway container, and then has
// to parse the human-readable error text — `AADSTS7000215`, `AADSTS900023`, etc. — to figure
// out which knob the operator typed wrong. String-scraping is fragile: if Microsoft rewords
// the message, or if DAXter wraps the exception differently, every Semantix integration
// silently breaks.
//
// SOLUTION. When the CLI is invoked with `--output json`, errors come out as a structured
// JSON envelope on stderr (preserving the stdout=data/stderr=status discipline):
//
//    {
//      "error": {
//        "error_code": "BAD_CLIENT_SECRET",         // stable machine-readable
//        "message":    "The client secret is invalid or expired.",   // human-readable
//        "aad_code":   "AADSTS7000215",             // the raw AAD code, optional
//        "trace_id":   "abc-123-…",                 // if present in the source error
//        "details":    "…",                         // full original message
//      }
//    }
//
// Semantix reads `error_code` and decides what UI hint to show. The codes are STABLE across
// releases — if Microsoft changes the wording, we update the mapping here, not Semantix.
// New codes go at the bottom; existing codes never get repurposed.
// ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Stable, machine-readable failure-class codes for the CLI's <c>--output json</c>
/// path. These are the only values that should ever appear in <see cref="CliError.ErrorCode"/>.
/// Adding a new code is fine; renaming or removing one is a contract break — Semantix and any
/// future MCP-server consumer parses these.</summary>
public static class CliErrorCodes
{
    // ── Auth failures (AADSTS) ────────────────────────────────────────────────────────────────
    /// <summary>AADSTS7000215 — service-principal secret is wrong or has been rotated. Most
    /// common Semantix-onboarding failure ("they pasted the secret ID instead of the value").</summary>
    public const string BAD_CLIENT_SECRET = "BAD_CLIENT_SECRET";
    /// <summary>AADSTS700016 — the client id is wrong, the app registration was deleted, or
    /// it's the right id but in the wrong tenant.</summary>
    public const string BAD_CLIENT_ID = "BAD_CLIENT_ID";
    /// <summary>AADSTS90002 / 900023 / 50020 — the tenant id is wrong, the tenant doesn't
    /// exist, or the SP doesn't belong to it.</summary>
    public const string BAD_TENANT_ID = "BAD_TENANT_ID";
    /// <summary>AADSTS65001 / 700016 (when permissions are missing) — the SP is valid but
    /// hasn't been granted the API permissions DAXter needs.</summary>
    public const string INSUFFICIENT_PERMISSIONS = "INSUFFICIENT_PERMISSIONS";
    /// <summary>No cached MSAL token AND can't run device-code — usually "interactive flow
    /// unavailable in headless mode". DAXter itself raises this as a DaxterException.</summary>
    public const string NOT_SIGNED_IN = "NOT_SIGNED_IN";

    // ── Power BI / Fabric REST failures ───────────────────────────────────────────────────────
    /// <summary>The workspace name didn't resolve to a group id (the workspace doesn't exist
    /// OR the SP can't see it).</summary>
    public const string WORKSPACE_NOT_FOUND = "WORKSPACE_NOT_FOUND";
    /// <summary>The dataset / report / item didn't resolve in the workspace it should live in.</summary>
    public const string ITEM_NOT_FOUND = "ITEM_NOT_FOUND";
    /// <summary>HTTP 403 from a Power BI / Fabric API — the SP doesn't have the role the
    /// operation needs (e.g. needs Member to refresh, has Viewer).</summary>
    public const string FORBIDDEN = "FORBIDDEN";

    // ── Generic / network ─────────────────────────────────────────────────────────────────────
    /// <summary>Network unreachable, DNS failure, timeout. Distinct from FORBIDDEN — the
    /// service rejected vs. the service is unreachable are different operator actions.</summary>
    public const string NETWORK_FAILURE = "NETWORK_FAILURE";
    /// <summary>Catch-all — used when we can't pattern-match. The full original message is in
    /// <see cref="CliError.Details"/> so Semantix still has something to show.</summary>
    public const string UNKNOWN = "UNKNOWN";
}

/// <summary>The structured error envelope. <c>Details</c> always carries the original
/// exception message so an operator can dig in when the high-level code isn't precise enough.</summary>
public sealed record CliError(
    [property: JsonPropertyName("error_code")] string ErrorCode,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("aad_code")] string? AadCode = null,
    [property: JsonPropertyName("trace_id")] string? TraceId = null,
    [property: JsonPropertyName("details")] string? Details = null);

/// <summary>Map an exception to a stable <see cref="CliError"/>. Best-effort: pattern-matches
/// known AADSTS codes and DaxterException message conventions, falls through to
/// <see cref="CliErrorCodes.UNKNOWN"/> with the raw message preserved in <c>Details</c>.</summary>
public static class CliErrorClassifier
{
    // Regex for AADSTS codes — these show up in MSAL exception messages in a few forms:
    //   "AADSTS7000215: ..."
    //   "Trace ID: ... AADSTS7000215 ..."
    // The number is the only stable thing; the surrounding text varies by tenant / locale.
    // AAD codes range from 5-digit (AADSTS50020) to 7-digit (AADSTS7000215). The widely-used
    // documented ones all fall in 4..8 — keeping the upper bound a little loose so a newly-
    // documented code doesn't fall through to UNKNOWN just because of digit count.
    private static readonly Regex AadStsRx = new(@"AADSTS(\d{4,8})", RegexOptions.Compiled);
    private static readonly Regex TraceIdRx = new(
        @"Trace\s*ID:\s*([0-9a-fA-F-]{8,})", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Classify any exception into a structured CLI error envelope.</summary>
    public static CliError Classify(Exception ex)
    {
        var raw = ex.Message ?? "";
        var details = raw.Length > 600 ? raw.Substring(0, 600) + "…" : raw;
        var traceId = TraceIdRx.Match(raw) is { Success: true } tm ? tm.Groups[1].Value : null;
        var aadCode = AadStsRx.Match(raw) is { Success: true } am ? "AADSTS" + am.Groups[1].Value : null;

        // ── AAD-coded paths ───────────────────────────────────────────────────────────────────
        // Each AADSTS we surface has a one-liner human message tuned for the operator. Adding
        // new codes here is the entire maintenance burden — Semantix doesn't change.
        if (aadCode is not null)
        {
            return aadCode switch
            {
                "AADSTS7000215" => new CliError(
                    CliErrorCodes.BAD_CLIENT_SECRET,
                    "The client secret is wrong or expired. Re-paste it from the Azure portal — note that 'secret value' (the long string) is required, not 'secret ID' (the GUID).",
                    AadCode: aadCode, TraceId: traceId, Details: details),

                "AADSTS700016" => new CliError(
                    CliErrorCodes.BAD_CLIENT_ID,
                    "The client (application) id was not found in the tenant. Check that the app registration exists in this tenant and the client id matches.",
                    AadCode: aadCode, TraceId: traceId, Details: details),

                "AADSTS90002" or "AADSTS900023" or "AADSTS50020" => new CliError(
                    CliErrorCodes.BAD_TENANT_ID,
                    "The tenant id is wrong or the service principal does not belong to that tenant.",
                    AadCode: aadCode, TraceId: traceId, Details: details),

                "AADSTS65001" or "AADSTS50105" => new CliError(
                    CliErrorCodes.INSUFFICIENT_PERMISSIONS,
                    "The service principal is valid but lacks the API permissions DAXter needs (Power BI Service / Fabric). Grant the required Application permissions and admin-consent them in the Azure portal.",
                    AadCode: aadCode, TraceId: traceId, Details: details),

                _ => new CliError(
                    CliErrorCodes.UNKNOWN,
                    $"Azure AD returned {aadCode}. See details.",
                    AadCode: aadCode, TraceId: traceId, Details: details),
            };
        }

        // ── DaxterException message conventions ───────────────────────────────────────────────
        // The engine already produces clean operator-facing messages for the common cases —
        // we just pattern-match on key phrases to assign a code. If a phrase here ever changes
        // in DaxterException-callsite code, this mapping has to move too; tests guard against
        // silent drift.
        if (ex is DaxterException)
        {
            var lower = raw.ToLowerInvariant();
            if (lower.Contains("not signed in"))
                return new CliError(CliErrorCodes.NOT_SIGNED_IN,
                    "DAXter is not signed in. Run `daxter login` (device code) or set DAXTER_AUTH_MODE=service-principal + client id/secret/tenant.",
                    Details: details);
            if (lower.Contains("workspace") && (lower.Contains("not found") || lower.Contains("could not resolve")))
                return new CliError(CliErrorCodes.WORKSPACE_NOT_FOUND,
                    "The workspace name was not found in the tenant, or the signed-in identity cannot see it.",
                    Details: details);
            if (lower.Contains("not found") && (lower.Contains("dataset") || lower.Contains("report") || lower.Contains("item")))
                return new CliError(CliErrorCodes.ITEM_NOT_FOUND,
                    "The requested item (dataset / report / notebook) was not found in the workspace.",
                    Details: details);
        }

        // ── HTTP / network classification ─────────────────────────────────────────────────────
        if (ex is HttpRequestException httpEx)
        {
            // 403 → permissions; everything else network/transport → NETWORK_FAILURE.
            if (httpEx.StatusCode == System.Net.HttpStatusCode.Forbidden)
                return new CliError(CliErrorCodes.FORBIDDEN,
                    "The signed-in identity is authenticated but lacks the role this operation requires.",
                    Details: details);
            if (httpEx.StatusCode == System.Net.HttpStatusCode.NotFound)
                return new CliError(CliErrorCodes.ITEM_NOT_FOUND,
                    "The Power BI / Fabric API returned 404 — the resource doesn't exist or isn't visible to this identity.",
                    Details: details);
            return new CliError(CliErrorCodes.NETWORK_FAILURE,
                $"Network failure reaching the Power BI / Fabric API: {ex.Message}",
                Details: details);
        }
        if (ex is TaskCanceledException or TimeoutException)
            return new CliError(CliErrorCodes.NETWORK_FAILURE,
                "Operation timed out talking to the Power BI / Fabric API.",
                Details: details);

        // ── Fallback ──────────────────────────────────────────────────────────────────────────
        return new CliError(CliErrorCodes.UNKNOWN, ex.Message, Details: details);
    }

    /// <summary>Serialize a <see cref="CliError"/> as the documented stderr envelope:
    /// <c>{"error": {...}}</c>. Used by the CLI when <c>--output json</c> is set. Omits null
    /// optional fields so the wire-shape contract is "field present iff value non-null" — the
    /// same rule /api/health follows. Semantix parsers can rely on this.</summary>
    public static string ToJsonEnvelope(CliError error)
        => JsonSerializer.Serialize(new { error },
            new JsonSerializerOptions
            {
                WriteIndented = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            });
}
