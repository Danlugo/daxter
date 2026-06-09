using Daxter.Core.Configuration;

namespace Daxter.Core.Tests;

/// <summary>The precedence rule baked into <see cref="DaxterConfig.IsReadOnlyTarget"/>:
/// <list type="number">
/// <item>Deny-list match (ReadOnlyWorkspaces OR legacy ProdWorkspaces) → always read-only.</item>
/// <item>Allow-list (WriteWorkspaces) non-empty AND workspace NOT in it → read-only.</item>
/// <item>Legacy heuristics (env=prod, name contains "prod").</item>
/// <item>Default: writable.</item>
/// </list>
/// These tests pin every leg of that ladder so a future "small tweak" can't accidentally widen
/// the writable surface without failing a build.</summary>
public class ReadOnlyTargetTests
{
    private static DaxterConfig Cfg(string workspace, string[]? readOnly = null, string[]? writeAllowed = null,
        string[]? legacyProd = null, string? env = null) => new()
    {
        Workspace = workspace,
        ReadOnlyWorkspaces = readOnly ?? Array.Empty<string>(),
        WriteWorkspaces = writeAllowed ?? Array.Empty<string>(),
        ProdWorkspaces = legacyProd ?? Array.Empty<string>(),
        Environment = env,
    };

    // --- 1. Deny-list wins outright ---
    [Fact]
    public void ReadOnly_pattern_match_is_read_only_even_with_allow_list_match()
    {
        // Workspace matches BOTH read-only and write-allowed lists → deny wins.
        var cfg = Cfg("Data Hub - Dev",
            readOnly: new[] { "Data*Dev" },
            writeAllowed: new[] { "Data*Dev" });
        Assert.True(cfg.IsReadOnlyTarget());
        Assert.StartsWith("read-only pattern", cfg.ReadOnlyReason());
    }

    [Fact]
    public void Legacy_prod_workspaces_still_locks()
    {
        var cfg = Cfg("Sales Analytics", legacyProd: new[] { "Sales*" });
        Assert.True(cfg.IsReadOnlyTarget());
        Assert.Contains("prod-workspaces", cfg.ReadOnlyReason()!);
    }

    // --- 2. Allow-list restricts when non-empty ---
    [Fact]
    public void Allow_list_restricts_to_its_members()
    {
        // "Marketing" matches no allow-list pattern → locked.
        var cfg = Cfg("Marketing", writeAllowed: new[] { "Data*Dev", "*QA" });
        Assert.True(cfg.IsReadOnlyTarget());
        Assert.Equal("not in the write-allowed patterns", cfg.ReadOnlyReason());
    }

    [Fact]
    public void Allow_list_match_unlocks_writes()
    {
        var cfg = Cfg("Data Hub - Dev", writeAllowed: new[] { "Data*Dev", "*QA" });
        Assert.False(cfg.IsReadOnlyTarget());
        Assert.Null(cfg.ReadOnlyReason());
    }

    [Fact]
    public void Empty_allow_list_does_NOT_lock_everything()
    {
        // Common mistake to guard against: an empty allow-list is "no allow-list", not "lock everything".
        var cfg = Cfg("Marketing");   // no patterns at all
        Assert.False(cfg.IsReadOnlyTarget());
    }

    // --- 3. Legacy heuristics (env + "*prod*" in name) — only when no explicit lists ---
    [Fact]
    public void Env_prod_locks_target()
    {
        var cfg = Cfg("Sales", env: "prod");
        Assert.True(cfg.IsReadOnlyTarget());
        Assert.Equal("DAXTER_ENV=prod", cfg.ReadOnlyReason());
    }

    [Fact]
    public void Workspace_name_containing_prod_locks_target()
    {
        var cfg = Cfg("Reporting Production East");
        Assert.True(cfg.IsReadOnlyTarget());
        Assert.Contains("workspace name", cfg.ReadOnlyReason()!);
    }

    // --- Combined real-world scenario ---
    [Fact]
    public void Dev_QA_allow_list_locks_prod_unlocks_dev_and_qa()
    {
        // Exactly the user's setup: "I can put Data Hub - Dev, Data Hub - QA — only those I can modify."
        // Expressed with patterns: Data*Dev, Data*QA (or *QA).
        var allow = new[] { "Data*Dev", "Data Hub - QA" };

        Assert.False(Cfg("Data Hub - Dev", writeAllowed: allow).IsReadOnlyTarget());
        Assert.False(Cfg("Data Hub - QA", writeAllowed: allow).IsReadOnlyTarget());
        Assert.True(Cfg("Data Hub", writeAllowed: allow).IsReadOnlyTarget());           // prod
        Assert.True(Cfg("Sales Analytics", writeAllowed: allow).IsReadOnlyTarget());
    }

    [Fact]
    public void Wildcard_deny_list_locks_everything_matching_prod_substring()
    {
        // "Lock anything with PROD in its name no matter what allow-list says."
        var cfg = Cfg("Sales Prod East",
            readOnly: new[] { "*Prod*" },
            writeAllowed: new[] { "*" });   // catch-all allow-list still loses to deny
        Assert.True(cfg.IsReadOnlyTarget());
    }

    // --- Legacy alias still works (kept for old callsites) ---
    [Fact]
#pragma warning disable CS0618 // testing the obsolete alias on purpose
    public void IsProductionTarget_alias_matches_IsReadOnlyTarget()
    {
        var cfg = Cfg("Data Hub - Dev", readOnly: new[] { "Data*" });
        Assert.Equal(cfg.IsReadOnlyTarget(), cfg.IsProductionTarget());
    }
#pragma warning restore CS0618
}
