using System.Text.Json;
using Daxter.Core.Maintenance;
using Daxter.Core.Scheduling;

namespace Daxter.Core.Tests;

/// <summary>The v1.39.0 Apply Refresh Policy plumbing. Pins the Enhanced-Refresh body shape
/// for both entry-point variants so a future refactor can't accidentally drop the scoping or
/// the safety guard against partition-targeted applyRefreshPolicy.
///
/// Two-path design (see memory: daxter-apply-refresh-policy-rule):
///   Option A — bundled with full refresh (CLI: refresh model --apply-policy; MCP: daxter_refresh apply_policy=true)
///              → applyRefreshPolicy=true, NO objects list. Power BI walks the policy on tables
///                that have one and does a normal refresh on the rest.
///   Option B — standalone (CLI: refresh apply-policy; MCP: daxter_apply_refresh_policy)
///              → applyRefreshPolicy=true, objects list scoped to ONLY policy tables.
///                Non-policy tables UNTOUCHED. Mirrors Tabular Editor's per-table semantics.</summary>
public sealed class ApplyRefreshPolicyTests
{
    private static RefreshSpec ModelSpec(bool applyPolicy = false, IReadOnlyList<string>? policyTables = null)
        => new(RefreshKind.Model, "ws", "ds", null, null, PartitionOrder.NewestFirst,
            RefreshType.Full, null, 0, applyPolicy, policyTables);

    private static RefreshSpec TableSpec(string table, bool applyPolicy = false, IReadOnlyList<string>? policyTables = null)
        => new(RefreshKind.Table, "ws", "ds", table, null, PartitionOrder.NewestFirst,
            RefreshType.Full, null, 0, applyPolicy, policyTables);

    private static RefreshSpec PartitionSpec()
        => new(RefreshKind.Partition, "ws", "ds", "Sales", "Sales 2025-01", PartitionOrder.NewestFirst,
            RefreshType.Full, null, 0, true);   // ApplyPolicy=true but partition kind — invalid combo

    // ── Option A — bundled with full refresh ──────────────────────────────────────────────────

    [Fact]
    public void OptionA_model_with_applyPolicy_emits_applyRefreshPolicy_true_and_no_objects_list()
    {
        // "Refresh the model + while you're at it apply policy." Power BI walks policy on
        // tables that have one; non-policy tables get a normal refresh. The wire-shape: no
        // `objects` key at all (whole model), `applyRefreshPolicy: true`.
        var body = EnhancedRefresh.BuildBody(ModelSpec(applyPolicy: true), maxParallelism: 4);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("applyRefreshPolicy").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("objects", out _),
            "Option A must NOT carry an objects list — whole-model refresh");
        Assert.Equal("transactional", doc.RootElement.GetProperty("commitMode").GetString());
    }

    // ── Option B — standalone, surgical ──────────────────────────────────────────────────────

    [Fact]
    public void OptionB_with_PolicyTables_list_emits_scoped_objects_array_and_applyRefreshPolicy_true()
    {
        // The standalone "apply refresh policy" surgical path: explicit list of policy
        // tables, scope the API call to only those. Non-policy tables are not in the objects
        // list so the service doesn't refresh them.
        var policyTables = new[] { "FACT - Trans Line", "FACT - Inventory" };
        var body = EnhancedRefresh.BuildBody(ModelSpec(applyPolicy: true, policyTables: policyTables), 4);
        using var doc = JsonDocument.Parse(body);
        Assert.True(doc.RootElement.GetProperty("applyRefreshPolicy").GetBoolean());

        var objects = doc.RootElement.GetProperty("objects");
        Assert.Equal(2, objects.GetArrayLength());
        Assert.Equal("FACT - Trans Line", objects[0].GetProperty("table").GetString());
        Assert.Equal("FACT - Inventory", objects[1].GetProperty("table").GetString());
        // No 'partition' key on those entries — table-level scoping.
        Assert.False(objects[0].TryGetProperty("partition", out _));
    }

    [Fact]
    public void OptionB_with_single_policy_table_emits_one_element_objects_array()
    {
        var body = EnhancedRefresh.BuildBody(ModelSpec(applyPolicy: true, policyTables: new[] { "FACT - Trans Line" }), 4);
        using var doc = JsonDocument.Parse(body);
        var objects = doc.RootElement.GetProperty("objects");
        Assert.Equal(1, objects.GetArrayLength());
        Assert.Equal("FACT - Trans Line", objects[0].GetProperty("table").GetString());
    }

    // ── Safety: partition kinds refuse applyRefreshPolicy:true ────────────────────────────────

    [Fact]
    public void Partition_kind_with_ApplyPolicy_true_still_emits_applyRefreshPolicy_false()
    {
        // Even if a caller sets ApplyPolicy=true on a partition job (the CLI/MCP refuse this,
        // but BuildBody is defensive), the wire-shape MUST be applyRefreshPolicy=false. Mixing
        // applyRefreshPolicy=true with commitMode=partialBatch is a Power BI API error.
        var body = EnhancedRefresh.BuildBody(PartitionSpec(), 4);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.GetProperty("applyRefreshPolicy").GetBoolean());
        Assert.Equal("partialBatch", doc.RootElement.GetProperty("commitMode").GetString());
    }

    // ── Back-compat: ApplyPolicy=false (the v1.x default) is unchanged ────────────────────────

    [Fact]
    public void Model_refresh_without_ApplyPolicy_omits_applyRefreshPolicy_entirely()
    {
        // Pre-v1.39.0 behaviour preserved: a regular `daxter refresh model` produces a body
        // with NO applyRefreshPolicy field at all (the v1.x wire shape Semantix and every
        // other consumer have been getting). The service treats absence as false.
        var body = EnhancedRefresh.BuildBody(ModelSpec(), 4);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("applyRefreshPolicy", out _),
            "v1.x callers must see the original body shape — no new keys appear when ApplyPolicy=false");
    }

    [Fact]
    public void Table_refresh_without_ApplyPolicy_emits_table_objects_list_unchanged()
    {
        var body = EnhancedRefresh.BuildBody(TableSpec("FACT - Trans Line"), 4);
        using var doc = JsonDocument.Parse(body);
        Assert.False(doc.RootElement.TryGetProperty("applyRefreshPolicy", out _));
        var objects = doc.RootElement.GetProperty("objects");
        Assert.Equal(1, objects.GetArrayLength());
        Assert.Equal("FACT - Trans Line", objects[0].GetProperty("table").GetString());
    }

    // ── Option A vs Option B distinguishable on the wire ─────────────────────────────────────

    [Fact]
    public void OptionA_and_OptionB_produce_distinguishable_bodies()
    {
        // Two operators should be able to tell the two paths apart from the wire body alone —
        // Option A has no objects list (whole model); Option B carries an explicit one.
        var bodyA = EnhancedRefresh.BuildBody(ModelSpec(applyPolicy: true), 4);
        var bodyB = EnhancedRefresh.BuildBody(ModelSpec(applyPolicy: true, policyTables: new[] { "T" }), 4);
        Assert.DoesNotContain("\"objects\":", bodyA);
        Assert.Contains("\"objects\":", bodyB);
        // Both carry the applyRefreshPolicy flag.
        Assert.Contains("\"applyRefreshPolicy\":true", bodyA);
        Assert.Contains("\"applyRefreshPolicy\":true", bodyB);
    }

    [Fact]
    public void RefreshSpec_default_ApplyPolicy_is_false_and_default_PolicyTables_is_null()
    {
        // Defensive — the record's default values are the v1.x backwards-compat shape.
        // If someone reorders the constructor positionals, this test catches it.
        var spec = new RefreshSpec(RefreshKind.Model, "ws", "ds", null, null, PartitionOrder.NewestFirst);
        Assert.False(spec.ApplyPolicy);
        Assert.Null(spec.PolicyTables);
    }
}
