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
}
