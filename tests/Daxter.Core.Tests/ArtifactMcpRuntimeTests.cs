using System.IO.Compression;
using System.Text;
using Daxter.Cli.Mcp;
using Daxter.Core.Artifacts;

namespace Daxter.Core.Tests;

/// <summary>The Phase 2 MCP-runtime helpers — <see cref="DaxterToolRuntime.ArtifactPutAsync"/> and
/// <see cref="DaxterToolRuntime.ArtifactExtractAsync"/>. Same Core API as the Phase 1 tests cover,
/// but exercised through the MCP wrappers so the JSON envelope, base64 decode path, and
/// dual-mode (content vs URL) validation can't silently regress.
///
/// These talk to the process-wide <see cref="DaxterToolRuntime.Artifacts"/> singleton — same
/// instance the real MCP server uses. We can't isolate per-test (the Lazy singleton resolves
/// once on first access), so each test uses a unique key prefix to avoid stepping on neighbours.
/// </summary>
public sealed class ArtifactMcpRuntimeTests
{
    private static string TestKey(string suffix) => $"mcp-runtime-tests/{Guid.NewGuid():N}/{suffix}";

    [Fact]
    public async Task Put_inline_base64_returns_ArtifactRef_envelope()
    {
        var key = TestKey("payload.json");
        var content = Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"hello\":\"artifacts\"}"));

        var json = await DaxterToolRuntime.ArtifactPutAsync(
            key, contentBase64: content, fetchUrl: null,
            ttlHours: null, sourceTool: "mcp-runtime-test", ct: default);

        Assert.Contains(key, json);
        Assert.Contains("\"bytes\":", json);
        Assert.Contains("\"source_tool\": \"mcp-runtime-test\"", json);

        // Round-trip the bytes via OpenReadAsync to confirm they actually landed.
        await using var read = await DaxterToolRuntime.Artifacts.OpenReadAsync(key);
        using var ms = new MemoryStream();
        await read.CopyToAsync(ms);
        Assert.Equal("{\"hello\":\"artifacts\"}", Encoding.UTF8.GetString(ms.ToArray()));

        await DaxterToolRuntime.Artifacts.DeleteAsync(key);    // cleanup
    }

    [Fact]
    public async Task Put_with_both_content_and_url_is_refused()
    {
        var key = TestKey("x.bin");
        var result = await DaxterToolRuntime.ArtifactPutAsync(
            key, contentBase64: "AA==", fetchUrl: "https://example.test/x", null, null, default);
        Assert.Contains("Pass exactly one of contentBase64 (inline) OR fetchUrl", result);
    }

    [Fact]
    public async Task Put_with_neither_content_nor_url_is_refused()
    {
        var key = TestKey("x.bin");
        var result = await DaxterToolRuntime.ArtifactPutAsync(
            key, contentBase64: null, fetchUrl: null, null, null, default);
        Assert.Contains("Pass exactly one of", result);
    }

    [Fact]
    public async Task Put_with_invalid_base64_surfaces_a_clean_message()
    {
        var key = TestKey("x.bin");
        var result = await DaxterToolRuntime.ArtifactPutAsync(
            key, contentBase64: "not-base64!", fetchUrl: null, null, null, default);
        // Guard() catches the DaxterException and returns its message verbatim.
        Assert.Contains("not valid base64", result);
    }

    [Fact]
    public async Task Put_with_invalid_key_surfaces_a_clean_message()
    {
        var content = Convert.ToBase64String(new byte[] { 1, 2, 3 });
        var result = await DaxterToolRuntime.ArtifactPutAsync(
            key: "../escape", contentBase64: content, fetchUrl: null, null, null, default);
        Assert.Contains("Invalid artifact key", result);
    }

    [Fact]
    public async Task Extract_inline_zip_round_trips_into_a_prefix()
    {
        // Build a small in-memory zip with two entries.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await using (var s = zip.CreateEntry("a.txt").Open()) await s.WriteAsync(Encoding.UTF8.GetBytes("AA"));
            await using (var s = zip.CreateEntry("sub/b.txt").Open()) await s.WriteAsync(Encoding.UTF8.GetBytes("BBB"));
        }
        var base64 = Convert.ToBase64String(ms.ToArray());

        var prefix = TestKey("extracted").TrimEnd('/');
        var json = await DaxterToolRuntime.ArtifactExtractAsync(
            prefix, zipBase64: base64, fetchUrl: null,
            ttlHours: null, sourceTool: "mcp-runtime-test", ct: default);

        Assert.Contains("\"extracted\": 2", json);
        Assert.Contains($"{prefix}/a.txt", json);
        Assert.Contains($"{prefix}/sub/b.txt", json);

        await DaxterToolRuntime.Artifacts.DeleteAsync(prefix);     // cleanup
    }

    [Fact]
    public async Task Extract_with_neither_content_nor_url_is_refused()
    {
        var prefix = TestKey("nothing");
        var result = await DaxterToolRuntime.ArtifactExtractAsync(
            prefix, zipBase64: null, fetchUrl: null, null, null, default);
        Assert.Contains("Pass exactly one of", result);
    }
}
