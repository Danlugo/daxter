using System.Text;
using Daxter.Cli.Mcp;
using Microsoft.AspNetCore.Http;

namespace Daxter.Core.Tests;

/// <summary>Pins the v1.38.0 bearer-auth contract Semantix depends on: header parse rules,
/// constant-time compare semantics, 401 wire-shape byte-identical to Caddy
/// (<c>respond "Unauthorized" 401</c>), token redaction behaviour, and the resolve chain
/// (env > persisted file > generate). A change to any of these is a Semantix-visible
/// contract change.</summary>
public sealed class McpBearerAuthTests : IDisposable
{
    private readonly string _tmpDir;
    private readonly string _tmpToken;

    public McpBearerAuthTests()
    {
        _tmpDir = Path.Combine(Path.GetTempPath(), "daxter-bearer-tests-" + Guid.NewGuid().ToString("N"));
        _tmpToken = Path.Combine(_tmpDir, "mcp-bearer-token");
        Directory.CreateDirectory(_tmpDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmpDir, recursive: true); } catch { }
    }

    // ── Token utilities ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Generate_produces_sk_prefixed_48_hex_chars()
    {
        // Format matches Semantix's existing `sk_` + 24 random bytes hex = 48 hex chars
        // (~192 bits entropy). Keeping the format the same means an L60 ops person looking at
        // a token in .env vs. logs vs. /api/state can't tell which side minted it — that's
        // good for consistency, not a security property.
        var t = McpBearerToken.Generate();
        Assert.StartsWith("sk_", t);
        Assert.Equal(3 + 48, t.Length);
        Assert.Matches("^sk_[0-9a-f]{48}$", t);
    }

    [Fact]
    public void Generate_produces_unique_tokens_each_call()
    {
        // Smoke test — if the random source ever collapses to a constant, this catches it.
        var a = McpBearerToken.Generate();
        var b = McpBearerToken.Generate();
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Resolve_prefers_env_var_over_persisted_file()
    {
        var prev = Environment.GetEnvironmentVariable(McpBearerToken.EnvVarName);
        try
        {
            File.WriteAllText(_tmpToken, "sk_from_disk_aaa");
            Environment.SetEnvironmentVariable(McpBearerToken.EnvVarName, "sk_from_env_bbb");

            var resolved = McpBearerToken.Resolve(_tmpToken);
            Assert.Equal("sk_from_env_bbb", resolved);
        }
        finally { Environment.SetEnvironmentVariable(McpBearerToken.EnvVarName, prev); }
    }

    [Fact]
    public void Resolve_reads_persisted_file_when_env_is_empty()
    {
        var prev = Environment.GetEnvironmentVariable(McpBearerToken.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(McpBearerToken.EnvVarName, null);
            File.WriteAllText(_tmpToken, "sk_from_disk_ccc\n");      // trailing newline tolerated

            var resolved = McpBearerToken.Resolve(_tmpToken);
            Assert.Equal("sk_from_disk_ccc", resolved);              // trim works
        }
        finally { Environment.SetEnvironmentVariable(McpBearerToken.EnvVarName, prev); }
    }

    [Fact]
    public void Resolve_generates_and_persists_when_neither_env_nor_file_exists()
    {
        // First-run path on a fresh Semantix volume. We generate a token, persist it, and
        // every subsequent call returns the same value (idempotent until Rotate() is called).
        var prev = Environment.GetEnvironmentVariable(McpBearerToken.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(McpBearerToken.EnvVarName, null);
            Assert.False(File.Exists(_tmpToken));

            var first = McpBearerToken.Resolve(_tmpToken);
            var second = McpBearerToken.Resolve(_tmpToken);
            Assert.Equal(first, second);
            Assert.True(File.Exists(_tmpToken));
            Assert.Matches("^sk_[0-9a-f]{48}$", first);
        }
        finally { Environment.SetEnvironmentVariable(McpBearerToken.EnvVarName, prev); }
    }

    [Fact]
    public void Rotate_writes_a_fresh_token_to_disk()
    {
        var prev = Environment.GetEnvironmentVariable(McpBearerToken.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(McpBearerToken.EnvVarName, null);
            File.WriteAllText(_tmpToken, "sk_old_token_ddd");

            var rotated = McpBearerToken.Rotate(_tmpToken);
            Assert.Matches("^sk_[0-9a-f]{48}$", rotated);
            Assert.Equal(rotated, File.ReadAllText(_tmpToken).Trim());
        }
        finally { Environment.SetEnvironmentVariable(McpBearerToken.EnvVarName, prev); }
    }

    [Fact]
    public void Redact_keeps_a_short_prefix_only()
    {
        // Log lines must never carry the full token. The prefix-+ *** shape lets an operator
        // correlate logs with what they pasted into Semantix without leaking the secret to
        // shared log aggregators (Splunk, Datadog, anywhere logs cross trust boundaries).
        Assert.Equal("sk_abc12***", McpBearerToken.Redact("sk_abc1234567890abcdef"));
        Assert.Equal("***", McpBearerToken.Redact("short"));
        Assert.Equal("(none)", McpBearerToken.Redact(null));
        Assert.Equal("(none)", McpBearerToken.Redact(""));
    }

    // ── Bearer middleware — wire-shape contract with Caddy ────────────────────────────────────

    [Fact]
    public async Task Middleware_rejects_request_with_no_Authorization_header_as_401_Unauthorized()
    {
        var (ctx, body) = TestContext(path: "/mcp");
        var mw = new McpBearerAuthMiddleware(_ => Task.CompletedTask, "sk_expected", "/mcp");
        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Equal("Unauthorized", ReadBody(body));                 // BYTE-IDENTICAL to Caddy
        Assert.Equal("Bearer realm=\"daxter-mcp\"", ctx.Response.Headers["WWW-Authenticate"]);
    }

    [Fact]
    public async Task Middleware_rejects_wrong_token_as_401_Unauthorized()
    {
        var (ctx, body) = TestContext(path: "/mcp", auth: "Bearer sk_wrong");
        var mw = new McpBearerAuthMiddleware(_ => Task.CompletedTask, "sk_expected", "/mcp");
        await mw.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Equal("Unauthorized", ReadBody(body));
    }

    [Fact]
    public async Task Middleware_accepts_correct_token_and_calls_next()
    {
        var nextCalled = false;
        var (ctx, _) = TestContext(path: "/mcp", auth: "Bearer sk_expected");
        var mw = new McpBearerAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            "sk_expected", "/mcp");
        await mw.InvokeAsync(ctx);

        Assert.True(nextCalled);
        // Default status code (200) — we never wrote to the response.
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_accepts_token_when_scheme_is_lowercase()
    {
        // RFC 6750 says the scheme is case-insensitive. Some HTTP clients lowercase it
        // (HTTPie defaults to that). Reject only on actual token mismatch, not on scheme case.
        var (ctx, _) = TestContext(path: "/mcp", auth: "bearer sk_expected");
        var mw = new McpBearerAuthMiddleware(_ => Task.CompletedTask, "sk_expected", "/mcp");
        await mw.InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_rejects_other_auth_scheme()
    {
        var (ctx, body) = TestContext(path: "/mcp", auth: "Basic dXNlcjpwYXNz");
        var mw = new McpBearerAuthMiddleware(_ => Task.CompletedTask, "sk_expected", "/mcp");
        await mw.InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Equal("Unauthorized", ReadBody(body));
    }

    [Fact]
    public async Task Middleware_lets_healthz_through_without_auth()
    {
        // Caddy / Container Apps / docker-compose healthchecks poll /healthz unauthenticated.
        // If we accidentally gate it we'd report "container unhealthy" forever.
        var nextCalled = false;
        var (ctx, _) = TestContext(path: McpBearerAuthMiddleware.HealthPath);
        var mw = new McpBearerAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            "sk_expected", "/mcp");
        await mw.InvokeAsync(ctx);
        Assert.True(nextCalled);
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Middleware_lets_paths_outside_the_protected_prefix_through()
    {
        // Future-proofing — additional endpoints (/api/health, /metrics) outside the MCP
        // mount should not get blanket-auth'd by us. They handle their own policy.
        var nextCalled = false;
        var (ctx, _) = TestContext(path: "/api/something");
        var mw = new McpBearerAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; },
            "sk_expected", "/mcp");
        await mw.InvokeAsync(ctx);
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Middleware_protects_subpaths_under_the_mount()
    {
        // Semantix routes /mcp/clienta — the middleware must protect that nested path too.
        var (ctx, body) = TestContext(path: "/mcp/clienta/sse");
        var mw = new McpBearerAuthMiddleware(_ => Task.CompletedTask, "sk_expected", "/mcp");
        await mw.InvokeAsync(ctx);
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Equal("Unauthorized", ReadBody(body));
    }

    /// <summary>Build a default <see cref="HttpContext"/> with a writable response body
    /// stream. Tests read the body back with <see cref="ReadBody"/>.</summary>
    private static (DefaultHttpContext ctx, MemoryStream body) TestContext(string path, string? auth = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = "POST";
        if (auth is not null) ctx.Request.Headers.Authorization = auth;
        var ms = new MemoryStream();
        ctx.Response.Body = ms;
        return (ctx, ms);
    }

    /// <summary>Decode the response body stream the middleware wrote into.</summary>
    private static string ReadBody(MemoryStream s)
    {
        s.Position = 0;
        return new StreamReader(s, Encoding.UTF8).ReadToEnd();
    }
}
