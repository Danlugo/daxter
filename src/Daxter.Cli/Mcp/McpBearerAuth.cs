using System.Security.Cryptography;
using System.Text;
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

/// <summary>Resolves + persists the bearer token for the HTTP MCP transport. Pure logic — no
/// ASP.NET dependencies. The middleware (<see cref="McpBearerAuthMiddleware"/>) consumes the
/// resolved bytes for the constant-time compare.</summary>
public static class McpBearerToken
{
    /// <summary>Env var Semantix (and any other fleet orchestrator) sets per-container to pin
    /// the token to whatever its admin console generated. Reading from env keeps DAXter stateless
    /// w.r.t. token lifecycle; rotation = restart with a new value.</summary>
    public const string EnvVarName = "DAXTER_MCP_BEARER_TOKEN";

    /// <summary>Path under the persistent token volume where the self-generated fallback token
    /// lives. Lets a solo `daxter mcp --http` start without any env var; first start generates,
    /// subsequent restarts reuse.</summary>
    public static string DefaultPersistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".daxter", "mcp-bearer-token");

    /// <summary>Resolve the token from env > persisted file > generate-and-persist. Returns the
    /// raw string (caller will UTF8-encode for the constant-time compare).</summary>
    public static string Resolve(string? persistPath = null)
    {
        var env = Environment.GetEnvironmentVariable(EnvVarName);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        var path = persistPath ?? DefaultPersistPath;
        if (File.Exists(path))
        {
            var fromDisk = File.ReadAllText(path).Trim();
            if (!string.IsNullOrEmpty(fromDisk)) return fromDisk;
        }

        // First-run on a fresh volume — generate + persist with restrictive perms.
        var generated = Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, generated);
        TryRestrictPermissions(path);
        return generated;
    }

    /// <summary>Generate a fresh sk_-prefixed hex token. Matches Semantix's existing format
    /// (`sk_` + 24 random bytes hex-encoded = 48 hex chars, ~192 bits entropy) so the token
    /// shape is consistent regardless of who minted it.</summary>
    public static string Generate()
    {
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return "sk_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Force-regenerate the persisted token (the `daxter mcp rotate-token` CLI calls
    /// this). No-op when the env-var path is in use — that's Semantix's responsibility.</summary>
    public static string Rotate(string? persistPath = null)
    {
        var fresh = Generate();
        var path = persistPath ?? DefaultPersistPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, fresh);
        TryRestrictPermissions(path);
        return fresh;
    }

    /// <summary>Truncate a token to its prefix for safe logging. Never log the raw token —
    /// even one accidental log line in a shared aggregator burns the secret.</summary>
    public static string Redact(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "(none)";
        if (token.Length <= 8) return "***";
        return $"{token.Substring(0, 8)}***";
    }

    /// <summary>Best-effort chmod 600 on the persist file so the token isn't world-readable on
    /// shared filesystems. POSIX-only; Windows ignores. Failure is non-fatal (the token still
    /// works; the host just isn't hardened).</summary>
    private static void TryRestrictPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        catch { /* best-effort */ }
    }
}

/// <summary>ASP.NET Core middleware that enforces the bearer-token check on the configured MCP
/// path. <see cref="HealthPath"/> is exempt so an unauthenticated container-orchestrator
/// liveness probe still works.</summary>
public sealed class McpBearerAuthMiddleware
{
    private readonly RequestDelegate _next;
    private readonly byte[] _expectedBytes;
    private readonly string _protectedPath;

    public const string HealthPath = "/healthz";

    public McpBearerAuthMiddleware(RequestDelegate next, string token, string protectedPath)
    {
        _next = next;
        _expectedBytes = Encoding.UTF8.GetBytes(token);
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
        if (!TryExtractBearer(header, out var presented))
        {
            await Reject(context);
            return;
        }

        // Constant-time compare on the UTF8 bytes — prevents an attacker from leaking the
        // token character-by-character via response-time variance. CryptographicOperations
        // doesn't throw on length mismatch but returns false, which is what we want.
        var presentedBytes = Encoding.UTF8.GetBytes(presented);
        if (!CryptographicOperations.FixedTimeEquals(presentedBytes, _expectedBytes))
        {
            await Reject(context);
            return;
        }

        await _next(context);
    }

    /// <summary>Parse <c>Authorization: Bearer &lt;token&gt;</c> case-insensitively per RFC 6750.
    /// Returns false if the header is missing, malformed, or uses a different scheme.</summary>
    private static bool TryExtractBearer(string? header, out string token)
    {
        token = "";
        if (string.IsNullOrEmpty(header)) return false;
        // "Bearer " literal — case-insensitive on the scheme.
        const string scheme = "Bearer ";
        if (header.Length <= scheme.Length) return false;
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        token = header.Substring(scheme.Length).Trim();
        return token.Length > 0;
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
