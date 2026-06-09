using System.IO.Compression;
using System.Text;
using Daxter.Core.Artifacts;

namespace Daxter.Core.Tests;

/// <summary>The <see cref="LocalArtifactStore"/> is the transport-agnostic file plane that every
/// file-shaped DAXter feature (export_report, sql_export, future update_*_definition) passes
/// through. Pin the invariants here so the surfaces (CLI / MCP / Web HTTP) can't drift apart, and
/// so the security-critical key-sanitisation never silently regresses.</summary>
public sealed class ArtifactStoreTests : IDisposable
{
    private readonly string _root;
    private readonly LocalArtifactStore _store;

    public ArtifactStoreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "daxter-artifacts-test-" + Guid.NewGuid().ToString("N"));
        _store = new LocalArtifactStore(_root, quotaBytes: 10 * 1024 * 1024);  // 10MB cap for tests
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    // ── Put / Get round-trip ──────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Put_then_Get_returns_same_bytes()
    {
        var payload = Encoding.UTF8.GetBytes("hello, artifacts");
        await _store.PutAsync("reports/sales/page.json", new MemoryStream(payload));
        await using var s = await _store.OpenReadAsync("reports/sales/page.json");
        var buf = new byte[payload.Length];
        var read = await s.ReadAsync(buf);
        Assert.Equal(payload.Length, read);
        Assert.Equal(payload, buf);
    }

    [Fact]
    public async Task Put_persists_TTL_and_source_tool()
    {
        var expires = DateTime.UtcNow.AddHours(1);
        await _store.PutAsync("notebooks/x/n.ipynb",
            new MemoryStream(Encoding.UTF8.GetBytes("{}")),
            new ArtifactMeta(ExpiresAt: expires, SourceTool: "daxter_notebook_definition"));

        var meta = await _store.GetMetaAsync("notebooks/x/n.ipynb");
        Assert.NotNull(meta);
        Assert.Equal("daxter_notebook_definition", meta!.SourceTool);
        Assert.NotNull(meta.ExpiresAt);
        Assert.Equal(expires, meta.ExpiresAt!.Value, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task Get_on_missing_key_throws_FileNotFoundException()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.OpenReadAsync("does/not/exist"));
    }

    // ── ListAsync — prefix semantics ──────────────────────────────────────────────────────────
    [Fact]
    public async Task List_with_prefix_filters_to_matching_keys()
    {
        await _store.PutAsync("reports/a/x.json", new MemoryStream(new byte[10]));
        await _store.PutAsync("reports/a/y.json", new MemoryStream(new byte[20]));
        await _store.PutAsync("reports/b/x.json", new MemoryStream(new byte[30]));
        await _store.PutAsync("notebooks/n.ipynb", new MemoryStream(new byte[40]));

        var aOnly = await _store.ListAsync("reports/a");
        Assert.Equal(2, aOnly.Count);
        Assert.All(aOnly, r => Assert.StartsWith("reports/a/", r.Key));

        var all = await _store.ListAsync();
        Assert.Equal(4, all.Count);
        // Deterministic sort — agent + UI rely on this.
        Assert.Equal(all.OrderBy(r => r.Key, StringComparer.Ordinal).Select(r => r.Key), all.Select(r => r.Key));
    }

    [Fact]
    public async Task List_excludes_the_metadata_index()
    {
        await _store.PutAsync("a.txt", new MemoryStream(new byte[5]),
            new ArtifactMeta(SourceTool: "test"));  // forces an index write
        Assert.True(File.Exists(Path.Combine(_root, LocalArtifactStore.IndexFileName)),
            "sanity: the index file should exist after a meta-tagged put");
        var listed = await _store.ListAsync();
        Assert.Single(listed);
        Assert.Equal("a.txt", listed[0].Key);
    }

    // ── Bundle — zip of a prefix ──────────────────────────────────────────────────────────────
    [Fact]
    public async Task Bundle_zips_every_file_under_the_prefix_with_relative_entry_paths()
    {
        await _store.PutAsync("reports/sales/definition/pages/01/visual.json",
            new MemoryStream(Encoding.UTF8.GetBytes("{\"a\":1}")));
        await _store.PutAsync("reports/sales/report.json",
            new MemoryStream(Encoding.UTF8.GetBytes("{\"b\":2}")));
        await _store.PutAsync("reports/other/x.json",
            new MemoryStream(Encoding.UTF8.GetBytes("ignore me")));

        await using var bundle = await _store.OpenBundleAsync("reports/sales");
        using var zip = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: false);
        var names = zip.Entries.Select(e => e.FullName).OrderBy(s => s).ToList();

        // Other/x.json must NOT be in the bundle.
        Assert.Equal(2, names.Count);
        // Entry paths are relative to the bundled prefix, not to the store root.
        Assert.Contains("definition/pages/01/visual.json", names);
        Assert.Contains("report.json", names);
    }

    [Fact]
    public async Task Bundle_then_Extract_round_trips_into_a_new_prefix()
    {
        await _store.PutAsync("src/a.json", new MemoryStream(Encoding.UTF8.GetBytes("A")));
        await _store.PutAsync("src/sub/b.json", new MemoryStream(Encoding.UTF8.GetBytes("BB")));

        await using var bundle = await _store.OpenBundleAsync("src");
        var extracted = await _store.ExtractAsync("dst", bundle);

        Assert.Equal(2, extracted.Count);
        var dstA = await _store.GetMetaAsync("dst/a.json");
        var dstB = await _store.GetMetaAsync("dst/sub/b.json");
        Assert.NotNull(dstA);
        Assert.NotNull(dstB);
        Assert.Equal(1, dstA!.Bytes);
        Assert.Equal(2, dstB!.Bytes);
    }

    [Fact]
    public async Task Bundle_on_unknown_prefix_throws_FileNotFoundException()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _store.OpenBundleAsync("nothing/here"));
    }

    // ── Delete (recursive prefix) ─────────────────────────────────────────────────────────────
    [Fact]
    public async Task Delete_with_prefix_removes_every_member()
    {
        await _store.PutAsync("group/a.json", new MemoryStream(new byte[3]));
        await _store.PutAsync("group/sub/b.json", new MemoryStream(new byte[3]));
        await _store.PutAsync("other/c.json", new MemoryStream(new byte[3]));

        var removed = await _store.DeleteAsync("group");
        Assert.Equal(2, removed);
        Assert.Empty(await _store.ListAsync("group"));
        Assert.Single(await _store.ListAsync("other"));
    }

    [Fact]
    public async Task Delete_removes_TTL_entries_from_the_index()
    {
        await _store.PutAsync("a.txt", new MemoryStream(new byte[3]),
            new ArtifactMeta(SourceTool: "test"));
        await _store.DeleteAsync("a.txt");
        var meta = await _store.GetMetaAsync("a.txt");
        Assert.Null(meta);
    }

    // ── TTL purge ─────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task PurgeExpired_removes_expired_only()
    {
        await _store.PutAsync("ephemeral.txt", new MemoryStream(new byte[100]),
            new ArtifactMeta(ExpiresAt: DateTime.UtcNow.AddSeconds(-1)));        // already expired
        await _store.PutAsync("durable.txt", new MemoryStream(new byte[100]),
            new ArtifactMeta(ExpiresAt: DateTime.UtcNow.AddHours(1)));           // still valid

        var freed = await _store.PurgeExpiredAsync();
        Assert.Equal(100, freed);
        Assert.Null(await _store.GetMetaAsync("ephemeral.txt"));
        Assert.NotNull(await _store.GetMetaAsync("durable.txt"));
    }

    [Fact]
    public async Task PurgeExpired_with_nothing_due_returns_zero()
    {
        await _store.PutAsync("a.txt", new MemoryStream(new byte[10]));     // no TTL
        Assert.Equal(0, await _store.PurgeExpiredAsync());
    }

    // ── Quota ─────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public async Task Put_throws_when_quota_would_be_exceeded()
    {
        var tiny = new LocalArtifactStore(_root + "-tiny", quotaBytes: 1024);
        try
        {
            var big = new byte[2048];
            await Assert.ThrowsAsync<ArtifactQuotaExceededException>(
                () => tiny.PutAsync("big.bin", new MemoryStream(big)));

            // After a quota failure the file shouldn't exist (caller can retry without orphans).
            Assert.Empty(await tiny.ListAsync());
        }
        finally
        {
            try { Directory.Delete(_root + "-tiny", recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task CurrentUsage_sums_all_files_excluding_index()
    {
        await _store.PutAsync("a", new MemoryStream(new byte[100]));
        await _store.PutAsync("b/c", new MemoryStream(new byte[50]),
            new ArtifactMeta(SourceTool: "x"));        // triggers index write
        Assert.Equal(150, await _store.CurrentUsageBytesAsync());
    }

    // ── Key sanitisation (security boundary) ──────────────────────────────────────────────────
    [Theory]
    [InlineData("")]
    [InlineData("/abs/path")]
    [InlineData("a/../b")]
    [InlineData("../escape")]
    [InlineData("a/./b")]
    [InlineData("with:colon")]
    [InlineData("with\0null")]
    [InlineData("with\rcr")]
    public async Task Invalid_keys_are_refused(string badKey)
    {
        await Assert.ThrowsAsync<InvalidArtifactKeyException>(
            () => _store.PutAsync(badKey, new MemoryStream(new byte[3])));
    }

    [Fact]
    public async Task Put_at_the_index_filename_is_refused()
    {
        await Assert.ThrowsAsync<InvalidArtifactKeyException>(
            () => _store.PutAsync(LocalArtifactStore.IndexFileName, new MemoryStream(new byte[3])));
    }

    [Fact]
    public async Task Backslash_keys_normalize_to_forward_slash()
    {
        // Agents on Windows may send backslash paths — accept them, normalise on the way in.
        await _store.PutAsync("a\\b\\c.txt", new MemoryStream(new byte[5]));
        var list = await _store.ListAsync("a/b");
        Assert.Single(list);
        Assert.Equal("a/b/c.txt", list[0].Key);
    }

    // ── Default root resolution ───────────────────────────────────────────────────────────────
    [Fact]
    public void ResolveRoot_honors_DAXTER_ARTIFACTS_ROOT()
    {
        var prior = Environment.GetEnvironmentVariable("DAXTER_ARTIFACTS_ROOT");
        try
        {
            Environment.SetEnvironmentVariable("DAXTER_ARTIFACTS_ROOT", "/tmp/some-explicit-root");
            Assert.Equal("/tmp/some-explicit-root", LocalArtifactStore.ResolveRoot());
        }
        finally { Environment.SetEnvironmentVariable("DAXTER_ARTIFACTS_ROOT", prior); }
    }

    [Fact]
    public void ResolveQuotaBytes_honors_DAXTER_ARTIFACTS_QUOTA_MB()
    {
        var prior = Environment.GetEnvironmentVariable("DAXTER_ARTIFACTS_QUOTA_MB");
        try
        {
            Environment.SetEnvironmentVariable("DAXTER_ARTIFACTS_QUOTA_MB", "256");
            Assert.Equal(256L * 1024 * 1024, LocalArtifactStore.ResolveQuotaBytes());
            Environment.SetEnvironmentVariable("DAXTER_ARTIFACTS_QUOTA_MB", "garbage");
            Assert.Equal(LocalArtifactStore.DefaultQuotaBytes, LocalArtifactStore.ResolveQuotaBytes());
        }
        finally { Environment.SetEnvironmentVariable("DAXTER_ARTIFACTS_QUOTA_MB", prior); }
    }
}
