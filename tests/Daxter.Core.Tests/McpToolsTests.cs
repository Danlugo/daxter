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
            "daxter_rls", "daxter_diff_measures", "daxter_refresh_history",
            "daxter_workspaces", "daxter_datasets", "daxter_reports", "daxter_lineage",
            "daxter_export", "daxter_permissions", "daxter_gateways", "daxter_datasources",
            "daxter_test_rls", "daxter_pipelines", "daxter_pipeline_stages",
            "daxter_pipeline_operations",
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
}
