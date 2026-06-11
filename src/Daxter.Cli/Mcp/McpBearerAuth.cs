using Daxter.Core.Auth;
using Microsoft.AspNetCore.Http;

namespace Daxter.Cli.Mcp;

// ──────────────────────────────────────────────────────────────────────────────────────────────
// Bearer-token auth for the v1.38.0 HTTP MCP transport.
//
// THE BOUNDARY. Semantix's hand-off doc names per-route bearer tokens as the auth boundary
// (the opaque path is just obfuscation). Today Caddy enforces it; after v1.38.0, DAXter does.
//
// CONTRACT WITH SEMANTIX (matched byte-for-byte so swapping Caddy -> DAXter is invisible):
//   Failure status:    401
//   Failure body:      "Unauthorized"        (Caddy's `respond "Unauthorized" 401`)
//   Failure header:    WWW-Authenticate: Bearer realm="daxter-mcp"
//   Header parsed:     Authorization: Bearer <token>
//   Compare:           FixedTimeEquals (constant time — defends against timing attacks)
//
// TOKEN SOURCING (in priority order):
//   1. DAXTER_MCP_BEARER_TOKEN env var       — primary path (Semantix sets this per-tenant)
//   2. ~/.daxter/mcp-bearer-token            — persisted fallback for solo `daxter mcp --http`
//   3. (none) + --no-auth flag               — opt-out for local dev; the CLI refuses to start
//                                              HTTP mode without one of {token, --no-auth}.
//
// LOG REDACTION. We never log the full token. Anywhere we surface it we go through
// Redact() which keeps a short prefix (sk_abc12***) so an operator can correlate it with
// what they pasted into Semantix without leaking the secret to log aggregators.
// ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The MCP HTTP transport's bearer token: env <c>DAXTER_MCP_BEARER_TOKEN</c>, persisted
/// fallback at <c>~/.daxter/mcp-bearer-token</c>. A thin wrapper over the shared
/// <see cref="BearerTokenStore"/> primitive (which owns the security-critical generate / compare /
/// redact logic) — this class just pins the MCP-specific env var + filename so the public API the
/// v1.38.0 code and tests use stays identical.</summary>
public static class McpBearerToken
{
    public const string EnvVarName = "DAXTER_MCP_BEARER_TOKEN";

    public static string DefaultPersistPath => Path.Combine(BearerTokenStore.DefaultDir, "mcp-bearer-token");

    public static string Resolve(string? persistPath = null)
        => BearerTokenStore.Resolve(EnvVarName, persistPath ?? DefaultPersistPath);

    public static string Generate() => BearerTokenStore.Generate();

    public static string Rotate(string? persistPath = null)
        => BearerTokenStore.Rotate(persistPath ?? DefaultPersistPath);

    public static string Redact(string? token) => BearerTokenStore.Redact(token);
}

/// <summary>ASP.NET Core middleware that enforces the bearer-token check on the configured MCP
/// path. <see cref="HealthPath"/> is exempt so an unauthenticated container-orchestrator
/// liveness probe still works.</summary>
public sealed class McpBearerAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _expected;
    private readonly string _protectedPath;

    public const string HealthPath = "/healthz";

    public McpBearerAuthMiddleware(RequestDelegate next, string token, string protectedPath)
    {
        _next = next;
        _expected = token;
        _protectedPath = protectedPath.StartsWith("/") ? protectedPath : "/" + protectedPath;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // /healthz is always open — used by Caddy / Container Apps for liveness probes. We do
        // NOT want a misconfigured orchestrator's probe to silently fail with 401 and look like
        // the daemon is down.
        if (context.Request.Path.StartsWithSegments(HealthPath, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        // Only protect the configured MCP path. Anything else (no other routes exist on this
        // server today) falls through — keeps the door open for `/api/health`-style additions
        // later without rewriting this middleware.
        if (!context.Request.Path.StartsWithSegments(_protectedPath, StringComparison.Ordinal))
        {
            await _next(context);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (!BearerTokenStore.TryExtractBearer(header, out var presented)
            || !BearerTokenStore.FixedTimeEquals(presented, _expected))
        {
            await Reject(context);
            return;
        }

        await _next(context);
    }

    /// <summary>Write the 401 response byte-for-byte identical to Caddy's
    /// <c>respond "Unauthorized" 401</c> — same status + body — so a Semantix client that
    /// matches the response shape today still works after the swap.</summary>
    private static async Task Reject(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"daxter-mcp\"";
        context.Response.ContentType = "text/plain; charset=utf-8";
        await context.Response.WriteAsync("Unauthorized");
    }
}
