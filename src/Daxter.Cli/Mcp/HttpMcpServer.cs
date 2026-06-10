using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Daxter.Cli.Mcp;

// ──────────────────────────────────────────────────────────────────────────────────────────────
// HTTP MCP transport — v1.38.0, delivering Semantix wishlist #1.
//
// WHAT THIS DOES. Today `daxter mcp` exposes the MCP server over stdio only. Semantix
// bridges it to HTTP using supergateway (Node + npm package). This file is the native
// equivalent: a Kestrel host that wires the MCP SDK's HTTP transport at a configurable path,
// gates it with bearer-token auth (matching Caddy's behaviour byte-for-byte), and surfaces a
// /healthz endpoint for liveness probes.
//
// WHAT SEMANTIX GETS TO DELETE WHEN THIS SHIPS:
//   - Node 20 + supergateway from gateway.Dockerfile (~25 lines + ~150 MB per image)
//   - The Caddyfile per-client auth handle block (~10 lines × N clients)
//   - The Caddy <KEY>_TOKEN env injection
//
// CONTRACT WITH SEMANTIX (preserves the §4 stability surface):
//   - stdio mode unchanged — every existing Semantix deployment keeps working as-is.
//   - HTTP mode is opt-in via --http. Off by default.
//   - Bearer token sourced from DAXTER_MCP_BEARER_TOKEN env (Semantix already manages this
//     shape per client; just rename CLIENTA_TOKEN -> DAXTER_MCP_BEARER_TOKEN in the
//     gateway container's environment block).
//   - 401 body is "Unauthorized" exactly (matches Caddy's `respond "Unauthorized" 401`).
//   - /healthz unauthenticated; returns 200 with body "ok".
// ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The HTTP transport host. Boots a Kestrel WebApplication, wires the MCP SDK's HTTP
/// transport at <paramref name="httpPath"/>, gates it with the bearer middleware, and runs
/// until cancellation. Stdio path stays in <see cref="McpServer"/> — both modes share the same
/// tool catalogue via reflection.</summary>
internal static class HttpMcpServer
{
    /// <summary>Default port matches Semantix's supergateway default (8000) so an existing
    /// `docker-compose.gen.yml` that publishes :8000 keeps working without changes.</summary>
    public const int DefaultPort = 8000;

    /// <summary>Default mount path mirrors Semantix's path scheme (<c>/mcp</c>). The full
    /// route an outside caller hits is <c>/&lt;httpPath&gt;</c> exactly — no rewriting, no
    /// trailing-slash surprises.</summary>
    public const string DefaultPath = "/mcp";

    public static async Task<int> RunAsync(
        int port, string httpPath, bool noAuth, CancellationToken ct)
    {
        // Normalise the path: always leading slash, no trailing slash. Mirror what Semantix's
        // generate.mjs emits as `path = /mcp/${key}`, which Caddy uses as a prefix matcher.
        if (!httpPath.StartsWith("/")) httpPath = "/" + httpPath;
        httpPath = httpPath.TrimEnd('/');
        if (httpPath.Length == 0) httpPath = DefaultPath;

        var builder = WebApplication.CreateBuilder();
        // Log to STDERR not stdout — stdout is reserved for MCP transport on the stdio path,
        // and even on HTTP-only the convention (stdout=data) means a process supervisor parsing
        // either stream shouldn't get logs mixed in with structured output.
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        // Bind to all interfaces inside the container — Docker / Container Apps handle the
        // public-facing port mapping. Localhost-only binding would break compose port forwarding.
        builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

        // Resolve the token EARLY (before service registration) so we can fail fast on the
        // safety rule — refuse HTTP mode without either a token or an explicit --no-auth.
        string? token = null;
        if (!noAuth)
        {
            try { token = McpBearerToken.Resolve(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"daxter: could not resolve bearer token: {ex.Message}");
                return 1;
            }
            if (string.IsNullOrEmpty(token))
            {
                Console.Error.WriteLine(
                    "daxter: HTTP MCP mode requires a bearer token. Set DAXTER_MCP_BEARER_TOKEN " +
                    "or pass --no-auth to disable the check (dev only — never in production).");
                return 1;
            }
        }

        // The MCP SDK's HTTP transport. Tools come from this assembly via reflection — the
        // same DaxterTools class the stdio path uses, so the two modes are feature-parity by
        // construction (a new [McpServerTool] automatically shows up on both transports).
        builder.Services
            .AddMcpServer()
            .WithHttpTransport()
            .WithToolsFromAssembly(typeof(DaxterTools).Assembly);

        var app = builder.Build();

        // Unauthenticated liveness — Caddy / Container Apps / docker-compose healthcheck poll
        // this. Plain "ok" body matches supergateway's current /healthz response so an existing
        // Semantix Caddyfile keeps working.
        app.MapGet(McpBearerAuthMiddleware.HealthPath, () => Results.Text("ok", "text/plain"));

        // Bearer middleware sits BEFORE MapMcp so an unauth'd request never reaches the MCP
        // pipeline. Skipped entirely when --no-auth is on (local dev only).
        if (!noAuth)
        {
            app.UseMiddleware<McpBearerAuthMiddleware>(token!, httpPath);
        }

        // Mount the MCP HTTP transport at the configured path. The SDK handles the
        // Streamable-HTTP framing; we just provide the route prefix.
        app.MapMcp(httpPath);

        // Operator-facing banner on stderr — confirms the bind, port, and (redacted) token
        // prefix so a Semantix admin can sanity-check the container's effective config.
        var redacted = noAuth ? "(NO AUTH — dev only)" : McpBearerToken.Redact(token);
        Console.Error.WriteLine(
            $"DAXter MCP (HTTP) listening on http://0.0.0.0:{port}{httpPath} · token={redacted}");

        await app.RunAsync(ct);
        return 0;
    }
}
