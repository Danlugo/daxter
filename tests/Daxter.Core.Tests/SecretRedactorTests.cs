using Daxter.Core.Diagnostics;

namespace Daxter.Core.Tests;

public class SecretRedactorTests
{
    [Theory]
    // Secrets that MUST be masked.
    [InlineData("password=hunter2;Data Source=x", "password")]
    [InlineData("Pwd: s3cr3t!", "Pwd")]
    [InlineData("client_secret=abc123def", "client_secret")]
    [InlineData("ClientSecret=abc123def", "ClientSecret")]
    [InlineData("access_token=zzz999", "access_token")]
    [InlineData("api_key=ABCDEF", "api_key")]
    public void Redact_masks_keyed_secrets(string input, string keyKept)
    {
        var result = SecretRedactor.Redact(input);
        Assert.Contains(keyKept, result);            // key is kept for context
        Assert.Contains("***redacted***", result);   // value is masked
        Assert.DoesNotContain("hunter2", result);
        Assert.DoesNotContain("s3cr3t", result);
        Assert.DoesNotContain("abc123def", result);
        Assert.DoesNotContain("zzz999", result);
        Assert.DoesNotContain("ABCDEF", result);
    }

    [Fact]
    public void Redact_masks_jwt_access_token()
    {
        const string jwt = "eyJhbGciOiJSUzI1NiIsInR5cCI6IkpXVCJ9.eyJhdWQiOiJodHRwczovL2FuYWx5c2lzIn0.SflKxwRJSMeKKF2QT4fwpMeJf36";
        var result = SecretRedactor.Redact($"Authentication failed: token {jwt} rejected");
        Assert.DoesNotContain(jwt, result);
        Assert.Contains("***redacted***", result);
    }

    [Fact]
    public void Redact_masks_bearer_header()
    {
        var result = SecretRedactor.Redact("Authorization: Bearer abc.def.ghi-XYZ_123");
        Assert.DoesNotContain("abc.def.ghi-XYZ_123", result);
        Assert.Contains("Bearer ***redacted***", result);
    }

    [Theory]
    // Normal, non-sensitive log content that must pass through unchanged.
    [InlineData("datasets [Prod] -> 94 rows in 887 ms")]
    [InlineData("Data Source=powerbi://api.powerbi.com/v1.0/myorg/Sales;Initial Catalog=Retail Model;")]
    [InlineData("Health check finished: 4/4 checks ok")]
    [InlineData("workspace 'Secret Sauce Analytics' selected")]
    public void Redact_leaves_non_secret_text_intact(string input)
        => Assert.Equal(input, SecretRedactor.Redact(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Redact_handles_empty(string? input)
        => Assert.Equal(string.Empty, SecretRedactor.Redact(input));
}
