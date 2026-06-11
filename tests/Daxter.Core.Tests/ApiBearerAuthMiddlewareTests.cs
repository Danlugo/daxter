using System.Text;
using Daxter.Web.Services;
using Microsoft.AspNetCore.Http;

namespace Daxter.Core.Tests;

/// <summary>The v1.40.0 Web /api/* bearer gate. Its security-relevant difference from the MCP
/// middleware is the OPT-IN semantic: when DAXTER_WEB_BEARER_TOKEN is unset the gate is OFF
/// (localhost-trusted default, back-compat); when set, the dangerous endpoints require the bearer
/// and /api/health stays open. Pinned here so a refactor can't silently flip the default to
/// "open even when a token is configured" or "closed when no token" (which would break every
/// existing localhost deployment).</summary>
public sealed class ApiBearerAuthMiddlewareTests
{
    private static (DefaultHttpContext ctx, MemoryStream body) Ctx(string path, string? auth = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Path = path;
        ctx.Request.Method = "POST";
        if (auth is not null) ctx.Request.Headers.Authorization = auth;
        var ms = new MemoryStream();
        ctx.Response.Body = ms;
        return (ctx, ms);
    }

    private static string ReadBody(MemoryStream s) { s.Position = 0; return new StreamReader(s, Encoding.UTF8).ReadToEnd(); }

    /// <summary>Run the middleware with the env var set to <paramref name="token"/> (or unset when
    /// null), restoring it afterwards so tests don't leak state.</summary>
    private static async Task<bool> Invoke(string? token, HttpContext ctx)
    {
        var prev = Environment.GetEnvironmentVariable(ApiBearerAuthMiddleware.EnvVarName);
        var nextCalled = false;
        try
        {
            Environment.SetEnvironmentVariable(ApiBearerAuthMiddleware.EnvVarName, token);
            var mw = new ApiBearerAuthMiddleware(_ => { nextCalled = true; return Task.CompletedTask; });
            await mw.InvokeAsync(ctx);
        }
        finally { Environment.SetEnvironmentVariable(ApiBearerAuthMiddleware.EnvVarName, prev); }
        return nextCalled;
    }

    // ── Gate DISABLED (no token) → everything passes (localhost-trusted default) ──────────────
    [Fact]
    public async Task No_token_configured_passes_protected_path_through()
    {
        var (ctx, _) = Ctx("/api/sql/export");
        Assert.True(await Invoke(null, ctx));     // gate off → next() called
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    // ── Gate ENABLED (token set) → protected paths require the bearer ─────────────────────────
    [Fact]
    public async Task Token_configured_rejects_sql_export_without_bearer()
    {
        var (ctx, body) = Ctx("/api/sql/export");
        Assert.False(await Invoke("sk_web_secret", ctx));   // next() NOT called
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
        Assert.Equal("Unauthorized", ReadBody(body));
        Assert.Equal("Bearer realm=\"daxter-web\"", ctx.Response.Headers["WWW-Authenticate"]);
    }

    [Fact]
    public async Task Token_configured_rejects_artifacts_with_wrong_bearer()
    {
        var (ctx, _) = Ctx("/api/artifacts/reports/x.json", auth: "Bearer sk_wrong");
        Assert.False(await Invoke("sk_web_secret", ctx));
        Assert.Equal(StatusCodes.Status401Unauthorized, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Token_configured_accepts_correct_bearer_on_protected_path()
    {
        var (ctx, _) = Ctx("/api/artifacts/reports/x.json", auth: "Bearer sk_web_secret");
        Assert.True(await Invoke("sk_web_secret", ctx));
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    // ── /api/health is ALWAYS open, even when the gate is on ──────────────────────────────────
    [Fact]
    public async Task Health_endpoint_open_even_with_token_configured()
    {
        var (ctx, _) = Ctx("/api/health");
        Assert.True(await Invoke("sk_web_secret", ctx));    // passes through, no auth
        Assert.Equal(StatusCodes.Status200OK, ctx.Response.StatusCode);
    }

    // ── Non-/api paths (the Blazor circuit, static files) are never gated here ────────────────
    [Fact]
    public async Task Blazor_and_static_paths_pass_through_even_with_token()
    {
        foreach (var path in new[] { "/", "/sql", "/_blazor", "/app.css" })
        {
            var (ctx, _) = Ctx(path);
            Assert.True(await Invoke("sk_web_secret", ctx), $"path {path} should pass through");
        }
    }
}
