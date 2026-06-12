using Daxter.Core.Configuration;

namespace Daxter.Core.Tests;

/// <summary>The v1.46.0 permission ladder (read &lt; execute &lt; modify &lt; full) and the
/// <see cref="PermissionPolicy"/> resolver: env ceiling + active level + per-workspace ceilings,
/// composed by min, with most-restrictive-wins when patterns overlap.</summary>
public sealed class PermissionPolicyTests
{
    private static PermissionPolicy P(PermissionLevel ceiling, PermissionLevel active,
        params (string, PermissionLevel)[] ws)
        => new(ceiling, ws.ToList(), active, PermissionLevel.Full);

    [Theory]
    [InlineData("read", PermissionLevel.Read)]
    [InlineData("execute", PermissionLevel.Execute)]
    [InlineData("read+execute", PermissionLevel.Execute)]
    [InlineData("modify", PermissionLevel.Modify)]
    [InlineData("write", PermissionLevel.Modify)]
    [InlineData("full", PermissionLevel.Full)]
    [InlineData("FULL", PermissionLevel.Full)]
    public void Parses_level_tokens(string token, PermissionLevel expected)
    {
        Assert.True(PermissionLevels.TryParse(token, out var l));
        Assert.Equal(expected, l);
    }

    [Fact]
    public void Levels_are_cumulative()
    {
        Assert.True(PermissionLevel.Full >= PermissionLevel.Modify);
        Assert.True(PermissionLevel.Modify >= PermissionLevel.Execute);
        Assert.True(PermissionLevel.Execute >= PermissionLevel.Read);
    }

    [Fact]
    public void Effective_is_min_of_active_and_ceiling()
    {
        // Active modify, but env ceiling caps at execute → effective execute (can refresh, can't edit).
        var p = P(PermissionLevel.Execute, PermissionLevel.Modify);
        Assert.Equal(PermissionLevel.Execute, p.Effective(null));
        Assert.True(p.Allows(PermissionLevel.Execute, null));
        Assert.False(p.Allows(PermissionLevel.Modify, null));
    }

    [Fact]
    public void Console_cannot_exceed_env_ceiling()
    {
        // Active full, ceiling read+execute → still capped at execute. Inside can't escalate.
        var p = P(PermissionLevel.Execute, PermissionLevel.Full);
        Assert.False(p.Allows(PermissionLevel.Modify, null));
        Assert.True(p.Allows(PermissionLevel.Execute, null));
    }

    [Fact]
    public void Workspace_ceiling_caps_a_specific_workspace_lower()
    {
        // Global full, but *Prod* capped at execute → refresh prod, never edit it.
        var p = P(PermissionLevel.Full, PermissionLevel.Full, ("*Prod*", PermissionLevel.Execute));
        Assert.True(p.Allows(PermissionLevel.Full, "Sales Dev"));      // unlisted → global full
        Assert.True(p.Allows(PermissionLevel.Execute, "Sales Prod"));  // refresh allowed
        Assert.False(p.Allows(PermissionLevel.Modify, "Sales Prod"));  // edit blocked
    }

    [Fact]
    public void Most_restrictive_pattern_wins_on_overlap()
    {
        var p = P(PermissionLevel.Full, PermissionLevel.Full,
            ("Sales*", PermissionLevel.Modify), ("*Prod*", PermissionLevel.Read));
        Assert.Equal(PermissionLevel.Read, p.Effective("Sales Prod"));   // both match → min
    }

    [Fact]
    public void Read_level_blocks_everything_including_refresh()
    {
        var p = P(PermissionLevel.Read, PermissionLevel.Read);
        Assert.False(p.Allows(PermissionLevel.Execute, null));
        Assert.True(p.Allows(PermissionLevel.Read, null));
    }

    [Theory]
    [InlineData("Dev*=full;*Prod*=read+execute", "Acme Dev", PermissionLevel.Full)]
    [InlineData("Dev*=full;*Prod*=read+execute", "Acme Prod", PermissionLevel.Execute)]
    public void Parses_workspace_levels_string(string spec, string workspace, PermissionLevel expected)
    {
        var ceilings = PermissionPolicy.ParseWorkspaceLevels(spec);
        var p = new PermissionPolicy(PermissionLevel.Full, ceilings, PermissionLevel.Full, PermissionLevel.Full);
        Assert.Equal(expected, p.Effective(workspace));
    }

    [Fact]
    public void Local_level_is_independent_of_the_estate()
    {
        var p = new PermissionPolicy(PermissionLevel.Read, new List<(string, PermissionLevel)>(),
            PermissionLevel.Read, PermissionLevel.Full);
        Assert.False(p.Allows(PermissionLevel.Execute, null));   // estate locked
        Assert.True(p.AllowsLocal(PermissionLevel.Full));        // scratch still open
    }
}
