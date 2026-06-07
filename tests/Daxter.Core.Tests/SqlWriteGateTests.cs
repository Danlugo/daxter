using Daxter.Core.Sql;

namespace Daxter.Core.Tests;

public class SqlWriteGateTests
{
    // The Allow-writes gate is the only thing protecting a SELECT-only intent from a typo'd MERGE.
    // This suite locks in what counts as read-only — the same rule both the Web page and the MCP tool
    // use to decide whether to confirm-prompt vs run straight through.

    [Theory]
    [InlineData("SELECT 1")]
    [InlineData("select * from foo")]
    [InlineData("  SELECT TOP 10 col FROM dbo.t  ")]
    [InlineData("/* a leading block comment */ SELECT 1")]
    [InlineData("-- a leading line comment\nSELECT 1")]
    [InlineData("WITH q AS (SELECT 1 AS x) SELECT * FROM q")]
    [InlineData("WITH a AS (SELECT 1), b AS (SELECT 2) SELECT * FROM b")]
    [InlineData("EXPLAIN SELECT 1")]
    [InlineData("SET ANSI_NULLS ON; SELECT 1;")]
    public void IsReadOnly_returns_true_for_read_statements(string sql)
        => Assert.True(SqlWriteGate.IsReadOnly(sql), sql);

    [Theory]
    [InlineData("INSERT INTO foo VALUES (1)")]
    [InlineData("UPDATE foo SET x = 1")]
    [InlineData("DELETE FROM foo")]
    [InlineData("MERGE foo USING bar ON foo.id = bar.id WHEN MATCHED THEN UPDATE SET x = 1")]
    [InlineData("DROP TABLE foo")]
    [InlineData("ALTER TABLE foo ADD col INT")]
    [InlineData("CREATE TABLE foo (id INT)")]
    [InlineData("TRUNCATE TABLE foo")]
    [InlineData("EXEC dbo.some_proc 1, 2")]
    [InlineData("GRANT SELECT ON foo TO bar")]
    public void IsReadOnly_returns_false_for_writes(string sql)
        => Assert.False(SqlWriteGate.IsReadOnly(sql), sql);

    // The dangerous shape: a CTE that LOOKS read-only but feeds an INSERT/MERGE. The gate must catch it.
    [Theory]
    [InlineData("WITH staged AS (SELECT * FROM src) INSERT INTO dest SELECT * FROM staged")]
    [InlineData("WITH a AS (SELECT 1) DELETE FROM b WHERE id IN (SELECT * FROM a)")]
    public void IsReadOnly_returns_false_for_CTE_feeding_a_write(string sql)
        => Assert.False(SqlWriteGate.IsReadOnly(sql), sql);

    // A mixed batch must be classified as write if ANY statement is a write — running the SELECT half
    // and refusing the write half is worse than refusing the whole batch with a clear gate message.
    [Theory]
    [InlineData("SELECT 1; UPDATE foo SET x = 1;")]
    [InlineData("UPDATE foo SET x = 1; SELECT 1;")]
    public void IsReadOnly_returns_false_for_mixed_batches(string sql)
        => Assert.False(SqlWriteGate.IsReadOnly(sql), sql);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n\t  \n")]
    public void IsReadOnly_treats_blank_as_safe(string sql)
        => Assert.True(SqlWriteGate.IsReadOnly(sql), sql);
}
