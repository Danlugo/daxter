using System.Net;
using System.Text.Json;
using Daxter.Core.Diagnostics;

namespace Daxter.Core.Tests;

/// <summary>The Semantix consumer contract for structured CLI errors (wishlist item #3). Pins
/// the AADSTS → error_code mappings so an AAD error message reword by Microsoft can't silently
/// reclassify a known failure, and so renaming an error_code surfaces as a test break (which is
/// what we want — it's a contract change).</summary>
public sealed class CliErrorClassifierTests
{
    // ── AADSTS mapping ────────────────────────────────────────────────────────────────────────
    // Each AAD code maps to a stable error_code Semantix can match on. The human message is
    // operator-facing; the test only pins the CODE because the message wording may improve.

    [Theory]
    [InlineData("AADSTS7000215: Invalid client secret provided.", CliErrorCodes.BAD_CLIENT_SECRET, "AADSTS7000215")]
    [InlineData("AADSTS700016: Application 'xyz' was not found in the directory.", CliErrorCodes.BAD_CLIENT_ID, "AADSTS700016")]
    [InlineData("AADSTS90002: Tenant 'abc' not found.", CliErrorCodes.BAD_TENANT_ID, "AADSTS90002")]
    [InlineData("AADSTS900023: Specified tenant identifier 'xyz' is not valid.", CliErrorCodes.BAD_TENANT_ID, "AADSTS900023")]
    [InlineData("AADSTS50020: User account from identity provider does not exist in tenant.", CliErrorCodes.BAD_TENANT_ID, "AADSTS50020")]
    [InlineData("AADSTS65001: The user or administrator has not consented to use the application.", CliErrorCodes.INSUFFICIENT_PERMISSIONS, "AADSTS65001")]
    [InlineData("AADSTS50105: The signed in user is not assigned to a role.", CliErrorCodes.INSUFFICIENT_PERMISSIONS, "AADSTS50105")]
    public void Maps_known_AADSTS_codes_to_stable_error_codes(string raw, string expectedCode, string expectedAadCode)
    {
        var err = CliErrorClassifier.Classify(new Exception(raw));
        Assert.Equal(expectedCode, err.ErrorCode);
        Assert.Equal(expectedAadCode, err.AadCode);
        Assert.NotNull(err.Message);          // human-readable hint is always present
        Assert.NotNull(err.Details);          // original message preserved
    }

    [Fact]
    public void Unknown_AADSTS_code_falls_through_to_UNKNOWN_but_keeps_AadCode()
    {
        // A code we haven't mapped (yet) should land in UNKNOWN with the AADSTS field populated —
        // Semantix can still surface the raw AAD code to the operator while we triage adding a
        // dedicated mapping in DAXter. This is the graceful-degradation contract.
        var raw = "AADSTS999999: Some new failure mode Microsoft just shipped.";
        var err = CliErrorClassifier.Classify(new Exception(raw));
        Assert.Equal(CliErrorCodes.UNKNOWN, err.ErrorCode);
        Assert.Equal("AADSTS999999", err.AadCode);
    }

    [Fact]
    public void Extracts_TraceID_when_present()
    {
        var raw = "AADSTS7000215: Invalid client secret. Trace ID: abc123de-1234-5678-9abc-def012345678 Correlation ID: ...";
        var err = CliErrorClassifier.Classify(new Exception(raw));
        Assert.Equal("abc123de-1234-5678-9abc-def012345678", err.TraceId);
    }

    // ── DaxterException patterns ──────────────────────────────────────────────────────────────
    // The CLI raises DaxterException with operator-facing messages. The classifier pattern-
    // matches on stable phrases ("not signed in", "workspace … not found") to assign a code.

    [Fact]
    public void DaxterException_not_signed_in_maps_to_NOT_SIGNED_IN()
    {
        var err = CliErrorClassifier.Classify(new DaxterException("Not signed in — use daxter login first."));
        Assert.Equal(CliErrorCodes.NOT_SIGNED_IN, err.ErrorCode);
    }

    [Fact]
    public void DaxterException_workspace_not_found_maps_to_WORKSPACE_NOT_FOUND()
    {
        var err = CliErrorClassifier.Classify(new DaxterException("Workspace 'DataHub - Prod' was not found."));
        Assert.Equal(CliErrorCodes.WORKSPACE_NOT_FOUND, err.ErrorCode);
    }

    [Fact]
    public void DaxterException_could_not_resolve_workspace_also_maps()
    {
        // Different DAXter call-sites phrase it differently — both surface the same intent.
        var err = CliErrorClassifier.Classify(new DaxterException("Could not resolve workspace 'foo'."));
        Assert.Equal(CliErrorCodes.WORKSPACE_NOT_FOUND, err.ErrorCode);
    }

    [Fact]
    public void DaxterException_dataset_not_found_maps_to_ITEM_NOT_FOUND()
    {
        var err = CliErrorClassifier.Classify(new DaxterException("Dataset 'Retail Model' not found in 'DataHub'."));
        Assert.Equal(CliErrorCodes.ITEM_NOT_FOUND, err.ErrorCode);
    }

    // ── HTTP / network ────────────────────────────────────────────────────────────────────────

    [Fact]
    public void HttpRequestException_403_maps_to_FORBIDDEN()
    {
        var ex = new HttpRequestException("Forbidden", null, HttpStatusCode.Forbidden);
        var err = CliErrorClassifier.Classify(ex);
        Assert.Equal(CliErrorCodes.FORBIDDEN, err.ErrorCode);
    }

    [Fact]
    public void HttpRequestException_404_maps_to_ITEM_NOT_FOUND()
    {
        var ex = new HttpRequestException("Not Found", null, HttpStatusCode.NotFound);
        var err = CliErrorClassifier.Classify(ex);
        Assert.Equal(CliErrorCodes.ITEM_NOT_FOUND, err.ErrorCode);
    }

    [Fact]
    public void HttpRequestException_other_status_maps_to_NETWORK_FAILURE()
    {
        var ex = new HttpRequestException("Bad Gateway", null, HttpStatusCode.BadGateway);
        var err = CliErrorClassifier.Classify(ex);
        Assert.Equal(CliErrorCodes.NETWORK_FAILURE, err.ErrorCode);
    }

    [Fact]
    public void TimeoutException_maps_to_NETWORK_FAILURE()
    {
        var err = CliErrorClassifier.Classify(new TimeoutException("Operation timed out."));
        Assert.Equal(CliErrorCodes.NETWORK_FAILURE, err.ErrorCode);
    }

    [Fact]
    public void Plain_exception_falls_through_to_UNKNOWN_with_original_message()
    {
        // The catch-all: anything we can't classify still produces a structured envelope so
        // Semantix can surface SOMETHING — `error_code: UNKNOWN`, `details` carries the raw text.
        var raw = "Something completely unexpected happened.";
        var err = CliErrorClassifier.Classify(new InvalidOperationException(raw));
        Assert.Equal(CliErrorCodes.UNKNOWN, err.ErrorCode);
        Assert.Equal(raw, err.Message);
        Assert.Equal(raw, err.Details);
    }

    [Fact]
    public void Details_truncates_very_long_messages_to_keep_envelope_compact()
    {
        // A 5KB AAD trace dump shouldn't tail-bomb the JSON envelope — Semantix only needs
        // enough context to diagnose. The classifier truncates at 600 chars with an ellipsis.
        var huge = new string('x', 10_000);
        var err = CliErrorClassifier.Classify(new Exception(huge));
        Assert.NotNull(err.Details);
        Assert.True(err.Details!.Length <= 601, $"expected ≤601 chars, got {err.Details.Length}");
        Assert.EndsWith("…", err.Details);
    }

    // ── Envelope serialisation — the actual stderr wire-shape ─────────────────────────────────

    [Fact]
    public void ToJsonEnvelope_wraps_error_under_error_key()
    {
        var err = new CliError(CliErrorCodes.BAD_CLIENT_SECRET, "msg", AadCode: "AADSTS7000215");
        var json = CliErrorClassifier.ToJsonEnvelope(err);
        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("error", out var inner));
        Assert.Equal("BAD_CLIENT_SECRET", inner.GetProperty("error_code").GetString());
        Assert.Equal("AADSTS7000215", inner.GetProperty("aad_code").GetString());
    }

    [Fact]
    public void ToJsonEnvelope_omits_null_optional_fields()
    {
        // A failure without an AAD code or trace id should not produce
        // `"aad_code": null` / `"trace_id": null` keys — Semantix's JSON shape contract is
        // "field present iff value non-null", same as /api/health.
        var err = new CliError(CliErrorCodes.UNKNOWN, "msg");
        var json = CliErrorClassifier.ToJsonEnvelope(err);
        Assert.DoesNotContain("\"aad_code\":", json);
        Assert.DoesNotContain("\"trace_id\":", json);
    }
}
