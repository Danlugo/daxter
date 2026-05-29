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

    [Fact]
    public void Partitions_refresh_orders_newest_first()
    {
        var session = new FakeSession(q =>
            q.Contains("TMSCHEMA_TABLES", StringComparison.Ordinal)
                ? new QueryResult(["ID"], [[1L]])
                : new QueryResult(["Name"], [["2025Q1"], ["2026Q1"], ["2025Q3"]]));

        var tmsl = new MaintenanceService(session, "DB")
            .BuildPartitionsRefresh("Sales", PartitionOrder.NewestFirst, RefreshType.Full);

        // Newest (2026Q1) should appear before the oldest (2025Q1).
        Assert.True(tmsl.IndexOf("2026Q1", StringComparison.Ordinal)
                    < tmsl.IndexOf("2025Q1", StringComparison.Ordinal));
    }

    [Fact]
    public void Partitions_refresh_orders_oldest_first()
    {
        var session = new FakeSession(q =>
            q.Contains("TMSCHEMA_TABLES", StringComparison.Ordinal)
                ? new QueryResult(["ID"], [[1L]])
                : new QueryResult(["Name"], [["2026Q1"], ["2025Q1"]]));

        var tmsl = new MaintenanceService(session, "DB")
            .BuildPartitionsRefresh("Sales", PartitionOrder.OldestFirst, RefreshType.Full);

        Assert.True(tmsl.IndexOf("2025Q1", StringComparison.Ordinal)
                    < tmsl.IndexOf("2026Q1", StringComparison.Ordinal));
    }

    [Fact]
    public void Partitions_refresh_throws_when_table_absent()
    {
        var session = new FakeSession(_ => QueryResult.Empty);
        Assert.Throws<DaxterException>(() =>
            new MaintenanceService(session, "DB").BuildPartitionsRefresh("Nope", PartitionOrder.NewestFirst, RefreshType.Full));
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
