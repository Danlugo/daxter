using Daxter.Core.Auth;
using Daxter.Core.Query;
using Microsoft.Data.SqlClient;

namespace Daxter.Core.Sql;

/// <summary>One SQL endpoint discovered on a Fabric workspace — either a Warehouse or a Lakehouse's
/// SQL analytics endpoint. <paramref name="Server"/> is the fully-qualified hostname (no port),
/// <paramref name="Database"/> the catalog name the TDS connection targets, and <paramref name="Kind"/>
/// is "Warehouse" or "Lakehouse" for the picker label.</summary>
public sealed record FabricSqlEndpoint(string Name, string Server, string Database, string Kind);

/// <summary>Thin client over Fabric Warehouse / Lakehouse SQL endpoints. Speaks TDS (not XMLA),
/// authenticating with an AAD bearer token acquired silently for the
/// <c>https://database.windows.net/.default</c> scope — same MSAL account as the rest of DAXter, so
/// the user signs in once and queries flow. Returns the same <see cref="QueryResult"/> shape as the
/// DAX and REST surfaces so the Web / CLI / MCP layer renders it uniformly.</summary>
public sealed class FabricSqlClient
{
    private readonly IFabricSqlTokenProvider _tokens;

    /// <summary>Connection timeout in seconds (TCP + auth). Short enough to fail fast on a wrong
    /// server, long enough to survive a brief network blip.</summary>
    public int ConnectTimeoutSeconds { get; init; } = 30;

    /// <summary>Query timeout in seconds (server-side execution). Default 5 minutes; oversize warehouse
    /// scans push this — but the UI cancels via the <c>CancellationToken</c> anyway.</summary>
    public int CommandTimeoutSeconds { get; init; } = 300;

    public FabricSqlClient(IFabricSqlTokenProvider tokens)
    {
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
    }

    /// <summary>Executes <paramref name="sql"/> on <paramref name="server"/> / <paramref name="database"/>
    /// and materializes the first result set as a <see cref="QueryResult"/>. The connection is opened with
    /// AAD bearer auth (<c>SqlConnection.AccessToken</c>) so no SQL login is configured anywhere.
    /// <paramref name="allowWrite"/> must be <c>true</c> to send anything other than read-only T-SQL —
    /// the page/CLI/MCP layer wires it to the DAXter <c>Allow writes</c> gate so a stray <c>UPDATE</c>
    /// can't land. Multi-result-set batches return the first set (subsequent sets are ignored).</summary>
    public async Task<QueryResult> ExecuteAsync(
        string server, string database, string sql, bool allowWrite, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(server)) throw new ArgumentException("server is required", nameof(server));
        if (string.IsNullOrWhiteSpace(database)) throw new ArgumentException("database is required", nameof(database));
        if (string.IsNullOrWhiteSpace(sql)) throw new ArgumentException("sql is required", nameof(sql));

        if (!allowWrite && !SqlWriteGate.IsReadOnly(sql))
        {
            throw new DaxterException(
                "This statement looks like a write (INSERT/UPDATE/DELETE/MERGE/DDL/EXEC/…). " +
                "Enable the DAXter 'Allow writes' gate to run it, or rewrite as a SELECT.");
        }

        var token = await _tokens.GetFabricSqlTokenAsync(ct);

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = server,                        // e.g. <workspace>.datawarehouse.fabric.microsoft.com
            InitialCatalog = database,                  // warehouse name / lakehouse SQL endpoint database
            Encrypt = true,
            TrustServerCertificate = false,
            ConnectTimeout = ConnectTimeoutSeconds,
            ApplicationName = "DAXter",
        };

        await using var conn = new SqlConnection(builder.ConnectionString) { AccessToken = token.Token };
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.CommandTimeout = CommandTimeoutSeconds;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await MaterializeAsync(reader, ct);
    }

    /// <summary>The objects in a Fabric SQL endpoint, flattened into one (schema, kind, name) table the
    /// Web page renders as a left-side tree (schema → Tables/Views/Functions/Stored Procedures → name).
    /// One round-trip — UNION ALL over INFORMATION_SCHEMA — ordered server-side so the page can group
    /// without re-sorting. Always read-only (the gate doesn't trigger).</summary>
    public Task<QueryResult> ListObjectsAsync(string server, string database, CancellationToken ct = default)
        => ExecuteAsync(server, database, ListObjectsSql, allowWrite: false, ct);

    /// <summary>Discovery query for <see cref="ListObjectsAsync"/>. Exposed so the CLI / MCP can run the
    /// exact same SQL.</summary>
    public const string ListObjectsSql = """
        SELECT TABLE_SCHEMA AS [schema], 'Table' AS [kind], TABLE_NAME AS [name]
          FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_TYPE = 'BASE TABLE'
        UNION ALL
        SELECT TABLE_SCHEMA, 'View', TABLE_NAME
          FROM INFORMATION_SCHEMA.VIEWS
        UNION ALL
        SELECT ROUTINE_SCHEMA, 'Function', ROUTINE_NAME
          FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_TYPE = 'FUNCTION'
        UNION ALL
        SELECT ROUTINE_SCHEMA, 'Stored Procedure', ROUTINE_NAME
          FROM INFORMATION_SCHEMA.ROUTINES WHERE ROUTINE_TYPE = 'PROCEDURE'
        ORDER BY [schema], [kind], [name]
        """;

    private static async Task<QueryResult> MaterializeAsync(SqlDataReader reader, CancellationToken ct)
    {
        if (reader.FieldCount == 0)
        {
            // Non-result-producing batch (e.g. SET NOCOUNT ON; followed by nothing) — surface a 0x0 grid
            // rather than throwing. The UI shows "0 rows".
            return QueryResult.Empty;
        }

        var columns = new string[reader.FieldCount];
        for (var i = 0; i < reader.FieldCount; i++) columns[i] = reader.GetName(i);

        var rows = new List<object?[]>();
        while (await reader.ReadAsync(ct))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return new QueryResult(columns, rows);
    }
}
