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

    /// <summary>The M (Power Query) source for each partition of a table.</summary>
    public QueryResult MCode(string table)
    {
        var tableId = TableId(table);
        return _session.Execute(
            "SELECT [Name], [QueryDefinition] FROM $SYSTEM.TMSCHEMA_PARTITIONS " +
            $"WHERE [TableID] = {tableId} ORDER BY [Name]");
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
