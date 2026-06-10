using System.Text.Json;
using Daxter.Cli.Mcp;

namespace Daxter.Core.Tests;

/// <summary>The <c>daxter_version</c> MCP tool (and the matching <c>daxter version</c> CLI verb)
/// is the cheap discovery path — agents call it to confirm which build they're talking to
/// across sessions. Pin the envelope shape so a renamed field doesn't silently break agents
/// that match on JSON keys.</summary>
public sealed class VersionToolTests
{
    [Fact]
    public async Task VersionAsync_returns_required_fields_without_check()
    {
        // Set a known DAXTER_VERSION for the assertion; restore on the way out so a test that
        // runs after this still sees what it expects.
        var prev = Environment.GetEnvironmentVariable("DAXTER_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("DAXTER_VERSION", "v1.99.7");
            var json = await DaxterToolRuntime.VersionAsync(checkLatest: false, default);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Assert.Equal("v1.99.7", root.GetProperty("version").GetString());
            Assert.Equal("ghcr.io/danlugo/daxter:v1.99.7", root.GetProperty("image").GetString());
            Assert.Equal("https://github.com/Danlugo/daxter", root.GetProperty("repo_url").GetString());
            Assert.Equal("https://github.com/Danlugo/daxter/releases", root.GetProperty("releases_url").GetString());
            Assert.True(root.TryGetProperty("dotnet_version", out _), "expected dotnet_version field");
            Assert.True(root.TryGetProperty("platform", out _), "expected platform field");
            Assert.True(root.TryGetProperty("architecture", out _), "expected architecture field");
            // With checkLatest=false, the update-check fields must NOT be present so a caller
            // that's checking for an offline path doesn't accidentally read stale data.
            Assert.False(root.TryGetProperty("latest_published", out _));
            Assert.False(root.TryGetProperty("update_available", out _));
        }
        finally { Environment.SetEnvironmentVariable("DAXTER_VERSION", prev); }
    }

    [Fact]
    public async Task VersionAsync_dev_label_when_env_var_missing()
    {
        var prev = Environment.GetEnvironmentVariable("DAXTER_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("DAXTER_VERSION", null);
            var json = await DaxterToolRuntime.VersionAsync(checkLatest: false, default);
            using var doc = JsonDocument.Parse(json);
            Assert.Equal("dev", doc.RootElement.GetProperty("version").GetString());
            Assert.Equal("ghcr.io/danlugo/daxter:dev", doc.RootElement.GetProperty("image").GetString());
        }
        finally { Environment.SetEnvironmentVariable("DAXTER_VERSION", prev); }
    }

    [Fact]
    public async Task VersionAsync_honors_DAXTER_REPO_for_forks()
    {
        var prevRepo = Environment.GetEnvironmentVariable("DAXTER_REPO");
        var prevVer = Environment.GetEnvironmentVariable("DAXTER_VERSION");
        try
        {
            Environment.SetEnvironmentVariable("DAXTER_REPO", "AcmeCorp/Daxter-Fork");
            Environment.SetEnvironmentVariable("DAXTER_VERSION", "v0.1.0");
            var json = await DaxterToolRuntime.VersionAsync(checkLatest: false, default);
            using var doc = JsonDocument.Parse(json);
            // Image URL is lowercased per OCI conventions; repo URL preserves case for humans.
            Assert.Equal("ghcr.io/acmecorp/daxter-fork:v0.1.0", doc.RootElement.GetProperty("image").GetString());
            Assert.Equal("https://github.com/AcmeCorp/Daxter-Fork", doc.RootElement.GetProperty("repo_url").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("DAXTER_REPO", prevRepo);
            Environment.SetEnvironmentVariable("DAXTER_VERSION", prevVer);
        }
    }
}
