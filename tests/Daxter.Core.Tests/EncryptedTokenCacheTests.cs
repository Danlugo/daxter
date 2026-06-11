using System.Reflection;
using System.Text;
using Daxter.Core.Auth;

namespace Daxter.Core.Tests;

/// <summary>The v1.41.0 encrypted-at-rest token cache. The MSAL Before/After-access wiring can't
/// be unit-tested without a live MSAL app, so we test the crypto round-trip directly via the
/// private read/write helpers (reflection) — that's where the security property lives: the
/// persisted blob must be unreadable without the key, tamper must fail closed, and a wrong key
/// must degrade to "empty cache" (re-auth), never throw into the sign-in path.</summary>
public sealed class EncryptedTokenCacheTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public EncryptedTokenCacheTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "daxter-enc-cache-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "msal_cache.enc");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // The two private helpers carry the crypto; reach them by reflection so we don't have to
    // stand up a real MSAL ITokenCache.
    private static void WriteEncrypt(EncryptedTokenCache c, byte[] plaintext)
        => typeof(EncryptedTokenCache)
            .GetMethod("WriteEncrypt", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(c, new object[] { plaintext });

    private static byte[]? TryReadDecrypt(EncryptedTokenCache c)
        => (byte[]?)typeof(EncryptedTokenCache)
            .GetMethod("TryReadDecrypt", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(c, null);

    [Fact]
    public void Round_trips_plaintext_through_encrypt_decrypt()
    {
        var c = EncryptedTokenCache.WithKey(_file, "the-secret-key");
        var payload = Encoding.UTF8.GetBytes("{\"AccessToken\":{\"...\":\"a fake MSAL v3 blob\"}}");

        WriteEncrypt(c, payload);
        var got = TryReadDecrypt(c);

        Assert.NotNull(got);
        Assert.Equal(payload, got);
    }

    [Fact]
    public void Persisted_file_is_not_plaintext()
    {
        // The whole point: the bytes on disk must NOT contain the plaintext. A grep of the
        // volume should never reveal a token.
        var c = EncryptedTokenCache.WithKey(_file, "k");
        var secret = "REFRESH_TOKEN_SECRET_MARKER";
        WriteEncrypt(c, Encoding.UTF8.GetBytes(secret));

        var onDisk = File.ReadAllBytes(_file);
        Assert.DoesNotContain(secret, Encoding.UTF8.GetString(onDisk));
        // Format: version byte (1) + 12-byte nonce + ciphertext + 16-byte tag.
        Assert.Equal((byte)1, onDisk[0]);
        Assert.True(onDisk.Length >= 1 + 12 + 16);
    }

    [Fact]
    public void Wrong_key_decrypts_to_null_not_throw()
    {
        // A rotated/wrong key must degrade to "no cache" (MSAL re-auths), NOT throw into the
        // auth path. This is the fail-closed-but-graceful contract.
        var writer = EncryptedTokenCache.WithKey(_file, "original-key");
        WriteEncrypt(writer, Encoding.UTF8.GetBytes("secret blob"));

        var wrongReader = EncryptedTokenCache.WithKey(_file, "different-key");
        Assert.Null(TryReadDecrypt(wrongReader));   // null, no exception
    }

    [Fact]
    public void Tampered_ciphertext_decrypts_to_null()
    {
        // AES-GCM authenticates: flip one byte and decryption must fail (tag mismatch) → null.
        var c = EncryptedTokenCache.WithKey(_file, "k");
        WriteEncrypt(c, Encoding.UTF8.GetBytes("secret blob that is long enough"));

        var blob = File.ReadAllBytes(_file);
        blob[^1] ^= 0xFF;                 // corrupt the GCM tag
        File.WriteAllBytes(_file, blob);

        Assert.Null(TryReadDecrypt(c));
    }

    [Fact]
    public void Missing_file_decrypts_to_null()
    {
        var c = EncryptedTokenCache.WithKey(_file, "k");
        Assert.False(File.Exists(_file));
        Assert.Null(TryReadDecrypt(c));
    }

    [Fact]
    public void Truncated_file_decrypts_to_null()
    {
        // A torn write / partial read (file shorter than version+nonce+tag) must be tolerated.
        File.WriteAllBytes(_file, new byte[] { 1, 2, 3 });
        var c = EncryptedTokenCache.WithKey(_file, "k");
        Assert.Null(TryReadDecrypt(c));
    }

    [Fact]
    public void Empty_plaintext_round_trips()
    {
        // MSAL can serialize an empty cache; encrypting 0 bytes must round-trip cleanly.
        var c = EncryptedTokenCache.WithKey(_file, "k");
        WriteEncrypt(c, Array.Empty<byte>());
        var got = TryReadDecrypt(c);
        Assert.NotNull(got);
        Assert.Empty(got!);
    }

    [Fact]
    public void Each_write_uses_a_fresh_nonce()
    {
        // Same plaintext + same key must produce DIFFERENT ciphertext each write (random nonce) —
        // otherwise an observer could detect "the token didn't change" or replay.
        var c = EncryptedTokenCache.WithKey(_file, "k");
        var payload = Encoding.UTF8.GetBytes("same content");

        WriteEncrypt(c, payload);
        var first = File.ReadAllBytes(_file);
        WriteEncrypt(c, payload);
        var second = File.ReadAllBytes(_file);

        Assert.NotEqual(first, second);           // different nonce → different bytes
        Assert.Equal(payload, TryReadDecrypt(c)); // still decrypts correctly
    }

    // ── FromEnv gating ───────────────────────────────────────────────────────────────────────
    [Fact]
    public void FromEnv_returns_null_when_key_unset()
    {
        var prev = Environment.GetEnvironmentVariable(EncryptedTokenCache.KeyEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EncryptedTokenCache.KeyEnvVar, null);
            Assert.Null(EncryptedTokenCache.FromEnv(_file));
            Assert.False(EncryptedTokenCache.IsConfigured);
        }
        finally { Environment.SetEnvironmentVariable(EncryptedTokenCache.KeyEnvVar, prev); }
    }

    [Fact]
    public void FromEnv_builds_a_working_cache_when_key_set()
    {
        var prev = Environment.GetEnvironmentVariable(EncryptedTokenCache.KeyEnvVar);
        try
        {
            Environment.SetEnvironmentVariable(EncryptedTokenCache.KeyEnvVar, "env-supplied-key");
            Assert.True(EncryptedTokenCache.IsConfigured);
            var c = EncryptedTokenCache.FromEnv(_file);
            Assert.NotNull(c);
            WriteEncrypt(c!, Encoding.UTF8.GetBytes("works"));
            Assert.Equal("works", Encoding.UTF8.GetString(TryReadDecrypt(c!)!));
        }
        finally { Environment.SetEnvironmentVariable(EncryptedTokenCache.KeyEnvVar, prev); }
    }
}
