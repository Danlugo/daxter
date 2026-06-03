using Daxter.Core;
using Daxter.Core.Connection;
using Daxter.Core.Maintenance;
using Daxter.Core.Query;

namespace Daxter.Core.Tests;

public class MaintenanceServiceTests
{
    private sealed class FakeSession : IXmlaSession
    {
        private readonly Func<string, QueryResult> _handler;
        public List<string> Commands { get; } = [];
        public FakeSession(Func<string, QueryResult> handler) => _handler = handler;
        public QueryResult Execute(string query) => _handler(query);
        public void ExecuteCommand(string command) => Commands.Add(command);
        public void Dispose() { }
    }

    private static MaintenanceService ForModel() =>
        new(new FakeSession(_ => QueryResult.Empty), "Retail Model");

    [Fact]
    public void Model_refresh_emits_full_tmsl_for_database()
    {
        var tmsl = ForModel().BuildModelRefresh(RefreshType.Full);
        Assert.Contains("\"refresh\"", tmsl);
        Assert.Contains("\"type\": \"full\"", tmsl);
        Assert.Contains("\"database\": \"Retail Model\"", tmsl);
    }

    [Fact]
    public void Table_refresh_includes_table_target()
    {
        var tmsl = ForModel().BuildTableRefresh("Sales", RefreshType.DataOnly);
        Assert.Contains("\"type\": \"dataOnly\"", tmsl);
        Assert.Contains("\"table\": \"Sales\"", tmsl);
    }

    // Ordered partition refresh = one single-partition command per partition, executed sequentially
    // by the caller (the engine ignores order within a single TMSL request, so a multi-partition
    // refresh can't control order). These assert the command list is correctly built and ordered.

    [Fact]
    public void OrderedPartitionCommands_newest_first_one_command_each()
    {
        var session = new FakeSession(q =>
            q.Contains("TMSCHEMA_TABLES", StringComparison.Ordinal)
                ? new QueryResult(["ID"], [[1L]])
                : new QueryResult(["Name"], [["2025Q1"], ["2026Q1"], ["2025Q3"]]));

        var cmds = new MaintenanceService(session, "DB")
            .BuildOrderedPartitionCommands("Sales", PartitionOrder.NewestFirst, RefreshType.Full);

        Assert.Equal(["2026Q1", "2025Q3", "2025Q1"], cmds.Select(c => c.Partition).ToArray());  // newest → oldest
        Assert.All(cmds, c =>
        {
            Assert.Contains($"\"partition\": \"{c.Partition}\"", c.Tmsl);   // each command is that one partition
            Assert.Single(System.Text.RegularExpressions.Regex.Matches(c.Tmsl, "\"refresh\""));
        });
    }

    [Fact]
    public void OrderedPartitionCommands_oldest_first()
    {
        var session = new FakeSession(q =>
            q.Contains("TMSCHEMA_TABLES", StringComparison.Ordinal)
                ? new QueryResult(["ID"], [[1L]])
                : new QueryResult(["Name"], [["2026Q1"], ["2025Q1"]]));

        var cmds = new MaintenanceService(session, "DB")
            .BuildOrderedPartitionCommands("Sales", PartitionOrder.OldestFirst, RefreshType.Full);

        Assert.Equal(["2025Q1", "2026Q1"], cmds.Select(c => c.Partition).ToArray());
    }

    [Fact]
    public void OrderedPartitionCommands_subset_preserves_given_order()
    {
        var cmds = new MaintenanceService(new FakeSession(_ => QueryResult.Empty), "DB")
            .BuildOrderedPartitionCommands("Sales", new[] { "2026Q3", "2026Q1", "2026Q2" }, RefreshType.Full);

        Assert.Equal(["2026Q3", "2026Q1", "2026Q2"], cmds.Select(c => c.Partition).ToArray());
    }

    [Fact]
    public void OrderedPartitionCommands_throws_when_table_absent()
    {
        var session = new FakeSession(_ => QueryResult.Empty);
        Assert.Throws<DaxterException>(() =>
            new MaintenanceService(session, "DB").BuildOrderedPartitionCommands("Nope", PartitionOrder.NewestFirst, RefreshType.Full));
    }

    [Fact]
    public void ClearCache_emits_xml_with_resolved_database_id()
    {
        var session = new FakeSession(q =>
            q.Contains("DBSCHEMA_CATALOGS", StringComparison.Ordinal)
                ? new QueryResult(["DATABASE_ID"], [["abc-123"]])
                : QueryResult.Empty);

        var xml = new MaintenanceService(session, "Retail Model").BuildClearCache();
        Assert.Contains("<ClearCache", xml);
        Assert.Contains("<DatabaseID>abc-123</DatabaseID>", xml);
    }

    [Theory]
    [InlineData(null, RefreshType.Full)]
    [InlineData("full", RefreshType.Full)]
    [InlineData("dataOnly", RefreshType.DataOnly)]
    [InlineData("clear-values", RefreshType.ClearValues)]
    [InlineData("calculate", RefreshType.Calculate)]
    public void ParseRefreshType_maps_values(string? input, RefreshType expected)
        => Assert.Equal(expected, MaintenanceService.ParseRefreshType(input));

    [Fact]
    public void ParseRefreshType_rejects_unknown()
        => Assert.Throws<DaxterException>(() => MaintenanceService.ParseRefreshType("bogus"));
}
