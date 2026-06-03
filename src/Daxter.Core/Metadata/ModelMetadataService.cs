using Daxter.Core.Connection;
using Daxter.Core.Query;

namespace Daxter.Core.Metadata;

/// <summary>
/// Read-only model metadata via TMSCHEMA DMVs: measures, M code, parameters,
/// partitions (with last-refresh times), and RLS roles/filters/members. All
/// methods return a <see cref="QueryResult"/> for uniform formatting.
/// </summary>
public sealed class ModelMetadataService
{
    private readonly IXmlaSession _session;

    public ModelMetadataService(IXmlaSession session)
        => _session = session ?? throw new ArgumentNullException(nameof(session));

    /// <summary>Measures, optionally including the DAX expression.</summary>
    public QueryResult Measures(bool withExpression)
    {
        var columns = withExpression
            ? "[Name], [DataType], [DisplayFolder], [Expression]"
            : "[Name], [DataType], [DisplayFolder]";
        return _session.Execute(
            $"SELECT {columns} FROM $SYSTEM.TMSCHEMA_MEASURES ORDER BY [Name]");
    }

    /// <summary>A single measure's full definition.</summary>
    public QueryResult Measure(string name) => _session.Execute(
        "SELECT [Name], [DataType], [DisplayFolder], [FormatString], [Expression] " +
        $"FROM $SYSTEM.TMSCHEMA_MEASURES WHERE [Name] = '{Escape(name)}'");

    /// <summary>Shared M expressions — this is where Power Query parameters live.</summary>
    public QueryResult Parameters() => _session.Execute(
        "SELECT [Name], [Kind], [Expression] FROM $SYSTEM.TMSCHEMA_EXPRESSIONS ORDER BY [Name]");

    /// <summary>Partitions and their last-refresh state, optionally for one table.</summary>
    public QueryResult Partitions(string? table)
    {
        const string columns =
            "[Name], [State], [Mode], [RefreshedTime], [ModifiedTime]";
        if (string.IsNullOrWhiteSpace(table))
        {
            return _session.Execute(
                $"SELECT {columns} FROM $SYSTEM.TMSCHEMA_PARTITIONS ORDER BY [Name]");
        }

        var tableId = TableId(table);
        return _session.Execute(
            $"SELECT {columns} FROM $SYSTEM.TMSCHEMA_PARTITIONS " +
            $"WHERE [TableID] = {tableId} ORDER BY [Name]");
    }

    /// <summary>
    /// Lists columns (name, data type, hidden flag), optionally for one table. The internal
    /// RowNumber column is excluded. Without a table, the table id is included for grouping.
    /// </summary>
    public QueryResult Columns(string? table)
    {
        if (string.IsNullOrWhiteSpace(table))
        {
            return _session.Execute(
                "SELECT [TableID], [ExplicitName] AS [Column], [IsHidden] " +
                "FROM $SYSTEM.TMSCHEMA_COLUMNS WHERE [Type] <> 3 ORDER BY [TableID]");
        }

        var tableId = TableId(table);
        return _session.Execute(
            "SELECT [ExplicitName] AS [Column], [IsHidden] " +
            $"FROM $SYSTEM.TMSCHEMA_COLUMNS WHERE [TableID] = {tableId} AND [Type] <> 3 ORDER BY [ExplicitName]");
    }

    /// <summary>The M (Power Query) source for a table. For an incremental-refresh table the template
    /// lives on the refresh policy (not the auto-generated partitions), so surface that first — else
    /// the M view is blank for those tables.</summary>
    public QueryResult MCode(string table)
    {
        var tableId = TableId(table);
        var parts = _session.Execute(
            "SELECT [Name], [QueryDefinition] FROM $SYSTEM.TMSCHEMA_PARTITIONS " +
            $"WHERE [TableID] = {tableId} ORDER BY [Name]");

        var policy = _session.Execute(
            "SELECT [SourceExpression] FROM $SYSTEM.TMSCHEMA_REFRESH_POLICIES " +
            $"WHERE [TableID] = {tableId}");
        var policySource = policy.RowCount > 0 ? policy.Rows[0][0]?.ToString() : null;
        if (string.IsNullOrWhiteSpace(policySource))
        {
            return parts;
        }

        // Lead with the incremental-refresh policy source, then any partitions that carry their own M.
        var rows = new List<object?[]>();
        rows.Add(new object?[] { "(incremental refresh policy source)", policySource });
        foreach (var r in parts.Rows)
        {
            if (r.Length > 1 && r[1] is string q && !string.IsNullOrWhiteSpace(q))
            {
                rows.Add(new object?[] { r[0], r[1] });
            }
        }
        return new QueryResult(["Name", "QueryDefinition"], rows);
    }

    /// <summary>RLS roles with their model permission level.</summary>
    public QueryResult Roles() => _session.Execute(
        "SELECT [Name], [ModelPermission] FROM $SYSTEM.TMSCHEMA_ROLES ORDER BY [Name]");

    /// <summary>The table filters (DAX) defined for a role, with table names resolved.</summary>
    public QueryResult RoleFilters(string role)
    {
        var roleId = RoleId(role);
        var raw = _session.Execute(
            "SELECT [TableID], [FilterExpression] FROM $SYSTEM.TMSCHEMA_TABLE_PERMISSIONS " +
            $"WHERE [RoleID] = {roleId}");

        var tableNames = TableNameMap();
        var rows = new List<object?[]>(raw.RowCount);
        foreach (var row in raw.Rows)
        {
            var tableId = row[0] is null ? -1 : Convert.ToInt64(row[0]);
            var name = tableNames.TryGetValue(tableId, out var n) ? n : $"#{tableId}";
            rows.Add([name, row.Length > 1 ? row[1] : null]);
        }

        return new QueryResult(["Table", "FilterExpression"], rows);
    }

    /// <summary>The members assigned to a role.</summary>
    public QueryResult RoleMembers(string role)
    {
        var roleId = RoleId(role);
        return _session.Execute(
            "SELECT [MemberName], [MemberType], [IdentityProvider] " +
            $"FROM $SYSTEM.TMSCHEMA_ROLE_MEMBERSHIPS WHERE [RoleID] = {roleId}");
    }

    private long TableId(string table)
    {
        var result = _session.Execute(
            $"SELECT [ID] FROM $SYSTEM.TMSCHEMA_TABLES WHERE [Name] = '{Escape(table)}'");
        if (result.RowCount == 0)
        {
            throw new DaxterException($"Table not found in model: {table}");
        }

        return Convert.ToInt64(result.Rows[0][0]);
    }

    private long RoleId(string role)
    {
        var result = _session.Execute(
            $"SELECT [ID] FROM $SYSTEM.TMSCHEMA_ROLES WHERE [Name] = '{Escape(role)}'");
        if (result.RowCount == 0)
        {
            throw new DaxterException($"Role not found in model: {role}");
        }

        return Convert.ToInt64(result.Rows[0][0]);
    }

    /// <summary>Relationships with from/to <c>table[column]</c> names, active flag, and cross-filter resolved.</summary>
    public QueryResult Relationships()
    {
        var raw = _session.Execute(
            "SELECT [Name], [FromTableID], [FromColumnID], [ToTableID], [ToColumnID], [IsActive], [CrossFilteringBehavior] " +
            "FROM $SYSTEM.TMSCHEMA_RELATIONSHIPS");
        var tables = TableNameMap();
        var cols = ColumnNameMap();
        var rows = new List<object?[]>(raw.RowCount);
        foreach (var r in raw.Rows)
        {
            string T(int i) => r[i] is null ? "" : (tables.TryGetValue(Convert.ToInt64(r[i]), out var n) ? n : $"#{r[i]}");
            string C(int i) => r[i] is null ? "" : (cols.TryGetValue(Convert.ToInt64(r[i]), out var n) ? n : $"#{r[i]}");
            var name = r[0]?.ToString() ?? "";
            var active = r.Length > 5 && r[5] is not null && Convert.ToBoolean(r[5]);
            rows.Add([name, $"{T(1)}[{C(2)}]", $"{T(3)}[{C(4)}]", active ? "Yes" : "No", CrossFilterName(r.Length > 6 ? r[6] : null)]);
        }
        return new QueryResult(["Name", "From", "To", "Active", "CrossFilter"], rows);
    }

    private static string CrossFilterName(object? v) => v is null ? "" : Convert.ToInt32(v) switch
    {
        1 => "single",
        2 => "both",
        3 => "automatic",
        _ => v.ToString() ?? "",
    };

    private Dictionary<long, string> ColumnNameMap()
    {
        var result = _session.Execute("SELECT [ID], [ExplicitName] FROM $SYSTEM.TMSCHEMA_COLUMNS WHERE [Type] <> 3");
        var map = new Dictionary<long, string>(result.RowCount);
        foreach (var row in result.Rows)
        {
            if (row[0] is not null)
            {
                map[Convert.ToInt64(row[0])] = row.Length > 1 ? row[1]?.ToString() ?? string.Empty : string.Empty;
            }
        }
        return map;
    }

    private Dictionary<long, string> TableNameMap()
    {
        var result = _session.Execute("SELECT [ID], [Name] FROM $SYSTEM.TMSCHEMA_TABLES");
        var map = new Dictionary<long, string>(result.RowCount);
        foreach (var row in result.Rows)
        {
            if (row[0] is not null)
            {
                map[Convert.ToInt64(row[0])] = row.Length > 1 ? row[1]?.ToString() ?? string.Empty : string.Empty;
            }
        }

        return map;
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
