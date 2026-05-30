using System.Reflection;
using Daxter.Cli.Mcp;
using ModelContextProtocol.Server;

namespace Daxter.Core.Tests;

public class McpToolsTests
{
    private static IReadOnlyList<string> ToolNames() =>
        typeof(DaxterTools)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(n => n is not null)
            .Select(n => n!)
            .ToList();

    [Fact]
    public void Exposes_all_expected_tools()
    {
        var names = ToolNames().ToHashSet(StringComparer.Ordinal);
        string[] expected =
        [
            "daxter_login",
            "daxter_query", "daxter_dmv", "daxter_list_tables", "daxter_measures",
            "daxter_measure", "daxter_mcode", "daxter_parameters", "daxter_partitions",
            "daxter_columns",
            "daxter_rls", "daxter_diff_measures", "daxter_refresh_history",
            "daxter_workspaces", "daxter_datasets", "daxter_reports", "daxter_lineage",
            "daxter_export", "daxter_permissions", "daxter_gateways", "daxter_datasources",
            "daxter_test_rls", "daxter_pipelines", "daxter_pipeline_stages",
            "daxter_pipeline_operations", "daxter_pipeline_rules",
            "daxter_pipeline_models_without_rules", "daxter_pipeline_param_check",
            "daxter_audit_list_saved", "daxter_audit_run_saved", "daxter_audit_run_all_saved",
            "daxter_refresh", "daxter_clear_cache",
        ];

        foreach (var tool in expected)
        {
            Assert.Contains(tool, names);
        }
    }

    [Fact]
    public void Tool_names_are_unique()
    {
        var names = ToolNames();
        Assert.Equal(names.Count, names.Distinct().Count());
    }

    [Fact]
    public void Has_both_read_and_gated_write_tools()
    {
        var names = ToolNames().ToHashSet(StringComparer.Ordinal);
        Assert.Contains("daxter_query", names);   // read
        Assert.Contains("daxter_refresh", names); // gated write
    }

    [Fact]
    public void FormatLoginPrompt_with_url_and_code_is_click_first()
    {
        var msg = DaxterToolRuntime.FormatLoginPrompt("https://microsoft.com/devicelogin", "ABCD-EFGH");

        Assert.Contains("https://microsoft.com/devicelogin", msg);  // clickable link
        Assert.Contains("ABCD-EFGH", msg);                          // the code
        Assert.Contains("Open", msg);                               // step 1
        Assert.Contains("Enter code", msg);                         // step 2
        Assert.Contains("list my workspaces", msg);                 // next step
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("https://microsoft.com/devicelogin", null)]
    public void FormatLoginPrompt_without_url_or_code_says_already_signed_in(string? url, string? code)
    {
        var msg = DaxterToolRuntime.FormatLoginPrompt(url, code);

        Assert.Contains("already signed in", msg, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Enter code", msg);
    }
}
