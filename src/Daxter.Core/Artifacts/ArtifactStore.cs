using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Daxter.Core.Artifacts;

// ──────────────────────────────────────────────────────────────────────────────────────────────
// DAXter ArtifactStore — the transport-agnostic file plane.
//
// PROBLEM. Several DAXter surfaces produce or consume file-shaped data:
//   - daxter_export_report  → PBIR parts + .pbix    (DAXter → agent)
//   - daxter_sql_export     → multi-GB CSV          (DAXter → agent)
//   - daxter_copy_job_definition / daxter_notebook_definition  → JSON bundles  (DAXter → agent)
//   - (future) daxter_update_report/notebook/copy_job_definition  → PBIR/IPYNB upload (agent → DAXter)
//
// Today most of these write directly to ~/.daxter/exports/ INSIDE the container's Docker volume.
// That works when DAXter runs on the user's laptop (local Docker, a bind-mount can paper over
// it), but it BREAKS the moment DAXter is hosted on another machine — the agent has no way to
// reach those bytes without a transport layer.
//
// SOLUTION. A single content-addressable artifact store, exposed through every surface
// (CLI / MCP / Web HTTP), that every file-shaped feature passes through. Same API regardless of
// where DAXter sits — local Docker, private VM, future hosted instance. The agent ferries bytes
// in and out via well-defined tools instead of filesystem assumptions.
//
// STORAGE SHAPE. `{root}/{key}` is the file. `{root}/.daxter-artifacts.json` is the metadata
// index (TTL + source-tool per key). The `.` prefix shields it from List/Bundle enumeration. We
// avoid per-file sidecars so a 1000-file PBIR bundle doesn't double its inode count.
//
// SAFETY. Key sanitization is enforced at the API boundary (no `..`, no rooted paths, no `\`,
// no control chars). Quota is checked before Put. Expired artifacts get purged by a background
// pass. Every public method validates the key — if a caller bypasses that, the static
// SafeJoin() helper still refuses to escape the root via a path-canonicalization check.
// ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>A single artifact in the store. Bytes and CreatedAt come from the filesystem;
/// ExpiresAt and SourceTool come from the metadata index if present.</summary>
public sealed record ArtifactRef(
    string Key,
    long Bytes,
    DateTime CreatedAt,
    DateTime? ExpiresAt,
    string? SourceTool);

/// <summary>Optional metadata attached on Put. Defaults are "no TTL, no source label".</summary>
public sealed record ArtifactMeta(
    DateTime? ExpiresAt = null,
    string? SourceTool = null);

/// <summary>Quota guardrail. Thrown by PutAsync when the store would exceed its byte budget.
/// Plain Exception subclass (DaxterException is sealed) — the surfaces that wrap MCP/CLI tool
/// calls catch it specifically so they can return a 413-style "store is full" hint instead of
/// the generic "Error: …" envelope.</summary>
public sealed class ArtifactQuotaExceededException : Exception
{
    public long CurrentBytes { get; }
    public long QuotaBytes { get; }
    public ArtifactQuotaExceededException(long current, long quota)
        : base($"Artifact store quota would be exceeded ({current:N0} + write > {quota:N0} bytes). " +
               $"Delete unused artifacts (daxter_artifact_delete) or raise DAXTER_ARTIFACTS_QUOTA_MB.")
    {
        CurrentBytes = current;
        QuotaBytes = quota;
    }
}

/// <summary>Thrown when a caller passes a key that escapes the store root or contains illegal
/// characters. Caught at the surface and returned as a 400-style message. Plain Exception
/// subclass — DaxterException is sealed.</summary>
public sealed class InvalidArtifactKeyException : Exception
{
    public InvalidArtifactKeyException(string reason)
        : base($"Invalid artifact key: {reason}. Keys must be relative forward-slash paths with no '..' segments, no absolute path, no control chars, and no reserved sidecar names.") {}
}

/// <summary>The transport-agnostic file plane. Implementations may back this with a local
/// filesystem (today), an object store (S3/Blob) in a future hosted scenario, or both. The
/// interface deliberately speaks in streams — never byte arrays — so a multi-GB CSV never has to
/// fit in memory.</summary>
public interface IArtifactStore
{
    /// <summary>Quota ceiling in bytes. PutAsync refuses when the store would exceed this.</summary>
    long QuotaBytes { get; }

    /// <summary>Stream into the store at <paramref name="key"/>, overwriting any prior content.
    /// Returns the metadata for the just-written artifact. Throws
    /// <see cref="ArtifactQuotaExceededException"/> if the write would exceed the quota.</summary>
    Task<ArtifactRef> PutAsync(string key, Stream content, ArtifactMeta? meta = null, CancellationToken ct = default);

    /// <summary>Open a single artifact for reading. The returned stream is the file — close it
    /// when done. Throws <see cref="FileNotFoundException"/> if the key isn't present.</summary>
    Task<Stream> OpenReadAsync(string key, CancellationToken ct = default);

    /// <summary>Zip every artifact under <paramref name="keyPrefix"/> into a stream the caller
    /// can pipe out to HTTP / disk. Used by the alignment workflow to grab a whole PBIR folder
    /// in one transfer.</summary>
    Task<Stream> OpenBundleAsync(string keyPrefix, CancellationToken ct = default);

    /// <summary>Unzip a zip stream into the store under <paramref name="keyPrefix"/>. Each entry's
    /// path is sanitised. Used by the future update_report_definition flow.</summary>
    Task<IReadOnlyList<ArtifactRef>> ExtractAsync(string keyPrefix, Stream zip, ArtifactMeta? meta = null, CancellationToken ct = default);

    /// <summary>List artifacts matching <paramref name="keyPrefix"/> (null = everything). Sorted
    /// by key for deterministic enumeration.</summary>
    Task<IReadOnlyList<ArtifactRef>> ListAsync(string? keyPrefix = null, CancellationToken ct = default);

    /// <summary>Get metadata for a single key. Returns null if the key isn't present.</summary>
    Task<ArtifactRef?> GetMetaAsync(string key, CancellationToken ct = default);

    /// <summary>Delete a single artifact OR an entire prefix (recursive). Returns the number of
    /// files removed.</summary>
    Task<int> DeleteAsync(string keyPrefix, CancellationToken ct = default);

    /// <summary>Remove every artifact whose ExpiresAt is in the past. Returns total bytes freed.
    /// Called from the nightly hosted-service tick (Phase 2) and on-demand from /artifacts.</summary>
    Task<long> PurgeExpiredAsync(CancellationToken ct = default);

    /// <summary>Sum of bytes currently in the store (excluding the metadata index itself).</summary>
    Task<long> CurrentUsageBytesAsync(CancellationToken ct = default);
}

/// <summary>Local-filesystem implementation. Backs onto <c>~/.daxter/artifacts/</c> by default;
/// override via the <c>DAXTER_ARTIFACTS_ROOT</c> env var. In a future hosted deployment the
/// same path can be a network-mounted volume; the API contract doesn't change.</summary>
public sealed class LocalArtifactStore : IArtifactStore
{
    /// <summary>Default per-deployment quota. Overridable via <c>DAXTER_ARTIFACTS_QUOTA_MB</c>.
    /// Generous default — artifacts are short-lived in the typical flow.</summary>
    public const long DefaultQuotaBytes = 5L * 1024 * 1024 * 1024;        // 5 GB

    /// <summary>The metadata sidecar filename. Lives at the root and is excluded from listings.
    /// The leading dot keeps it out of casual filesystem browsing too.</summary>
    public const string IndexFileName = ".daxter-artifacts.json";

    private readonly string _root;
    private readonly string _indexPath;
    private readonly SemaphoreSlim _indexLock = new(1, 1);
    public long QuotaBytes { get; }

    /// <summary>Resolve the root from the environment (Docker volume mount today, network mount
    /// tomorrow). When the env var is empty we default to <c>~/.daxter/artifacts</c>.</summary>
    public static string ResolveRoot()
    {
        var explicitRoot = Environment.GetEnvironmentVariable("DAXTER_ARTIFACTS_ROOT");
        if (!string.IsNullOrWhiteSpace(explicitRoot)) return explicitRoot;
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".daxter", "artifacts");
    }

    /// <summary>Resolve the quota from the environment. Returns the default if unset/invalid.
    /// We choose a generous 5GB default so first-time users don't bump into it on a single
    /// .pbix export; raise via env var for power users.</summary>
    public static long ResolveQuotaBytes()
    {
        var mb = Environment.GetEnvironmentVariable("DAXTER_ARTIFACTS_QUOTA_MB");
        if (long.TryParse(mb, out var m) && m > 0) return m * 1024 * 1024;
        return DefaultQuotaBytes;
    }

    public LocalArtifactStore() : this(ResolveRoot(), ResolveQuotaBytes()) {}

    public LocalArtifactStore(string root, long quotaBytes)
    {
        _root = root;
        _indexPath = Path.Combine(_root, IndexFileName);
        QuotaBytes = quotaBytes;
        Directory.CreateDirectory(_root);
    }

    // ── Key validation (the security boundary) ────────────────────────────────────────────────
    // Every public method runs the key through SafeJoin BEFORE touching the filesystem. The
    // checks are deliberately strict — any attempt to escape (`..`, absolute path, control char,
    // OS-reserved name) throws. We also reject the index filename so a caller can't overwrite
    // the metadata index by putting at its key.

    private string SafeJoin(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidArtifactKeyException("empty key");
        if (key.IndexOfAny(new[] { '\0', '\r', '\n' }) >= 0)
            throw new InvalidArtifactKeyException("control character in key");
        // Normalize the path-separator early — agents sometimes send Windows-style paths.
        var normalized = key.Replace('\\', '/');
        // Reject backslash-escaped colon drive letters and Windows drive prefixes.
        if (normalized.Contains(':')) throw new InvalidArtifactKeyException("colon not allowed");
        // No absolute paths (server-rooted or POSIX-rooted).
        if (normalized.StartsWith("/")) throw new InvalidArtifactKeyException("absolute path");
        // Reject `..` segments — they would walk out of the store root.
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0) throw new InvalidArtifactKeyException("empty key after normalize");
        foreach (var seg in segments)
        {
            if (seg == "." || seg == "..") throw new InvalidArtifactKeyException($"dot-segment '{seg}'");
            if (seg.Length > 255) throw new InvalidArtifactKeyException("segment > 255 chars");
        }
        // Refuse the metadata-index filename — a Put at that key would corrupt the index.
        if (string.Equals(segments[^1], IndexFileName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidArtifactKeyException($"reserved name '{IndexFileName}'");

        var joined = Path.Combine(new[] { _root }.Concat(segments).ToArray());

        // Double-belt: after canonicalisation, verify we're still under the root. Catches edge
        // cases the segment check missed (e.g., symlinks that point outside the root tree).
        var fullRoot = Path.GetFullPath(_root + Path.DirectorySeparatorChar);
        var fullKey  = Path.GetFullPath(joined);
        if (!fullKey.StartsWith(fullRoot, StringComparison.Ordinal))
            throw new InvalidArtifactKeyException("path escapes root");
        return joined;
    }

    /// <summary>Convert an absolute filesystem path back to a forward-slash artifact key,
    /// relative to the store root.</summary>
    private string ToKey(string fullPath)
    {
        var rel = Path.GetRelativePath(_root, fullPath);
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    // ── Metadata index ────────────────────────────────────────────────────────────────────────
    // Single JSON file at the root holds TTL + source-tool per key. Loaded lazily, rewritten
    // atomically (write to .tmp + rename). The semaphore serialises concurrent writers — a
    // race could corrupt the index, and the cost of one-at-a-time index writes is negligible
    // compared to the artifact-byte transfer they bracket.

    private sealed class IndexEntry
    {
        [JsonPropertyName("expires_at")] public DateTime? ExpiresAt { get; set; }
        [JsonPropertyName("source_tool")] public string? SourceTool { get; set; }
    }

    private async Task<Dictionary<string, IndexEntry>> LoadIndexAsync(CancellationToken ct)
    {
        if (!File.Exists(_indexPath)) return new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        try
        {
            await using var fs = File.OpenRead(_indexPath);
            return await JsonSerializer.DeserializeAsync<Dictionary<string, IndexEntry>>(fs, cancellationToken: ct)
                   ?? new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        }
        catch
        {
            // A corrupt index is recoverable — we lose TTL/source info, not data. Start fresh.
            return new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
        }
    }

    private async Task SaveIndexAsync(Dictionary<string, IndexEntry> index, CancellationToken ct)
    {
        var tmp = _indexPath + ".tmp";
        await using (var fs = File.Create(tmp))
        {
            await JsonSerializer.SerializeAsync(fs, index, new JsonSerializerOptions { WriteIndented = true }, ct);
        }
        // Atomic replace — File.Move with overwrite is rename(2) on POSIX.
        File.Move(tmp, _indexPath, overwrite: true);
    }

    private async Task UpdateIndexAsync(Action<Dictionary<string, IndexEntry>> mutator, CancellationToken ct)
    {
        await _indexLock.WaitAsync(ct);
        try
        {
            var index = await LoadIndexAsync(ct);
            mutator(index);
            await SaveIndexAsync(index, ct);
        }
        finally { _indexLock.Release(); }
    }

    // ── PutAsync ──────────────────────────────────────────────────────────────────────────────
    public async Task<ArtifactRef> PutAsync(string key, Stream content, ArtifactMeta? meta = null, CancellationToken ct = default)
    {
        var dest = SafeJoin(key);

        // Quota check BEFORE we open the file. We can't know the exact size of an unseekable
        // stream up front, so the check is "would the current usage + a conservative estimate
        // exceed the quota?" We re-check after the write to fail-fast on actual overrun. This
        // is a pragmatic guardrail rather than a hard limit — a malicious stream can still blow
        // through it, but the daemon then refuses the next Put.
        var current = await CurrentUsageBytesAsync(ct);
        if (current > QuotaBytes) throw new ArtifactQuotaExceededException(current, QuotaBytes);

        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        long written;
        await using (var fs = File.Create(dest))
        {
            await content.CopyToAsync(fs, ct);
            written = fs.Length;
        }

        // Post-write quota check — if THIS put pushed the store over, delete it and throw.
        var afterPut = await CurrentUsageBytesAsync(ct);
        if (afterPut > QuotaBytes)
        {
            try { File.Delete(dest); } catch { /* best-effort */ }
            throw new ArtifactQuotaExceededException(afterPut - written, QuotaBytes);
        }

        // Persist TTL/source-tool if supplied.
        if (meta is not null && (meta.ExpiresAt is not null || meta.SourceTool is not null))
        {
            await UpdateIndexAsync(idx =>
            {
                idx[ToKey(dest)] = new IndexEntry { ExpiresAt = meta.ExpiresAt, SourceTool = meta.SourceTool };
            }, ct);
        }

        return new ArtifactRef(
            Key: ToKey(dest),
            Bytes: written,
            CreatedAt: File.GetCreationTimeUtc(dest),
            ExpiresAt: meta?.ExpiresAt,
            SourceTool: meta?.SourceTool);
    }

    // ── Read paths ────────────────────────────────────────────────────────────────────────────
    public Task<Stream> OpenReadAsync(string key, CancellationToken ct = default)
    {
        var path = SafeJoin(key);
        if (!File.Exists(path)) throw new FileNotFoundException($"Artifact not found: {key}", key);
        // Open async-capable so the HTTP endpoint can stream without thread-pool starvation.
        Stream s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
        return Task.FromResult(s);
    }

    public async Task<Stream> OpenBundleAsync(string keyPrefix, CancellationToken ct = default)
    {
        // Re-use the listing logic so prefix semantics are identical to ListAsync.
        var members = await ListAsync(keyPrefix, ct);
        if (members.Count == 0) throw new FileNotFoundException($"No artifacts under prefix: {keyPrefix}", keyPrefix);

        // Stream the zip into a temp file and hand back a FileStream. Streaming straight to the
        // HTTP response is possible too, but a temp file makes the API consistent with single-
        // file Get and gives the caller a Length they can advertise via Content-Length.
        var tmp = Path.GetTempFileName();
        try
        {
            await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            using (var zip = new ZipArchive(fs, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var m in members)
                {
                    ct.ThrowIfCancellationRequested();
                    var path = SafeJoin(m.Key);
                    // Trim the prefix so the zip entries are relative to the user's chosen root.
                    var rel = m.Key;
                    if (!string.IsNullOrEmpty(keyPrefix))
                    {
                        var trimmed = keyPrefix.TrimEnd('/');
                        if (m.Key.StartsWith(trimmed + "/", StringComparison.Ordinal))
                            rel = m.Key.Substring(trimmed.Length + 1);
                        else if (m.Key == trimmed)
                            rel = Path.GetFileName(m.Key);  // single-file prefix
                    }
                    var entry = zip.CreateEntry(rel, CompressionLevel.Fastest);
                    await using var src = File.OpenRead(path);
                    await using var dst = entry.Open();
                    await src.CopyToAsync(dst, ct);
                }
            }
            // Return a read stream over the temp file; the FileOptions.DeleteOnClose flag means
            // the OS removes it as soon as the caller disposes the stream — no leak, no manual
            // cleanup path the caller has to remember.
            return new FileStream(tmp, FileMode.Open, FileAccess.Read, FileShare.Read, 81920,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        }
        catch
        {
            try { File.Delete(tmp); } catch { /* best-effort */ }
            throw;
        }
    }

    public async Task<IReadOnlyList<ArtifactRef>> ExtractAsync(string keyPrefix, Stream zip, ArtifactMeta? meta = null, CancellationToken ct = default)
    {
        var prefix = keyPrefix.TrimEnd('/');
        // Empty prefix is allowed — unzips at the root. But validate that any entry path resulting
        // from the (prefix + entry.FullName) join is still safe — SafeJoin handles it.
        using var archive = new ZipArchive(zip, ZipArchiveMode.Read, leaveOpen: false);
        var written = new List<ArtifactRef>(archive.Entries.Count);
        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Name)) continue;        // a directory entry — skip
            var combined = string.IsNullOrEmpty(prefix) ? entry.FullName : $"{prefix}/{entry.FullName}";
            await using var src = entry.Open();
            written.Add(await PutAsync(combined, src, meta, ct));
        }
        return written;
    }

    public async Task<IReadOnlyList<ArtifactRef>> ListAsync(string? keyPrefix = null, CancellationToken ct = default)
    {
        if (!Directory.Exists(_root)) return Array.Empty<ArtifactRef>();
        var index = await LoadIndexAsync(ct);
        var results = new List<ArtifactRef>();

        // EnumerateFiles is iterative, doesn't materialise the whole tree, and is safe to cancel.
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            // Skip the metadata index AND any tmp files left mid-write.
            if (name == IndexFileName || name.EndsWith(".tmp", StringComparison.Ordinal)) continue;

            var key = ToKey(path);
            // Apply the prefix filter — match either the exact key (single file) or anything
            // under prefix + '/' (folder). This is the same semantic as a prefix scan in S3.
            if (!string.IsNullOrEmpty(keyPrefix))
            {
                var trimmed = keyPrefix.TrimEnd('/');
                if (key != trimmed && !key.StartsWith(trimmed + "/", StringComparison.Ordinal)) continue;
            }
            var fi = new FileInfo(path);
            index.TryGetValue(key, out var meta);
            results.Add(new ArtifactRef(key, fi.Length, fi.CreationTimeUtc, meta?.ExpiresAt, meta?.SourceTool));
        }
        // Deterministic order — the agent + the Web grid expect a stable sort.
        results.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
        return results;
    }

    public async Task<ArtifactRef?> GetMetaAsync(string key, CancellationToken ct = default)
    {
        var path = SafeJoin(key);
        if (!File.Exists(path)) return null;
        var index = await LoadIndexAsync(ct);
        var fi = new FileInfo(path);
        index.TryGetValue(key, out var meta);
        return new ArtifactRef(ToKey(path), fi.Length, fi.CreationTimeUtc, meta?.ExpiresAt, meta?.SourceTool);
    }

    public async Task<int> DeleteAsync(string keyPrefix, CancellationToken ct = default)
    {
        var members = await ListAsync(keyPrefix, ct);
        if (members.Count == 0) return 0;
        var index = await LoadIndexAsync(ct);
        var removed = 0;
        foreach (var m in members)
        {
            ct.ThrowIfCancellationRequested();
            var path = SafeJoin(m.Key);
            try { File.Delete(path); removed++; index.Remove(m.Key); } catch { /* skip — best-effort */ }
        }
        // Clean up empty directories left behind so List doesn't churn over dead trees.
        TryRemoveEmptyDirs(_root);
        await UpdateIndexAsync(idx =>
        {
            foreach (var (k, _) in index) idx[k] = index[k];
            foreach (var m in members) idx.Remove(m.Key);
        }, ct);
        return removed;
    }

    public async Task<long> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var index = await LoadIndexAsync(ct);
        var now = DateTime.UtcNow;
        long bytesFreed = 0;
        var toRemove = index.Where(kv => kv.Value.ExpiresAt is { } e && e <= now).Select(kv => kv.Key).ToList();
        foreach (var key in toRemove)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var path = SafeJoin(key);
                if (File.Exists(path)) { bytesFreed += new FileInfo(path).Length; File.Delete(path); }
            }
            catch { /* skip */ }
        }
        if (toRemove.Count > 0)
        {
            await UpdateIndexAsync(idx => { foreach (var k in toRemove) idx.Remove(k); }, ct);
            TryRemoveEmptyDirs(_root);
        }
        return bytesFreed;
    }

    public Task<long> CurrentUsageBytesAsync(CancellationToken ct = default)
    {
        if (!Directory.Exists(_root)) return Task.FromResult(0L);
        long total = 0;
        foreach (var path in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(path);
            if (name == IndexFileName) continue;
            try { total += new FileInfo(path).Length; } catch { /* skip racing-delete */ }
        }
        return Task.FromResult(total);
    }

    // ── Housekeeping ──────────────────────────────────────────────────────────────────────────
    /// <summary>After Delete/PurgeExpired, sweep any directory that's now empty so listings don't
    /// have to walk dead trees. The root itself is preserved.</summary>
    private void TryRemoveEmptyDirs(string startDir)
    {
        if (!Directory.Exists(startDir)) return;
        foreach (var sub in Directory.EnumerateDirectories(startDir, "*", SearchOption.AllDirectories)
                                     .OrderByDescending(d => d.Length))  // deepest first
        {
            try { if (!Directory.EnumerateFileSystemEntries(sub).Any()) Directory.Delete(sub); }
            catch { /* skip */ }
        }
    }
}
