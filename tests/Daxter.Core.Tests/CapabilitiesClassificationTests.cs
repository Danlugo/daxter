using System.Text.Json;
using Daxter.Cli.Mcp;

namespace Daxter.Core.Tests;

/// <summary>Locks in the kind-classification logic in DaxterToolRuntime.CapabilitiesJson — every tool
/// marked ReadOnly = true MUST surface as "read" (not "write"), because the McpServerToolAttribute's
/// Destructive flag defaults to true and the original ordering checked Destructive first. The bug
/// made agents treat daxter_sql_query, daxter_rls, daxter_role_filters, daxter_role_members, etc. as
/// destructive — they'd refuse to call them without per-call confirmation, breaking discoverability.</summary>
public class CapabilitiesClassificationTests
{
    [Fact]
    public void ReadOnly_tools_are_classified_as_read()
    {
        var json = DaxterToolRuntime.CapabilitiesJson();
        using var doc = JsonDocument.Parse(json);
        var tools = doc.RootElement.GetProperty("tools");

        // A representative slice of read tools across every surface — DAX, REST, SQL, RLS, audit.
        // If any of these regress to "write (gated, destructive)" the classification bug is back.
        var mustBeRead = new[]
        {
            "daxter_query",            // DAX
            "daxter_dmv",              // DMV
            "daxter_rls",              // list roles
            "daxter_role_filters",     // RLS DAX expressions
            "daxter_role_members",     // RLS members
            "daxter_sql_query",        // T-SQL on Fabric SQL endpoint
            "daxter_sql_endpoints",    // endpoint discovery
            "daxter_sql_objects",      // INFORMATION_SCHEMA
            "daxter_sql_export",       // streaming CSV export
            "daxter_connections",      // Fabric REST
            "daxter_workspaces",       // Power BI REST
            "daxter_capabilities",     // self-introspection
            "daxter_copy_jobs",        // Fabric Copy Job list
            "daxter_copy_job_definition",
            "daxter_notebooks",        // Fabric Notebook list
            "daxter_notebook_definition",
            "daxter_item_runs",        // job-instance history
            "daxter_item_job_status",  // single job-instance status
        };

        foreach (var tool in tools.EnumerateArray())
        {
            var name = tool.GetProperty("name").GetString();
            if (name is null || !mustBeRead.Contains(name)) continue;
            var kind = tool.GetProperty("kind").GetString();
            Assert.True(kind == "read",
                $"Tool '{name}' is classified as '{kind}' but should be 'read' (ReadOnly = true on the attribute). " +
                "Check the order of checks in CapabilitiesJson — ReadOnly must be checked BEFORE Destructive.");
        }
    }

    [Fact]
    public void Capabilities_returns_a_nonzero_tool_count_and_a_version()
    {
        // Sanity — reflection must find every [McpServerTool] in DaxterTools. The number is allowed
        // to grow; this just catches "we accidentally broke discovery" regressions.
        var json = DaxterToolRuntime.CapabilitiesJson();
        using var doc = JsonDocument.Parse(json);
        var count = doc.RootElement.GetProperty("tool_count").GetInt32();
        Assert.True(count >= 50, $"Expected at least 50 tools, got {count}. Reflection-based discovery likely broke.");
        Assert.NotNull(doc.RootElement.GetProperty("version").GetString());
    }
}
