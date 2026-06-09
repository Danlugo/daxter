using Daxter.Core.Configuration;

namespace Daxter.Core.Tests;

/// <summary>The writes-gate is the safety net protecting prod workspaces from agent-driven mishap.
/// Every pattern semantic — anchoring, case, the * wildcard, multi-pattern matching, empty handling —
/// is locked down here. If WorkspaceMatcher drifts, the wrong workspace gets refused (or worse,
/// the wrong one gets writable).</summary>
public class WorkspaceMatcherTests
{
    // --- Exact, case-insensitive matching ---
    [Theory]
    [InlineData("Data Hub - Dev", "Data Hub - Dev", true)]
    [InlineData("data hub - dev", "Data Hub - Dev", true)]  // case-insensitive
    [InlineData("DATA HUB - DEV", "Data Hub - Dev", true)]
    [InlineData("Data Hub - QA", "Data Hub - Dev", false)]
    public void Exact_name_is_case_insensitive(string name, string pattern, bool expected)
        => Assert.Equal(expected, WorkspaceMatcher.Matches(name, pattern));

    // --- Anchoring: pattern matches the WHOLE name, no implicit wildcards ---
    [Theory]
    [InlineData("Data Hub - Dev", "Data", false)]    // not a prefix unless * present
    [InlineData("Data Hub - Dev", "Dev", false)]     // not a suffix unless * present
    [InlineData("Data Hub - Dev", "Hub", false)]     // not a substring unless * present
    public void Bare_pattern_is_anchored_to_whole_name(string name, string pattern, bool expected)
        => Assert.Equal(expected, WorkspaceMatcher.Matches(name, pattern));

    // --- The * wildcard ---
    [Theory]
    [InlineData("Data Hub - Dev", "Data*", true)]            // prefix
    [InlineData("Data Hub - Dev", "*Dev", true)]             // suffix
    [InlineData("Data Hub - Dev", "*Hub*", true)]            // contains
    [InlineData("Data Hub - Dev", "Data*Dev", true)]         // both ends + middle
    [InlineData("Data Hub - QA", "Data*Dev", false)]         // suffix mismatch
    [InlineData("anything", "*", true)]                       // catch-all
    [InlineData("Data*Hub", "Data*Hub", true)]                // literal * → glob * still matches (rare)
    public void Star_wildcard_matches_zero_or_more_chars(string name, string pattern, bool expected)
        => Assert.Equal(expected, WorkspaceMatcher.Matches(name, pattern));

    // --- Regex specials in the workspace name aren't interpreted ---
    [Theory]
    [InlineData("Sales (Composite)", "Sales*", true)]    // (), spaces — must NOT explode regex
    [InlineData("Data+QA", "Data+QA", true)]              // + literal
    [InlineData("Prod.Sales", "Prod.Sales", true)]        // . literal (not "any char")
    [InlineData("Prod.Sales", "ProdXSales", false)]       // . is literal — so X doesn't match
    public void Regex_specials_in_name_are_treated_literally(string name, string pattern, bool expected)
        => Assert.Equal(expected, WorkspaceMatcher.Matches(name, pattern));

    // --- MatchesAny over a list ---
    [Fact]
    public void MatchesAny_returns_true_on_any_pattern_hit()
    {
        var patterns = new[] { "*Prod*", "Data*Dev", "Sales (Composite)" };
        Assert.True(WorkspaceMatcher.MatchesAny("Data Hub - Dev", patterns));
        Assert.True(WorkspaceMatcher.MatchesAny("Reporting Prod East", patterns));
        Assert.True(WorkspaceMatcher.MatchesAny("Sales (Composite)", patterns));
        Assert.False(WorkspaceMatcher.MatchesAny("Marketing", patterns));
    }

    [Fact]
    public void MatchedPattern_returns_the_actual_pattern_that_hit()
    {
        var patterns = new[] { "*Prod*", "Data*Dev" };
        Assert.Equal("Data*Dev", WorkspaceMatcher.MatchedPattern("Data Hub - Dev", patterns));
        Assert.Equal("*Prod*", WorkspaceMatcher.MatchedPattern("Sales Prod", patterns));
        Assert.Null(WorkspaceMatcher.MatchedPattern("Marketing", patterns));
    }

    // --- Empty / null patterns yield no match (NOT match-everything) ---
    [Fact]
    public void Null_patterns_yield_no_match()
        => Assert.False(WorkspaceMatcher.MatchesAny("anything", (string[]?)null));

    [Fact]
    public void Empty_patterns_yield_no_match()
        => Assert.False(WorkspaceMatcher.MatchesAny("anything", Array.Empty<string>()));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_name_yields_no_match(string? name)
        => Assert.False(WorkspaceMatcher.MatchesAny(name, new[] { "*" }));

    // --- Parse(): comma-separated input → trimmed, deduped list ---
    [Fact]
    public void Parse_trims_dedupes_and_drops_empty()
    {
        var parsed = WorkspaceMatcher.Parse("  Data*Dev , *QA, , Data*Dev,*qa");
        Assert.Equal(new[] { "Data*Dev", "*QA" }, parsed);   // dedup is OrdinalIgnoreCase
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_blank_returns_empty_list(string? csv)
        => Assert.Empty(WorkspaceMatcher.Parse(csv));
}
