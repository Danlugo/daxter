using Daxter.Core.Connection;

namespace Daxter.Core.Tests;

public class XmlaConnectionStringTests
{
    [Theory]
    [InlineData("Sales Analytics", null,
        "Data Source=powerbi://api.powerbi.com/v1.0/myorg/Sales Analytics;")]
    [InlineData("Sales Analytics", "Retail Model",
        "Data Source=powerbi://api.powerbi.com/v1.0/myorg/Sales Analytics;Initial Catalog=Retail Model;")]
    [InlineData("  Trimmed  ", "  Model  ",
        "Data Source=powerbi://api.powerbi.com/v1.0/myorg/Trimmed;Initial Catalog=Model;")]
    [InlineData("powerbi://api.powerbi.com/v1.0/myorg/Already", null,
        "Data Source=powerbi://api.powerbi.com/v1.0/myorg/Already;")]
    [InlineData("asazure://westus.asazure.windows.net/srv", "db",
        "Data Source=asazure://westus.asazure.windows.net/srv;Initial Catalog=db;")]
    public void Build_produces_expected_connection_string(string workspace, string? dataset, string expected)
    {
        Assert.Equal(expected, XmlaConnectionString.Build(workspace, dataset));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Build_throws_when_workspace_missing(string? workspace)
    {
        Assert.Throws<ArgumentException>(() => XmlaConnectionString.Build(workspace!));
    }

    [Fact]
    public void Build_appends_roles_and_effective_user_for_rls()
    {
        var conn = XmlaConnectionString.Build("WS", "Model", roles: "Manager", effectiveUserName: "u@x.com");
        Assert.Contains("Initial Catalog=Model;", conn);
        Assert.Contains("Roles=Manager;", conn);
        Assert.Contains("EffectiveUserName=u@x.com;", conn);
    }

    [Fact]
    public void Build_omits_impersonation_when_not_supplied()
    {
        var conn = XmlaConnectionString.Build("WS", "Model");
        Assert.DoesNotContain("Roles=", conn);
        Assert.DoesNotContain("EffectiveUserName=", conn);
    }

    // Regression: a model name with an apostrophe was injected into Initial Catalog unquoted,
    // corrupting the OLE-DB parse so the AdomdConnection ctor threw before connecting. The value
    // must be enclosed in double quotes (no caller-side doubling needed).
    [Theory]
    [InlineData("Reseller's Margin", "Initial Catalog=\"Reseller's Margin\";")]  // single quote → wrap in double quotes
    [InlineData("Has;Semicolon", "Initial Catalog=\"Has;Semicolon\";")]          // semicolon → must quote
    [InlineData("Say \"Hi\"", "Initial Catalog='Say \"Hi\"';")]                  // double quote → wrap in single quotes
    [InlineData("Mix'd \"both\"", "Initial Catalog=\"Mix'd \"\"both\"\"\";")]    // both → double the embedded double quotes
    [InlineData("Plain Model", "Initial Catalog=Plain Model;")]                  // no special chars → untouched
    public void Build_quotes_dataset_with_special_characters(string dataset, string expectedFragment)
    {
        var conn = XmlaConnectionString.Build("WS", dataset);
        Assert.Contains(expectedFragment, conn);
    }

    [Fact]
    public void Build_quotes_workspace_with_apostrophe()
    {
        var conn = XmlaConnectionString.Build("O'Brien Workspace", "Model");
        Assert.Contains("Data Source=\"powerbi://api.powerbi.com/v1.0/myorg/O'Brien Workspace\";", conn);
    }
}
