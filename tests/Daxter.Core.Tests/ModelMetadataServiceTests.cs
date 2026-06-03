using Daxter.Core;
using Daxter.Core.Connection;
using Daxter.Core.Metadata;
using Daxter.Core.Query;

namespace Daxter.Core.Tests;

public class ModelMetadataServiceTests
{
    // Records executed queries and returns canned results based on a matcher.
    private sealed class FakeXmlaSession : IXmlaSession
    {
        private readonly Func<string, QueryResult> _handler;
        public List<string> Queries { get; } = [];

        public FakeXmlaSession(Func<string, QueryResult> handler) => _handler = handler;

        public List<string> Commands { get; } = [];

        public QueryResult Execute(string query)
        {
            Queries.Add(query);
            return _handler(query);
        }

        public void ExecuteCommand(string command) => Commands.Add(command);

        public void Dispose() { }
    }

    private static QueryResult Empty => QueryResult.Empty;

    [Fact]
    public void Measures_without_expression_omits_expression_column()
    {
        var session = new FakeXmlaSession(_ => Empty);
        new ModelMetadataService(session).Measures(withExpression: false);

        Assert.Single(session.Queries);
        Assert.Contains("TMSCHEMA_MEASURES", session.Queries[0]);
        Assert.DoesNotContain("[Expression]", session.Queries[0]);
    }

    [Fact]
    public void Measures_with_expression_includes_expression_column()
    {
        var session = new FakeXmlaSession(_ => Empty);
        new ModelMetadataService(session).Measures(withExpression: true);
        Assert.Contains("[Expression]", session.Queries[0]);
    }

    [Fact]
    public void MCode_resolves_table_id_then_filters_partitions()
    {
        var session = new FakeXmlaSession(q =>
            q.Contains("TMSCHEMA_TABLES", StringComparison.Ordinal) && q.Contains("WHERE", StringComparison.Ordinal)
                ? new QueryResult(["ID"], [[42L]])
                : Empty);

        new ModelMetadataService(session).MCode("Fact Sales");

        // Resolve table id → partitions → refresh policy (the policy carries the M for incremental tables).
        Assert.Equal(3, session.Queries.Count);
        Assert.Contains("WHERE [Name] = 'Fact Sales'", session.Queries[0]);
        Assert.Contains("QueryDefinition", session.Queries[1]);
        Assert.Contains("[TableID] = 42", session.Queries[1]);
        Assert.Contains("TMSCHEMA_REFRESH_POLICIES", session.Queries[2]);
        Assert.Contains("[TableID] = 42", session.Queries[2]);
    }

    [Fact]
    public void MCode_surfaces_refresh_policy_source_for_incremental_tables()
    {
        // Partitions carry no M (policy-range); the M template lives on the refresh policy.
        var session = new FakeXmlaSession(q =>
        {
            if (q.Contains("TMSCHEMA_TABLES", StringComparison.Ordinal)) return new QueryResult(["ID"], [[42L]]);
            if (q.Contains("TMSCHEMA_REFRESH_POLICIES", StringComparison.Ordinal))
                return new QueryResult(["SourceExpression"], [["let Source = 1 in Source"]]);
            return Empty;   // partitions: none with their own M
        });

        var result = new ModelMetadataService(session).MCode("Fact Sales");

        Assert.Equal(1, result.RowCount);
        Assert.Contains("policy", result.Rows[0][0]?.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("let Source = 1 in Source", result.Rows[0][1]);
    }

    [Fact]
    public void MCode_escapes_single_quotes_in_table_name()
    {
        var session = new FakeXmlaSession(_ => new QueryResult(["ID"], [[1L]]));
        new ModelMetadataService(session).MCode("Tom's Sales");
        Assert.Contains("WHERE [Name] = 'Tom''s Sales'", session.Queries[0]);
    }

    [Fact]
    public void TableId_lookup_miss_throws()
    {
        var session = new FakeXmlaSession(_ => Empty); // no rows -> not found
        var ex = Assert.Throws<DaxterException>(() => new ModelMetadataService(session).MCode("Nope"));
        Assert.Contains("Nope", ex.Message);
    }

    [Fact]
    public void RoleFilters_resolves_table_names_from_ids()
    {
        var session = new FakeXmlaSession(q =>
        {
            if (q.Contains("TMSCHEMA_ROLES", StringComparison.Ordinal))
            {
                return new QueryResult(["ID"], [[7L]]);
            }

            if (q.Contains("TABLE_PERMISSIONS", StringComparison.Ordinal))
            {
                return new QueryResult(["TableID", "FilterExpression"], [[10L, "[Region] = \"US\""]]);
            }

            // TMSCHEMA_TABLES id->name map
            return new QueryResult(["ID", "Name"], [[10L, "DIM - Region"]]);
        });

        var result = new ModelMetadataService(session).RoleFilters("Manager");

        Assert.Equal(["Table", "FilterExpression"], result.Columns);
        Assert.Equal("DIM - Region", result.Rows[0][0]);
        Assert.Equal("[Region] = \"US\"", result.Rows[0][1]);
    }

    [Fact]
    public void Partitions_without_table_lists_all()
    {
        var session = new FakeXmlaSession(_ => Empty);
        new ModelMetadataService(session).Partitions(null);
        Assert.Single(session.Queries);
        Assert.DoesNotContain("WHERE", session.Queries[0]);
        Assert.Contains("RefreshedTime", session.Queries[0]);
    }
}
