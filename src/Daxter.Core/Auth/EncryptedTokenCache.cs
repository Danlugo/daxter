using System.Security.Cryptography;
using System.Text;
using Microsoft.Identity.Client;

namespace Daxter.Core.Auth;

// ──────────────────────────────────────────────────────────────────────────────────────────────
// Encrypted MSAL token cache at rest (v1.41.0).
//
// WHY. On the Linux container image the MSAL cache helper falls back to an UNPROTECTED file
// (no keyring), so the AAD refresh tokens sit in plaintext on the ~/.daxter volume. Anyone
// with read access to the volume (a docker exec, a host-mounted volume, a snapshot) can read
// them and act as the signed-in identity. This wraps the cache in AES-256-GCM so the persisted
// blob is unreadable without the key.
//
// THE KEY MUST LIVE OUTSIDE THE VOLUME. Encryption only helps if the key isn't sitting next to
// the ciphertext. The key comes from DAXTER_CACHE_KEY (env) — Semantix/hosted set it per
// container from their secrets store, so the volume alone is no longer enough to impersonate.
// On a personal laptop where the key would live in the same place, the benefit is smaller —
// hence this is OPT-IN: when DAXTER_CACHE_KEY is unset, MsalTokenProvider keeps the platform
// default (macOS keychain / Windows DPAPI / Linux unprotected-file + a warning).
//
// FORMAT (versioned, so we can rotate the scheme later):
//   [1 byte version=1][12 byte nonce][N byte ciphertext][16 byte GCM tag]
// Key = SHA-256(DAXTER_CACHE_KEY) → 32 bytes (AES-256). A fresh random nonce per write.
//
// FAILURE = EMPTY CACHE. A decrypt failure (wrong/rotated key, torn read, corruption) is
// treated as "no cache" — MSAL then re-authenticates. Never throws into the auth path; a
// broken cache must not break sign-in.
//
// CONCURRENCY. Writes are atomic (temp + rename) so a reader never sees a half-written file.
// Two concurrent writers are last-writer-wins (one process may re-auth next time) — acceptable
// for an infrequently-written token cache; it's never corruption.
// ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>AES-256-GCM encryption layer for the MSAL token cache. Wires the
/// <see cref="ITokenCache"/> Before/After-access callbacks to read/write an encrypted file.
/// Pure crypto + file I/O — no platform keyring dependency, so it works identically in the
/// slim Linux container.</summary>
public sealed class EncryptedTokenCache
{
    /// <summary>Env var holding the encryption key (any non-empty string; we hash it to 32 bytes).
    /// When unset, encryption is off and the caller uses the platform default cache.</summary>
    public const string KeyEnvVar = "DAXTER_CACHE_KEY";

    private const byte FormatVersion = 1;
    private const int NonceBytes = 12;   // AES-GCM standard nonce size
    private const int TagBytes = 16;     // AES-GCM standard tag size

    private readonly string _path;
    private readonly byte[] _key;        // 32 bytes (AES-256)
    private readonly object _gate = new();

    private EncryptedTokenCache(string path, byte[] key)
    {
        _path = path;
        _key = key;
    }

    /// <summary>True when <see cref="KeyEnvVar"/> is set — i.e. the caller should use encryption.</summary>
    public static bool IsConfigured => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(KeyEnvVar));

    /// <summary>Create an encrypted cache for <paramref name="filePath"/> using the key from
    /// <see cref="KeyEnvVar"/>. Returns null when no key is configured (caller falls back to the
    /// platform default). The key string is hashed to a 32-byte AES key — any length input works.</summary>
    public static EncryptedTokenCache? FromEnv(string filePath)
    {
        var raw = Environment.GetEnvironmentVariable(KeyEnvVar);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var key = SHA256.HashData(Encoding.UTF8.GetBytes(raw.Trim()));
        return new EncryptedTokenCache(filePath, key);
    }

    /// <summary>Test/host hook — build with an explicit key string instead of the env var.</summary>
    public static EncryptedTokenCache WithKey(string filePath, string key)
        => new(filePath, SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    /// <summary>Attach to an MSAL app's user token cache. Before-access decrypts the file into the
    /// cache; after-access (when changed) encrypts and persists it.</summary>
    public void Attach(ITokenCache cache)
    {
        cache.SetBeforeAccess(OnBeforeAccess);
        cache.SetAfterAccess(OnAfterAccess);
    }

    private void OnBeforeAccess(TokenCacheNotificationArgs args)
    {
        lock (_gate)
        {
            var plaintext = TryReadDecrypt();
            if (plaintext is not null) args.TokenCache.DeserializeMsalV3(plaintext);
        }
    }

    private void OnAfterAccess(TokenCacheNotificationArgs args)
    {
        if (!args.HasStateChanged) return;
        lock (_gate)
        {
            var plaintext = args.TokenCache.SerializeMsalV3();
            WriteEncrypt(plaintext);
        }
    }

    /// <summary>Read + decrypt the cache file. Returns null on absent file OR any failure
    /// (wrong key, torn read, corruption) — failure means "no cache", so MSAL re-auths.</summary>
    private byte[]? TryReadDecrypt()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            var blob = File.ReadAllBytes(_path);
            // Minimum: version + nonce + tag (empty ciphertext is technically valid).
            if (blob.Length < 1 + NonceBytes + TagBytes) return null;
            if (blob[0] != FormatVersion) return null;

            var nonce = new byte[NonceBytes];
            Array.Copy(blob, 1, nonce, 0, NonceBytes);
            var cipherLen = blob.Length - 1 - NonceBytes - TagBytes;
            var cipher = new byte[cipherLen];
            Array.Copy(blob, 1 + NonceBytes, cipher, 0, cipherLen);
            var tag = new byte[TagBytes];
            Array.Copy(blob, 1 + NonceBytes + cipherLen, tag, 0, TagBytes);

            var plain = new byte[cipherLen];
            using var gcm = new AesGcm(_key, TagBytes);
            gcm.Decrypt(nonce, cipher, tag, plain);   // throws on tag mismatch (wrong key / tamper)
            return plain;
        }
        catch
        {
            // Wrong key, tampered file, partial read — treat as empty so sign-in still works.
            return null;
        }
    }

    /// <summary>Encrypt + atomically write the cache file (temp + rename), chmod 600.</summary>
    private void WriteEncrypt(byte[] plaintext)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            var nonce = new byte[NonceBytes];
            RandomNumberGenerator.Fill(nonce);
            var cipher = new byte[plaintext.Length];
            var tag = new byte[TagBytes];
            using (var gcm = new AesGcm(_key, TagBytes))
                gcm.Encrypt(nonce, plaintext, cipher, tag);

            var blob = new byte[1 + NonceBytes + cipher.Length + TagBytes];
            blob[0] = FormatVersion;
            Array.Copy(nonce, 0, blob, 1, NonceBytes);
            Array.Copy(cipher, 0, blob, 1 + NonceBytes, cipher.Length);
            Array.Copy(tag, 0, blob, 1 + NonceBytes + cipher.Length, TagBytes);

            var tmp = _path + ".tmp";
            File.WriteAllBytes(tmp, blob);
            BearerTokenStore.TryRestrictPermissions(tmp);   // 600 before it becomes the live file
            File.Move(tmp, _path, overwrite: true);          // atomic rename
            BearerTokenStore.TryRestrictPermissions(_path);
        }
        catch
        {
            // A failed persist just means this process re-auths next run — never throw into
            // the auth path.
        }
    }
}
