using Daxter.Core;
using Daxter.Core.Connection;
using Daxter.Core.Editing;
using Daxter.Core.Query;

namespace Daxter.Core.Tests;

public class ModelEditServiceTests
{
    private sealed class FakeSession(Func<string, QueryResult> handler) : IXmlaSession
    {
        public List<string> Commands { get; } = [];
        public QueryResult Execute(string query) => handler(query);
        public void ExecuteCommand(string command) => Commands.Add(command);
        public void Dispose() { }
    }

    private static ModelEditService ForModel(FakeSession? session = null) =>
        new(session ?? new FakeSession(_ => QueryResult.Empty), "Retail Model");

    [Fact]
    public void Upsert_emits_createOrReplace_targeting_the_measure_path()
    {
        var tmsl = ForModel().BuildMeasureUpsert("Sales", "Revenue", "SUM(Sales[Amount])");

        Assert.Contains("\"createOrReplace\"", tmsl);
        Assert.Contains("\"database\": \"Retail Model\"", tmsl);
        Assert.Contains("\"table\": \"Sales\"", tmsl);
        Assert.Contains("\"measure\": \"Revenue\"", tmsl);          // object path
        Assert.Contains("\"name\": \"Revenue\"", tmsl);             // measure body
        Assert.Contains("\"expression\": \"SUM(Sales[Amount])\"", tmsl);
    }

    [Fact]
    public void Upsert_includes_optional_fields_only_when_supplied()
    {
        var withOpts = ForModel().BuildMeasureUpsert(
            "Sales", "Revenue", "1", formatString: "$#,0.00", displayFolder: "KPIs", description: "Total");
        Assert.Contains("\"formatString\": \"$#,0.00\"", withOpts);
        Assert.Contains("\"displayFolder\": \"KPIs\"", withOpts);
        Assert.Contains("\"description\": \"Total\"", withOpts);

        var minimal = ForModel().BuildMeasureUpsert("Sales", "Revenue", "1");
        Assert.DoesNotContain("formatString", minimal);
        Assert.DoesNotContain("displayFolder", minimal);
        Assert.DoesNotContain("description", minimal);
    }

    [Fact]
    public void Delete_emits_delete_targeting_the_measure_path()
    {
        var tmsl = ForModel().BuildMeasureDelete("Sales", "Revenue");
        Assert.Contains("\"delete\"", tmsl);
        Assert.Contains("\"measure\": \"Revenue\"", tmsl);
        Assert.DoesNotContain("createOrReplace", tmsl);
    }

    [Theory]
    [InlineData("", "M", "1")]      // missing table
    [InlineData("T", "", "1")]      // missing name
    [InlineData("T", "M", "")]      // missing DAX
    public void Upsert_validates_required_fields(string table, string name, string dax)
        => Assert.Throws<DaxterException>(() => ForModel().BuildMeasureUpsert(table, name, dax));

    [Fact]
    public void EnsureTableExists_throws_when_absent()
    {
        var session = new FakeSession(_ => new QueryResult(["Name"], [["Sales"], ["Date"]]));
        Assert.Throws<DaxterException>(() => ForModel(session).EnsureTableExists("Nope"));
    }

    [Fact]
    public void EnsureTableExists_passes_when_present_case_insensitive()
    {
        var session = new FakeSession(_ => new QueryResult(["Name"], [["Sales"]]));
        ForModel(session).EnsureTableExists("sales");   // no throw
    }

    [Fact]
    public void Execute_sends_the_command_over_the_session()
    {
        var session = new FakeSession(_ => QueryResult.Empty);
        var svc = ForModel(session);
        svc.Execute(svc.BuildMeasureDelete("Sales", "Revenue"));
        Assert.Single(session.Commands);
        Assert.Contains("\"delete\"", session.Commands[0]);
    }

    [Fact]
    public void Constructor_requires_a_dataset()
        => Assert.Throws<DaxterException>(() => new ModelEditService(new FakeSession(_ => QueryResult.Empty), ""));

    // ---- parameters / expressions ----

    [Fact]
    public void Expression_upsert_targets_model_level_expression_path()
    {
        var tmsl = ForModel().BuildExpressionUpsert("ServerName", "\"sql.contoso.com\" meta [IsParameterQuery=true]");
        Assert.Contains("\"createOrReplace\"", tmsl);
        Assert.Contains("\"expression\": \"ServerName\"", tmsl);   // object path (no table)
        Assert.Contains("\"kind\": \"m\"", tmsl);
        Assert.DoesNotContain("\"table\"", tmsl);
    }

    [Fact]
    public void Expression_delete_targets_the_expression()
    {
        var tmsl = ForModel().BuildExpressionDelete("ServerName");
        Assert.Contains("\"delete\"", tmsl);
        Assert.Contains("\"expression\": \"ServerName\"", tmsl);
    }

    // ---- roles ----

    [Fact]
    public void Role_upsert_emits_members_and_table_filters()
    {
        var tmsl = ForModel().BuildRoleUpsert("Manager", "read",
            [new RoleMember("dana@contoso.com")],
            [new TableFilter("Sales", "[Region] = \"US\"")]);

        Assert.Contains("\"role\": \"Manager\"", tmsl);                 // object path
        Assert.Contains("\"modelPermission\": \"read\"", tmsl);
        Assert.Contains("\"memberName\": \"dana@contoso.com\"", tmsl);
        Assert.Contains("\"filterExpression\": \"[Region] = \\u0022US\\u0022\"", tmsl);
    }

    [Fact]
    public void Role_upsert_defaults_permission_and_omits_empty_collections()
    {
        var tmsl = ForModel().BuildRoleUpsert("Viewer");
        Assert.Contains("\"modelPermission\": \"read\"", tmsl);
        Assert.DoesNotContain("members", tmsl);
        Assert.DoesNotContain("tablePermissions", tmsl);
    }

    [Fact]
    public void Role_delete_targets_the_role()
        => Assert.Contains("\"role\": \"Manager\"", ForModel().BuildRoleDelete("Manager"));

    // ---- calculated columns ----

    [Fact]
    public void CalculatedColumn_upsert_is_typed_calculated()
    {
        var tmsl = ForModel().BuildCalculatedColumnUpsert("Sales", "Margin", "[Revenue]-[Cost]", dataType: "double");
        Assert.Contains("\"column\": \"Margin\"", tmsl);               // object path
        Assert.Contains("\"type\": \"calculated\"", tmsl);
        Assert.Contains("\"expression\": \"[Revenue]-[Cost]\"", tmsl);
        Assert.Contains("\"dataType\": \"double\"", tmsl);
    }

    [Fact]
    public void Column_delete_targets_table_and_column()
    {
        var tmsl = ForModel().BuildColumnDelete("Sales", "Margin");
        Assert.Contains("\"delete\"", tmsl);
        Assert.Contains("\"table\": \"Sales\"", tmsl);
        Assert.Contains("\"column\": \"Margin\"", tmsl);
    }

    // ---- partition source ----

    [Fact]
    public void PartitionSource_set_emits_m_source()
    {
        var tmsl = ForModel().BuildPartitionSourceSet("Sales", "Sales", "let Source = ... in Source");
        Assert.Contains("\"partition\": \"Sales\"", tmsl);
        Assert.Contains("\"type\": \"m\"", tmsl);
        Assert.Contains("\"expression\": \"let Source", tmsl);
    }

    // ---- tables ----

    [Fact]
    public void CalculatedTable_create_wraps_dax_in_a_calculated_partition()
    {
        var tmsl = ForModel().BuildCalculatedTableCreate("DimDate", "CALENDARAUTO()");
        Assert.Contains("\"table\": \"DimDate\"", tmsl);
        Assert.Contains("\"partitions\"", tmsl);
        Assert.Contains("\"type\": \"calculated\"", tmsl);
        Assert.Contains("\"expression\": \"CALENDARAUTO()\"", tmsl);
    }

    [Fact]
    public void Table_delete_targets_the_table()
    {
        var tmsl = ForModel().BuildTableDelete("DimDate");
        Assert.Contains("\"delete\"", tmsl);
        Assert.Contains("\"table\": \"DimDate\"", tmsl);
    }

    [Theory]
    [InlineData("", "1")]   // missing name
    [InlineData("X", "")]   // missing expression
    public void Expression_validates_required_fields(string name, string m)
        => Assert.Throws<DaxterException>(() => ForModel().BuildExpressionUpsert(name, m));
}
