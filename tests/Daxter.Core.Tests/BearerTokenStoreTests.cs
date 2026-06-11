using Daxter.Core.Auth;

namespace Daxter.Core.Tests;

/// <summary>The v1.40.0 shared bearer-token primitive — the single home for the security-critical
/// constant-time compare, token format, header parse, and redaction that BOTH the MCP transport
/// and the Web /api/* gate rely on. If any of these regress, both auth surfaces are affected, so
/// they're pinned here once.</summary>
public sealed class BearerTokenStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public BearerTokenStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "daxter-bts-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "token");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ── Token format ─────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Generate_is_sk_prefixed_48_hex_chars()
    {
        var t = BearerTokenStore.Generate();
        Assert.Matches("^sk_[0-9a-f]{48}$", t);
    }

    [Fact]
    public void Generate_is_unique_per_call()
        => Assert.NotEqual(BearerTokenStore.Generate(), BearerTokenStore.Generate());

    // ── Constant-time compare (the security-critical bit) ────────────────────────────────────
    [Fact]
    public void FixedTimeEquals_true_for_equal_tokens()
        => Assert.True(BearerTokenStore.FixedTimeEquals("sk_abc", "sk_abc"));

    [Fact]
    public void FixedTimeEquals_false_for_different_tokens()
        => Assert.False(BearerTokenStore.FixedTimeEquals("sk_abc", "sk_xyz"));

    [Fact]
    public void FixedTimeEquals_false_for_different_lengths()
    {
        // Length mismatch must return false (not throw) — a presented token that's a prefix of
        // the real one must not be accepted.
        Assert.False(BearerTokenStore.FixedTimeEquals("sk_ab", "sk_abc"));
        Assert.False(BearerTokenStore.FixedTimeEquals("sk_abcd", "sk_abc"));
    }

    [Fact]
    public void FixedTimeEquals_empty_strings_equal_but_not_against_nonempty()
    {
        Assert.True(BearerTokenStore.FixedTimeEquals("", ""));
        Assert.False(BearerTokenStore.FixedTimeEquals("", "sk_abc"));
    }

    // ── Header parse (RFC 6750) ──────────────────────────────────────────────────────────────
    [Theory]
    [InlineData("Bearer sk_abc", true, "sk_abc")]
    [InlineData("bearer sk_abc", true, "sk_abc")]      // case-insensitive scheme
    [InlineData("BEARER sk_abc", true, "sk_abc")]
    [InlineData("Bearer   sk_abc  ", true, "sk_abc")]  // trimmed
    [InlineData("Basic dXNlcjpwdw==", false, "")]      // wrong scheme
    [InlineData("sk_abc", false, "")]                   // no scheme
    [InlineData("Bearer ", false, "")]                  // empty token
    [InlineData("", false, "")]                          // empty header
    [InlineData(null, false, "")]                        // missing header
    public void TryExtractBearer_parses_per_rfc6750(string? header, bool ok, string expected)
    {
        var got = BearerTokenStore.TryExtractBearer(header, out var token);
        Assert.Equal(ok, got);
        if (ok) Assert.Equal(expected, token);
    }

    // ── Redaction ────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void Redact_keeps_prefix_only()
    {
        Assert.Equal("sk_abc12***", BearerTokenStore.Redact("sk_abc1234567890"));
        Assert.Equal("***", BearerTokenStore.Redact("short"));
        Assert.Equal("(none)", BearerTokenStore.Redact(null));
        Assert.Equal("(none)", BearerTokenStore.Redact(""));
    }

    // ── Resolve precedence: env > file > generate ────────────────────────────────────────────
    [Fact]
    public void Resolve_prefers_env_over_file()
    {
        var envName = "DAXTER_TEST_BTS_" + Guid.NewGuid().ToString("N");
        var prev = Environment.GetEnvironmentVariable(envName);
        try
        {
            File.WriteAllText(_file, "sk_from_file");
            Environment.SetEnvironmentVariable(envName, "sk_from_env");
            Assert.Equal("sk_from_env", BearerTokenStore.Resolve(envName, _file));
        }
        finally { Environment.SetEnvironmentVariable(envName, prev); }
    }

    [Fact]
    public void Resolve_reads_file_when_env_unset_and_trims()
    {
        var envName = "DAXTER_TEST_BTS_" + Guid.NewGuid().ToString("N");
        File.WriteAllText(_file, "sk_from_file\n");
        Assert.Equal("sk_from_file", BearerTokenStore.Resolve(envName, _file));
    }

    [Fact]
    public void Resolve_generates_and_persists_when_neither_present()
    {
        var envName = "DAXTER_TEST_BTS_" + Guid.NewGuid().ToString("N");
        Assert.False(File.Exists(_file));
        var first = BearerTokenStore.Resolve(envName, _file);
        var second = BearerTokenStore.Resolve(envName, _file);
        Assert.Equal(first, second);                 // idempotent until Rotate
        Assert.Matches("^sk_[0-9a-f]{48}$", first);
        Assert.True(File.Exists(_file));
    }

    // ── FromEnv — the "is the Web gate enabled?" check (must NOT generate) ────────────────────
    [Fact]
    public void FromEnv_returns_null_when_unset_and_does_not_create_a_file()
    {
        var envName = "DAXTER_TEST_BTS_" + Guid.NewGuid().ToString("N");
        Assert.Null(BearerTokenStore.FromEnv(envName));
        // Crucially, FromEnv must NOT have generated/persisted anything — an absent Web token means
        // "gate disabled / localhost-trusted", not "mint a token".
        Assert.False(File.Exists(_file));
    }

    [Fact]
    public void FromEnv_returns_trimmed_value_when_set()
    {
        var envName = "DAXTER_TEST_BTS_" + Guid.NewGuid().ToString("N");
        var prev = Environment.GetEnvironmentVariable(envName);
        try
        {
            Environment.SetEnvironmentVariable(envName, "  sk_web  ");
            Assert.Equal("sk_web", BearerTokenStore.FromEnv(envName));
        }
        finally { Environment.SetEnvironmentVariable(envName, prev); }
    }

    [Fact]
    public void Rotate_replaces_persisted_token()
    {
        File.WriteAllText(_file, "sk_old");
        var fresh = BearerTokenStore.Rotate(_file);
        Assert.Matches("^sk_[0-9a-f]{48}$", fresh);
        Assert.Equal(fresh, File.ReadAllText(_file).Trim());
    }
}
