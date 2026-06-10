namespace Daxter.Core.Tests;

/// <summary>Pin the env-var → identity-card contract that the Semantics fleet orchestrator
/// depends on. If we ever rename the env vars or change the omit-when-unset behavior, every
/// downstream Semantics dashboard breaks — these tests fail loudly first.</summary>
public sealed class TenantInfoTests : IDisposable
{
    private readonly string? _priorTenant;
    private readonly string? _priorLabel;

    public TenantInfoTests()
    {
        // Snapshot whatever's in the host env so the tests don't leak into other tests that
        // happen to run after this one in the same process.
        _priorTenant = Environment.GetEnvironmentVariable("DAXTER_TENANT_ID");
        _priorLabel = Environment.GetEnvironmentVariable("DAXTER_LABEL");
        Environment.SetEnvironmentVariable("DAXTER_TENANT_ID", null);
        Environment.SetEnvironmentVariable("DAXTER_LABEL", null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("DAXTER_TENANT_ID", _priorTenant);
        Environment.SetEnvironmentVariable("DAXTER_LABEL", _priorLabel);
    }

    [Fact]
    public void Both_null_when_no_env_vars()
    {
        Assert.Null(TenantInfo.TenantId);
        Assert.Null(TenantInfo.Label);
        Assert.False(TenantInfo.IsConfigured);
    }

    [Fact]
    public void Whitespace_is_treated_as_unset()
    {
        // A misconfigured Semantics container that exports "" or "   " for the env var should
        // STILL behave as if the tenant is unset — otherwise the home page banner shows a
        // blank rectangle.
        Environment.SetEnvironmentVariable("DAXTER_TENANT_ID", "   ");
        Environment.SetEnvironmentVariable("DAXTER_LABEL", "");
        Assert.Null(TenantInfo.TenantId);
        Assert.Null(TenantInfo.Label);
        Assert.False(TenantInfo.IsConfigured);
    }

    [Fact]
    public void Trims_surrounding_whitespace_on_set()
    {
        Environment.SetEnvironmentVariable("DAXTER_TENANT_ID", "  inspire  ");
        Environment.SetEnvironmentVariable("DAXTER_LABEL", "  Inspire Brands — Prod  ");
        Assert.Equal("inspire", TenantInfo.TenantId);
        Assert.Equal("Inspire Brands — Prod", TenantInfo.Label);
        Assert.True(TenantInfo.IsConfigured);
    }

    [Fact]
    public void IsConfigured_true_when_only_one_set()
    {
        Environment.SetEnvironmentVariable("DAXTER_TENANT_ID", "x");
        Assert.True(TenantInfo.IsConfigured);
        Environment.SetEnvironmentVariable("DAXTER_TENANT_ID", null);
        Environment.SetEnvironmentVariable("DAXTER_LABEL", "Label only");
        Assert.True(TenantInfo.IsConfigured);
    }

    [Fact]
    public void MergeInto_omits_unset_fields()
    {
        // A response that didn't get a tenant_id stamped should not contain a "tenant_id":null
        // key — Semantics' JSON-shape contract is "field is present iff value is non-null".
        var dict = new Dictionary<string, object?>();
        TenantInfo.MergeInto(dict);
        Assert.False(dict.ContainsKey("tenant_id"));
        Assert.False(dict.ContainsKey("label"));
    }

    [Fact]
    public void MergeInto_writes_both_keys_when_set()
    {
        Environment.SetEnvironmentVariable("DAXTER_TENANT_ID", "inspire");
        Environment.SetEnvironmentVariable("DAXTER_LABEL", "Inspire Brands");
        var dict = new Dictionary<string, object?>();
        TenantInfo.MergeInto(dict);
        Assert.Equal("inspire", dict["tenant_id"]);
        Assert.Equal("Inspire Brands", dict["label"]);
    }

    [Fact]
    public void MergeInto_does_not_clobber_existing_keys_that_arent_being_set()
    {
        Environment.SetEnvironmentVariable("DAXTER_TENANT_ID", "inspire");
        // No DAXTER_LABEL — so MergeInto should leave a pre-existing "label" key alone.
        var dict = new Dictionary<string, object?> { ["label"] = "preexisting" };
        TenantInfo.MergeInto(dict);
        Assert.Equal("inspire", dict["tenant_id"]);
        Assert.Equal("preexisting", dict["label"]);   // not overwritten with null
    }
}
