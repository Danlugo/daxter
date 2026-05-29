namespace Daxter.Core.Query;

/// <summary>An in-memory, materialized result set: ordered column names and rows.</summary>
public sealed class QueryResult
{
    public IReadOnlyList<string> Columns { get; }

    /// <summary>Each row has one cell per column; cells are <c>null</c> for DB nulls.</summary>
    public IReadOnlyList<object?[]> Rows { get; }

    public QueryResult(IReadOnlyList<string> columns, IReadOnlyList<object?[]> rows)
    {
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        Rows = rows ?? throw new ArgumentNullException(nameof(rows));
    }

    public int RowCount => Rows.Count;

    public int ColumnCount => Columns.Count;

    public static QueryResult Empty { get; } = new([], []);
}
