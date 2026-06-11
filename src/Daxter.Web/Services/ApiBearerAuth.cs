using Daxter.Core.Auth;

namespace Daxter.Web.Services;

// ──────────────────────────────────────────────────────────────────────────────────────────────
// v1.40.0 — Optional bearer-token authentication for the Web console's HTTP /api/* endpoints.
//
// The console's data-bearing endpoints (/api/sql/export, /api/artifacts/*) operate with the
// signed-in identity, so when the console is exposed beyond localhost they should require auth.
// This middleware provides that, opt-in via DAXTER_WEB_BEARER_TOKEN.
//
// POLICY:
//   - /api/health        — ALWAYS open. A liveness probe with no sensitive data; gating it would
//                          make a misconfigured orchestrator think the container is down.
//   - /api/sql/export    — requires the bearer when a Web token is configured.
//   - /api/artifacts/*   — requires the bearer when a Web token is configured.
//   - Everything else (the Blazor SignalR circuit, static files) — not handled here; the
//     localhost-default bind is its protection (gating a SignalR circuit with a bearer would
//     break the browser).
//
// ENABLE/DISABLE. When DAXTER_WEB_BEARER_TOKEN is UNSET the gate is OFF — preserving the
// localhost-only default (run `daxter web` on 127.0.0.1, no token needed). Set the token when
// binding beyond localhost; a startup warning fires if you bind wide without one.
//
// Shares the security primitive (constant-time compare, header parse) with the MCP path via
// Daxter.Core.Auth.BearerTokenStore — the crypto lives once.
// ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Gates the sensitive Web <c>/api/*</c> endpoints behind a bearer token when
/// <c>DAXTER_WEB_BEARER_TOKEN</c> is configured. No-op (pass-through) when the token is unset —
/// the localhost-default bind is the protection in that case.</summary>
public sealed class ApiBearerAuthMiddleware
{
    /// <summary>Env var that, when set, turns ON the /api/* bearer gate. Distinct from the MCP
    /// token (<c>DAXTER_MCP_BEARER_TOKEN</c>) — the Web console and the MCP transport are
    /// separate surfaces with separate tokens.</summary>
    public const string EnvVarName = "DAXTER_WEB_BEARER_TOKEN";

    /// <summary>Path prefixes that REQUIRE the bearer when the gate is on. <c>/api/health</c> is
    /// deliberately excluded (open liveness probe).</summary>
    private static readonly string[] ProtectedPrefixes =
    [
        "/api/sql",        // SQL export — operates with the signed-in identity
        "/api/artifacts",  // artifact + context store read / write / delete
    ];

    private readonly RequestDelegate _next;
    private readonly string? _token;

    public ApiBearerAuthMiddleware(RequestDelegate next)
    {
        _next = next;
        _token = BearerTokenStore.FromEnv(EnvVarName);   // null = gate disabled
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Gate disabled (no token) → pass everything through. localhost-bind is the protection.
        if (_token is null) { await _next(context); return; }

        var path = context.Request.Path;
        var isProtected = ProtectedPrefixes.Any(p =>
            path.StartsWithSegments(p, StringComparison.OrdinalIgnoreCase));
        if (!isProtected) { await _next(context); return; }

        var header = context.Request.Headers.Authorization.ToString();
        if (!BearerTokenStore.TryExtractBearer(header, out var presented)
            || !BearerTokenStore.FixedTimeEquals(presented, _token))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"daxter-web\"";
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync("Unauthorized");
            return;
        }

        await _next(context);
    }
}
