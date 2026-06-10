using System.Text;
using Daxter.Core.Artifacts;

namespace Daxter.Core.Context;

// ──────────────────────────────────────────────────────────────────────────────────────────────
// DAXter Context — the shared-knowledge layer.
//
// PROBLEM. The artifact store is a generic key→bytes plane. Anything can live in it. But for the
// "team knowledge / client glossary / reusable skill" use case, agents need an ergonomic surface
// that:
//   - speaks TEXT, not base64 (these payloads are markdown, prose, prompts — never .pbix bytes)
//   - is DISCOVERABLE without inspecting random keys
//   - supports SEARCH across bodies, not just metadata
//   - is NAMESPACED so "client glossary" doesn't sit next to "PBIR export"
//
// SOLUTION. A thin Context layer on top of IArtifactStore. Stores under the reserved
// `context/` key prefix. Returns content as text (UTF-8). Adds a search-bodies method the
// artifact API doesn't expose because most file-shaped payloads aren't searchable text.
//
// One bucket, two surfaces: agents use the FILE plane (daxter_artifact_*) for byte transfer and
// the KNOWLEDGE plane (daxter_context_*) for shared facts. Storage is shared — bytes written by
// one surface are visible to the other, with `context/` acting as a prefix convention rather
// than a separate store.
//
// NAMESPACES. Top-level segments under `context/` are conventional, not enforced. Examples we
// document and seed in CHANGELOG / examples:
//   context/clients/<client>/...   — per-client glossaries, conventions
//   context/workspaces/<ws>/...    — per-workspace context cards (auto-attached to daxter_query)
//   context/endpoints/<ep>/...     — per-Fabric-SQL-endpoint cards (auto-attached to daxter_sql_query)
//   context/skills/<topic>/...     — reusable knowledge snippets (DAX patterns, SQL cheatsheets)
//   context/conventions/...        — global rules ("never write to PROD", "use star schema")
//   context/prompts/...            — Phase 5: MCP-prompt templates surfaced in Claude Desktop's UI
//
// Phase 4 ships list/get/put/search + auto-attach in the query tools. Phase 5 adds MCP prompts +
// a /context Razor page so humans can curate without the CLI.
// ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>One context entry — what daxter_context_list returns. The body is NOT included in
/// the list response (a 5MB glossary would blow the agent's context budget on a list call); use
/// <see cref="ContextService.GetAsync"/> to fetch it.</summary>
public sealed record ContextEntry(
    string Key,
    string Namespace,
    long Bytes,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    string? SourceTool);

/// <summary>A namespace summary — surfaced by daxter_capabilities so brand-new agents see the
/// knowledge plane on their very first tool call.</summary>
public sealed record ContextNamespaceSummary(
    string Namespace,
    int KeyCount,
    DateTime? LastUpdated);

/// <summary>One search hit — the matched key plus a small snippet of surrounding text so the
/// agent can decide whether to fetch the full body.</summary>
public sealed record ContextSearchHit(
    string Key,
    string Namespace,
    string Snippet,
    int MatchCount);

/// <summary>Text-first knowledge layer over <see cref="IArtifactStore"/>. Every method below is
/// a thin convenience wrapper over the underlying store; you can replicate any of it directly
/// via the artifact tools if you prefer — that's the point of building on the artifact API
/// rather than a parallel persistence layer.</summary>
public sealed class ContextService
{
    /// <summary>The reserved prefix every context entry lives under. Acts as the namespace
    /// separator vs. PBIR exports / SQL CSVs / etc. on the same store.</summary>
    public const string RootPrefix = "context/";

    /// <summary>Max byte size we'll surface to the agent in a single Get call. Larger entries
    /// are flagged with a `too_large_for_inline` field and a hint to fetch via the artifact
    /// HTTP endpoint. 1 MB is plenty for any prose context; if you find yourself wanting more,
    /// you probably want a `daxter_artifact_*` payload instead.</summary>
    public const int InlineBytesLimit = 1 * 1024 * 1024;

    /// <summary>Max bytes scanned per artifact during a search. A pathological 50MB markdown
    /// blob shouldn't burn the call. Search is intentionally cheap-and-shallow; the agent can
    /// follow up with GetAsync once it's narrowed to a hit.</summary>
    public const int SearchScanLimit = 256 * 1024;

    private readonly IArtifactStore _store;

    public ContextService(IArtifactStore store) => _store = store;

    /// <summary>List context entries, optionally narrowed by a namespace under <c>context/</c>.
    /// Returns metadata only — call <see cref="GetAsync"/> for the body.</summary>
    public async Task<IReadOnlyList<ContextEntry>> ListAsync(string? namespacePath = null, CancellationToken ct = default)
    {
        var prefix = string.IsNullOrWhiteSpace(namespacePath)
            ? RootPrefix
            : $"{RootPrefix}{namespacePath.Trim('/').TrimEnd('/')}/";
        var raw = await _store.ListAsync(prefix, ct);
        var entries = new List<ContextEntry>(raw.Count);
        foreach (var a in raw)
        {
            entries.Add(new ContextEntry(
                Key: StripRoot(a.Key),
                Namespace: ExtractNamespace(a.Key),
                Bytes: a.Bytes,
                CreatedAt: a.CreatedAt,
                ExpiresAt: a.ExpiresAt,
                SourceTool: a.SourceTool));
        }
        return entries;
    }

    /// <summary>Fetch a single context entry's full text. Throws if the key doesn't exist.
    /// Returns null content + <c>tooLargeForInline = true</c> when the body exceeds
    /// <see cref="InlineBytesLimit"/> — agent should then use the artifact HTTP endpoint.</summary>
    public async Task<(string? Content, ContextEntry Entry, bool TooLargeForInline)> GetAsync(string key, CancellationToken ct = default)
    {
        var fullKey = ToFullKey(key);
        var meta = await _store.GetMetaAsync(fullKey, ct)
            ?? throw new DaxterException($"Context entry not found: {key}");

        var entry = new ContextEntry(
            Key: StripRoot(meta.Key),
            Namespace: ExtractNamespace(meta.Key),
            Bytes: meta.Bytes,
            CreatedAt: meta.CreatedAt,
            ExpiresAt: meta.ExpiresAt,
            SourceTool: meta.SourceTool);

        if (meta.Bytes > InlineBytesLimit)
            return (null, entry, true);

        await using var stream = await _store.OpenReadAsync(fullKey, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var content = await reader.ReadToEndAsync(ct);
        return (content, entry, false);
    }

    /// <summary>Create or replace a context entry. Content is text (UTF-8); we store it via the
    /// underlying byte API. <paramref name="sourceTool"/> defaults to a generic label — pass
    /// something specific ("team-curator", "powerbi_alignment_fix") so /artifacts surfaces
    /// provenance.</summary>
    public async Task<ContextEntry> PutAsync(
        string key, string content,
        string? sourceTool = null, double? ttlHours = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new DaxterException("context key is required (e.g. 'clients/acme/glossary.md').");
        // Reject keys that start with another root segment — a `key = "artifacts/..."` would
        // bypass the namespace convention. The artifact store's own key sanitisation handles
        // the harder cases (path traversal, control chars).
        if (key.TrimStart('/').StartsWith(RootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new DaxterException($"Pass the key WITHOUT the '{RootPrefix}' prefix — it's added automatically.");

        var fullKey = ToFullKey(key);
        var meta = new ArtifactMeta(
            ExpiresAt: ttlHours is { } h && h > 0 ? DateTime.UtcNow.AddHours(h) : null,
            SourceTool: sourceTool ?? "daxter_context");
        await using var ms = new MemoryStream(Encoding.UTF8.GetBytes(content));
        var aref = await _store.PutAsync(fullKey, ms, meta, ct);
        return new ContextEntry(
            Key: StripRoot(aref.Key),
            Namespace: ExtractNamespace(aref.Key),
            Bytes: aref.Bytes,
            CreatedAt: aref.CreatedAt,
            ExpiresAt: aref.ExpiresAt,
            SourceTool: aref.SourceTool);
    }

    /// <summary>Delete a single context entry (or every entry under a sub-namespace).
    /// Returns the count removed.</summary>
    public async Task<int> DeleteAsync(string keyOrNamespace, CancellationToken ct = default)
    {
        var fullKey = ToFullKey(keyOrNamespace);
        return await _store.DeleteAsync(fullKey, ct);
    }

    /// <summary>Case-insensitive substring search across both keys AND bodies. Each hit comes
    /// back with a small snippet (the matched line in context) so the agent can decide if it's
    /// worth a full GetAsync. Pathological-size bodies are scanned up to
    /// <see cref="SearchScanLimit"/> bytes.</summary>
    public async Task<IReadOnlyList<ContextSearchHit>> SearchAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return Array.Empty<ContextSearchHit>();
        var entries = await _store.ListAsync(RootPrefix, ct);
        var hits = new List<ContextSearchHit>();
        foreach (var entry in entries)
        {
            ct.ThrowIfCancellationRequested();
            var keyMatches = entry.Key.Contains(query, StringComparison.OrdinalIgnoreCase);

            // Bound the body read — we don't want one rogue 50MB markdown to OOM the search.
            string? body = null;
            int bodyMatches = 0;
            string? snippet = null;
            if (entry.Bytes <= SearchScanLimit)
            {
                await using var stream = await _store.OpenReadAsync(entry.Key, ct);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                body = await reader.ReadToEndAsync(ct);
                bodyMatches = CountSubstring(body, query);
                if (bodyMatches > 0) snippet = ExtractSnippet(body, query);
            }

            if (keyMatches || bodyMatches > 0)
            {
                hits.Add(new ContextSearchHit(
                    Key: StripRoot(entry.Key),
                    Namespace: ExtractNamespace(entry.Key),
                    Snippet: snippet ?? "(key match; body too large to scan)",
                    MatchCount: bodyMatches + (keyMatches ? 1 : 0)));
            }
        }
        // Most-matches first, then deterministic by key for tie-breaks.
        hits.Sort((a, b) =>
        {
            var c = b.MatchCount.CompareTo(a.MatchCount);
            return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
        });
        return hits;
    }

    /// <summary>Group entries by top-level namespace and summarize — used by
    /// <c>daxter_capabilities</c> so a brand-new agent's discovery call surfaces what shared
    /// knowledge the team has curated.</summary>
    public async Task<IReadOnlyList<ContextNamespaceSummary>> NamespacesAsync(CancellationToken ct = default)
    {
        var entries = await _store.ListAsync(RootPrefix, ct);
        var groups = new Dictionary<string, (int Count, DateTime? LastUpdated)>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            ct.ThrowIfCancellationRequested();
            var ns = ExtractNamespace(e.Key);
            if (groups.TryGetValue(ns, out var prev))
            {
                groups[ns] = (prev.Count + 1,
                    prev.LastUpdated is null || e.CreatedAt > prev.LastUpdated ? e.CreatedAt : prev.LastUpdated);
            }
            else
            {
                groups[ns] = (1, e.CreatedAt);
            }
        }
        return groups
            .Select(kv => new ContextNamespaceSummary(kv.Key, kv.Value.Count, kv.Value.LastUpdated))
            .OrderBy(s => s.Namespace, StringComparer.Ordinal)
            .ToList();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Map a caller-visible context key to the full artifact-store key by prepending the
    /// reserved <see cref="RootPrefix"/>. The artifact store does its own sanitisation; we just
    /// add the convention prefix.</summary>
    private static string ToFullKey(string callerKey)
    {
        var trimmed = callerKey.TrimStart('/').TrimEnd('/');
        return RootPrefix + trimmed;
    }

    /// <summary>Inverse of <see cref="ToFullKey"/> for display — returns the key WITHOUT the
    /// reserved prefix, so the caller's "what they typed" stays consistent in API responses.</summary>
    private static string StripRoot(string fullKey)
    {
        if (fullKey.StartsWith(RootPrefix, StringComparison.Ordinal))
            return fullKey.Substring(RootPrefix.Length);
        return fullKey;
    }

    /// <summary>Extract the top-level namespace from a full key — for <c>context/clients/acme/x.md</c>
    /// returns <c>clients/acme</c> (everything between the root prefix and the filename). When the
    /// entry has no sub-namespace (just <c>context/foo.md</c>) returns <c>"(root)"</c>.</summary>
    private static string ExtractNamespace(string fullKey)
    {
        var stripped = StripRoot(fullKey);
        var lastSlash = stripped.LastIndexOf('/');
        return lastSlash < 0 ? "(root)" : stripped.Substring(0, lastSlash);
    }

    /// <summary>Count non-overlapping case-insensitive occurrences of <paramref name="needle"/>
    /// in <paramref name="haystack"/>. Used by Search to rank hits — more matches = more
    /// relevant.</summary>
    private static int CountSubstring(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    /// <summary>Pull a small window of text around the first match — enough to let the agent
    /// (or a human) decide whether the hit is interesting. Keeps the search response compact.</summary>
    private static string ExtractSnippet(string body, string query)
    {
        var idx = body.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return body.Length <= 200 ? body : body.Substring(0, 200) + "…";
        var start = Math.Max(0, idx - 60);
        var end = Math.Min(body.Length, idx + query.Length + 60);
        var snippet = body.Substring(start, end - start).Replace('\n', ' ').Replace('\r', ' ');
        return (start > 0 ? "…" : "") + snippet.Trim() + (end < body.Length ? "…" : "");
    }
}
