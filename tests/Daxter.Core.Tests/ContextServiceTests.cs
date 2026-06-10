using Daxter.Core.Artifacts;
using Daxter.Core.Context;

namespace Daxter.Core.Tests;

/// <summary>The Phase 4 <see cref="ContextService"/> — text-first knowledge layer over the
/// artifact store. Pin the invariants: stores under context/ (so /artifacts shows it alongside
/// PBIR exports), surfaces namespaces for capability discovery, search ranks by hit count and
/// snippets show context for each match.</summary>
public sealed class ContextServiceTests : IDisposable
{
    private readonly string _root;
    private readonly LocalArtifactStore _store;
    private readonly ContextService _svc;

    public ContextServiceTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "daxter-context-test-" + Guid.NewGuid().ToString("N"));
        _store = new LocalArtifactStore(_root, quotaBytes: 10 * 1024 * 1024);
        _svc = new ContextService(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task Put_then_Get_round_trips_text_content()
    {
        var written = await _svc.PutAsync("clients/acme/glossary.md", "ACME = our biggest client.\nTLA = trailing last audit.", sourceTool: "team-curator");
        Assert.Equal("clients/acme/glossary.md", written.Key);
        Assert.Equal("clients/acme", written.Namespace);
        Assert.Equal("team-curator", written.SourceTool);

        var (content, entry, tooBig) = await _svc.GetAsync("clients/acme/glossary.md");
        Assert.False(tooBig);
        Assert.NotNull(content);
        Assert.Contains("ACME = our biggest client", content);
        Assert.Equal("clients/acme/glossary.md", entry.Key);
    }

    [Fact]
    public async Task Put_stores_under_context_prefix_in_the_underlying_artifact_store()
    {
        await _svc.PutAsync("conventions/never-touch-prod.md", "never write to prod without explicit approval");

        // The artifact store should show it under context/conventions/... — the file plane
        // and knowledge plane share one bucket, separated by the reserved prefix.
        var artifacts = await _store.ListAsync("context/conventions/");
        Assert.Single(artifacts);
        Assert.Equal("context/conventions/never-touch-prod.md", artifacts[0].Key);
    }

    [Fact]
    public async Task Get_on_missing_key_throws_with_the_callers_key()
    {
        var ex = await Assert.ThrowsAsync<DaxterException>(() => _svc.GetAsync("clients/no/such.md"));
        // Error should reference the caller's key (no internal prefix), so the agent's error
        // surface doesn't leak the implementation detail.
        Assert.Contains("clients/no/such.md", ex.Message);
    }

    [Fact]
    public async Task Put_rejects_a_key_that_already_starts_with_context_prefix()
    {
        // A caller passing "context/..." would double-prefix and silently land at
        // context/context/... — refuse loudly.
        var ex = await Assert.ThrowsAsync<DaxterException>(() =>
            _svc.PutAsync("context/clients/acme/x.md", "noop"));
        Assert.Contains("WITHOUT", ex.Message);
    }

    [Fact]
    public async Task List_without_namespace_returns_every_entry()
    {
        await _svc.PutAsync("clients/acme/glossary.md", "A");
        await _svc.PutAsync("skills/dax/patterns.md", "B");
        await _svc.PutAsync("workspaces/data-hub/notes.md", "C");

        var all = await _svc.ListAsync();
        Assert.Equal(3, all.Count);
        Assert.All(all, e => Assert.False(e.Key.StartsWith("context/")));
    }

    [Fact]
    public async Task List_with_namespace_narrows_to_that_subtree()
    {
        await _svc.PutAsync("clients/acme/glossary.md", "A");
        await _svc.PutAsync("clients/acme/conventions.md", "B");
        await _svc.PutAsync("clients/other/glossary.md", "C");

        var acmeOnly = await _svc.ListAsync("clients/acme");
        Assert.Equal(2, acmeOnly.Count);
        Assert.All(acmeOnly, e => Assert.StartsWith("clients/acme/", e.Key));
    }

    [Fact]
    public async Task Namespaces_groups_and_counts_top_level_paths()
    {
        await _svc.PutAsync("clients/acme/glossary.md", "A");
        await _svc.PutAsync("clients/acme/conventions.md", "B");
        await _svc.PutAsync("skills/dax/patterns.md", "C");
        await _svc.PutAsync("conventions/global.md", "D");

        var ns = await _svc.NamespacesAsync();
        var byName = ns.ToDictionary(s => s.Namespace);
        Assert.Equal(2, byName["clients/acme"].KeyCount);
        Assert.Equal(1, byName["skills/dax"].KeyCount);
        Assert.Equal(1, byName["conventions"].KeyCount);
    }

    [Fact]
    public async Task Search_returns_hits_sorted_by_match_count_with_snippets()
    {
        await _svc.PutAsync("clients/acme/glossary.md",
            "ACME uses TLA throughout. TLA = trailing last audit. See TLA for definitions.");
        await _svc.PutAsync("skills/dax/patterns.md",
            "TLA is the time-to-live abbreviation in DAX patterns.");
        await _svc.PutAsync("conventions/global.md",
            "Always escape apostrophes in DAX. No mention of the abbreviation here.");

        var hits = await _svc.SearchAsync("TLA");
        Assert.Equal(2, hits.Count);
        // Most matches first.
        Assert.Equal("clients/acme/glossary.md", hits[0].Key);
        Assert.True(hits[0].MatchCount >= hits[1].MatchCount);
        // Snippet shows surrounding text.
        Assert.Contains("TLA", hits[0].Snippet);
        // Last entry has no match — must not be in results.
        Assert.DoesNotContain(hits, h => h.Key == "conventions/global.md");
    }

    [Fact]
    public async Task Search_matches_keys_too_not_just_bodies()
    {
        await _svc.PutAsync("clients/acme/TLA-glossary.md", "Body without the abbreviation.");
        var hits = await _svc.SearchAsync("TLA");
        Assert.Single(hits);
        Assert.Equal("clients/acme/TLA-glossary.md", hits[0].Key);
    }

    [Fact]
    public async Task Search_with_empty_query_returns_empty()
    {
        await _svc.PutAsync("a.md", "hello");
        Assert.Empty(await _svc.SearchAsync(""));
        Assert.Empty(await _svc.SearchAsync("   "));
    }

    [Fact]
    public async Task Delete_removes_just_the_targeted_key()
    {
        await _svc.PutAsync("clients/acme/glossary.md", "A");
        await _svc.PutAsync("clients/acme/conventions.md", "B");

        var removed = await _svc.DeleteAsync("clients/acme/glossary.md");
        Assert.Equal(1, removed);
        var remaining = await _svc.ListAsync("clients/acme");
        Assert.Single(remaining);
        Assert.Equal("clients/acme/conventions.md", remaining[0].Key);
    }

    [Fact]
    public async Task Delete_with_namespace_removes_the_whole_subtree()
    {
        await _svc.PutAsync("clients/acme/glossary.md", "A");
        await _svc.PutAsync("clients/acme/conventions.md", "B");
        await _svc.PutAsync("clients/other/glossary.md", "C");

        var removed = await _svc.DeleteAsync("clients/acme");
        Assert.Equal(2, removed);

        Assert.Empty(await _svc.ListAsync("clients/acme"));
        Assert.Single(await _svc.ListAsync("clients/other"));
    }

    [Fact]
    public async Task Get_on_oversize_entry_returns_too_large_flag()
    {
        // Put a body slightly above the inline limit.
        var big = new string('x', ContextService.InlineBytesLimit + 1024);
        await _svc.PutAsync("dump/huge.md", big);
        var (content, entry, tooBig) = await _svc.GetAsync("dump/huge.md");
        Assert.True(tooBig);
        Assert.Null(content);
        Assert.True(entry.Bytes > ContextService.InlineBytesLimit);
    }

    [Fact]
    public async Task Put_with_TTL_persists_the_expiry()
    {
        await _svc.PutAsync("incidents/INC123/findings.md", "investigation notes", ttlHours: 24);
        var entries = await _svc.ListAsync("incidents/INC123");
        Assert.NotNull(entries[0].ExpiresAt);
        Assert.True(entries[0].ExpiresAt!.Value > DateTime.UtcNow);
        Assert.True(entries[0].ExpiresAt!.Value < DateTime.UtcNow.AddHours(25));
    }
}
