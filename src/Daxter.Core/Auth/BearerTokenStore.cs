using System.Security.Cryptography;
using System.Text;

namespace Daxter.Core.Auth;

// ──────────────────────────────────────────────────────────────────────────────────────────────
// Shared bearer-token primitive (v1.40.0).
//
// Two surfaces need bearer-token auth with identical security properties but different config:
//   - The HTTP MCP transport (v1.38.0)  — env DAXTER_MCP_BEARER_TOKEN, file mcp-bearer-token
//   - The Web console /api/* endpoints  (v1.40.0) — env DAXTER_WEB_BEARER_TOKEN, file web-bearer-token
//
// The SECURITY-CRITICAL bits live here, once: token format (sk_ + 192 bits), constant-time
// compare (FixedTimeEquals — defends against char-by-char timing leaks), and log redaction
// (never emit a full token). The two consumers differ only in which env var / file they read.
//
// Pure — no ASP.NET dependency, so it lives in Daxter.Core and both the CLI middleware and the
// Web middleware reference it.
// ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Resolve / generate / redact / compare bearer tokens. The single home for the
/// security primitives both the MCP and Web bearer paths share.</summary>
public static class BearerTokenStore
{
    /// <summary>The directory bearer-token files live in (<c>~/.daxter</c>). Shared with the
    /// MSAL token cache + artifact store; persisted in the container's token volume.</summary>
    public static string DefaultDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".daxter");

    /// <summary>Generate a fresh <c>sk_</c>-prefixed hex token: 24 random bytes hex-encoded
    /// (48 hex chars, ~192 bits entropy). Matches Semantix's existing token format so a token
    /// minted by either side is interoperable.</summary>
    public static string Generate()
    {
        var bytes = new byte[24];
        RandomNumberGenerator.Fill(bytes);
        return "sk_" + Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Resolve from env &gt; persisted file &gt; generate-and-persist. The env var is the
    /// primary path (fleet orchestrators set it per-container); the file is the solo fallback that
    /// survives restarts via the token volume.</summary>
    public static string Resolve(string envVarName, string persistPath)
    {
        var env = Environment.GetEnvironmentVariable(envVarName);
        if (!string.IsNullOrWhiteSpace(env)) return env.Trim();

        if (File.Exists(persistPath))
        {
            var fromDisk = File.ReadAllText(persistPath).Trim();
            if (!string.IsNullOrEmpty(fromDisk)) return fromDisk;
        }

        var generated = Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(persistPath)!);
        File.WriteAllText(persistPath, generated);
        TryRestrictPermissions(persistPath);
        return generated;
    }

    /// <summary>Read whatever the env var holds, or null when unset. Used by the Web path to
    /// decide whether /api/* auth is ENABLED (token present) vs the localhost-trusted default
    /// (token absent) — it deliberately does NOT generate a token, because an absent Web token
    /// means "open on localhost", not "make one up".</summary>
    public static string? FromEnv(string envVarName)
    {
        var env = Environment.GetEnvironmentVariable(envVarName);
        return string.IsNullOrWhiteSpace(env) ? null : env.Trim();
    }

    /// <summary>Force-regenerate the persisted token at <paramref name="persistPath"/>.</summary>
    public static string Rotate(string persistPath)
    {
        var fresh = Generate();
        Directory.CreateDirectory(Path.GetDirectoryName(persistPath)!);
        File.WriteAllText(persistPath, fresh);
        TryRestrictPermissions(persistPath);
        return fresh;
    }

    /// <summary>Truncate a token to its prefix for safe logging — never emit the raw token.
    /// One accidental log line in a shared aggregator burns the secret.</summary>
    public static string Redact(string? token)
    {
        if (string.IsNullOrEmpty(token)) return "(none)";
        if (token.Length <= 8) return "***";
        return $"{token[..8]}***";
    }

    /// <summary>Constant-time equality on the UTF-8 bytes of two tokens. Defends against an
    /// attacker leaking the token char-by-char via response-time variance.
    /// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// returns false (rather than throwing) on a length mismatch, which is exactly what we want.</summary>
    public static bool FixedTimeEquals(string presented, string expected)
        => CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(presented), Encoding.UTF8.GetBytes(expected));

    /// <summary>Parse <c>Authorization: Bearer &lt;token&gt;</c> case-insensitively (RFC 6750).
    /// Returns false on a missing header, malformed value, or a different scheme.</summary>
    public static bool TryExtractBearer(string? header, out string token)
    {
        token = "";
        if (string.IsNullOrEmpty(header)) return false;
        const string scheme = "Bearer ";
        if (header.Length <= scheme.Length) return false;
        if (!header.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)) return false;
        token = header[scheme.Length..].Trim();
        return token.Length > 0;
    }

    /// <summary>Best-effort <c>chmod 600</c> on a token file (POSIX only; Windows ignores).
    /// Failure is non-fatal — the token still works, the host just isn't hardened.</summary>
    public static void TryRestrictPermissions(string path)
    {
        try
        {
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch { /* best-effort */ }
    }
}
