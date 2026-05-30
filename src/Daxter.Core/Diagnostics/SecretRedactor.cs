using System.Text.RegularExpressions;

namespace Daxter.Core.Diagnostics;

/// <summary>
/// Scrubs credentials and tokens from text before it is logged or displayed. A defense-in-depth
/// safety net: DAXter injects OAuth tokens out-of-band (never in connection strings) and does not
/// log secrets, but a third-party exception message could still echo one. Over-redacts on purpose
/// — losing a value in a log line is always preferable to leaking a credential.
/// </summary>
public static partial class SecretRedactor
{
    private const string Mask = "***redacted***";

    // JWTs / OAuth access tokens (Power BI tokens start "eyJ"): header.payload.signature.
    [GeneratedRegex(@"eyJ[A-Za-z0-9_=-]{8,}\.[A-Za-z0-9_=-]{8,}\.[A-Za-z0-9_=-]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex JwtPattern();

    // key=value / key: value where the key names a secret. Value runs to ; , & whitespace or quote.
    [GeneratedRegex(
        @"(?i)\b(password|pwd|client[_-]?secret|secret|access[_-]?token|accesstoken|client[_-]?assertion|assertion|refresh[_-]?token|id[_-]?token|api[_-]?key|apikey)\b\s*[=:]\s*([^;,&\s""']+)",
        RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueSecretPattern();

    // Authorization: Bearer <token>
    [GeneratedRegex(@"(?i)\bbearer\s+[A-Za-z0-9._\-]+", RegexOptions.CultureInvariant)]
    private static partial Regex BearerPattern();

    /// <summary>Returns <paramref name="text"/> with any detected secrets replaced by a mask.</summary>
    public static string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? string.Empty;

        var result = JwtPattern().Replace(text, Mask);
        result = KeyValueSecretPattern().Replace(result, m => $"{m.Groups[1].Value}={Mask}");
        result = BearerPattern().Replace(result, $"Bearer {Mask}");
        return result;
    }
}
