using System.Text.Json;
using Daxter.Core.Connection;

namespace Daxter.Core.Maintenance;

/// <summary>How a refresh processes the target (maps to TMSL refresh "type").</summary>
public enum RefreshType { Full, Automatic, Calculate, DataOnly, ClearValues }

/// <summary>Ordering for partition-by-partition refresh.</summary>
public enum PartitionOrder { NewestFirst, OldestFirst }

/// <summary>
/// Builds (and optionally executes) maintenance commands against a model:
/// TMSL refresh of the model / a table / its partitions, and XMLA ClearCache.
/// Build and execute are separate so callers can offer <c>--dry-run</c>.
/// </summary>
public sealed class MaintenanceService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IXmlaSession _session;
    private readonly string _database;

    public MaintenanceService(IXmlaSession session, string database)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new DaxterException("A dataset is required for maintenance operations.");
        }

        _database = database;
    }

    /// <summary>TMSL to refresh the whole model.</summary>
    public string BuildModelRefresh(RefreshType type)
        => RefreshTmsl(type, [Target(_database)]);

    /// <summary>TMSL to refresh one table.</summary>
    public string BuildTableRefresh(string table, RefreshType type)
        => RefreshTmsl(type, [Target(_database, table)]);

    /// <summary>TMSL to refresh a single named partition of a table.</summary>
    public string BuildPartitionRefresh(string table, string partition, RefreshType type)
        => RefreshTmsl(type, [Target(_database, table, partition)]);

    /// <summary>
    /// TMSL to refresh every partition of a table, ordered (newest-first by default).
    /// Partition names are sorted lexically — names like <c>2026Q205</c> sort correctly.
    /// When <paramref name="maxParallelism"/> is set, the refresh is wrapped in a TMSL
    /// <c>Sequence</c> with that thread limit — pass <c>1</c> to process the partitions
    /// strictly in the listed order (a plain refresh runs them in parallel).
    /// </summary>
    public string BuildPartitionsRefresh(string table, PartitionOrder order, RefreshType type, int? maxParallelism = null)
    {
        var ordered = OrderedPartitionNames(table, order);
        var objects = ordered.Select(p => Target(_database, table, p)).ToList();
        return RefreshTmsl(type, objects, maxParallelism);
    }

    /// <summary>
    /// TMSL to refresh a specific set of partitions of a table, in the given order. With
    /// <paramref name="maxParallelism"/> = 1 they process strictly in that order.
    /// </summary>
    public string BuildPartitionsRefresh(string table, IReadOnlyList<string> partitions, RefreshType type, int? maxParallelism = null)
    {
        if (partitions is null || partitions.Count == 0)
        {
            throw new DaxterException("No partitions specified.");
        }

        var objects = partitions.Select(p => Target(_database, table, p)).ToList();
        return RefreshTmsl(type, objects, maxParallelism);
    }

    /// <summary>Partition names of a table in refresh order (newest- or oldest-first), Ordinal-sorted.</summary>
    public IReadOnlyList<string> OrderedPartitionNames(string table, PartitionOrder order)
    {
        var names = PartitionNames(table);
        if (names.Count == 0)
        {
            throw new DaxterException($"No partitions found for table: {table}");
        }

        return (order == PartitionOrder.NewestFirst
            ? names.OrderByDescending(n => n, StringComparer.Ordinal)
            : names.OrderBy(n => n, StringComparer.Ordinal)).ToList();
    }

    /// <summary>XMLA ClearCache command for this database.</summary>
    public string BuildClearCache()
    {
        var databaseId = DatabaseId();
        return "<ClearCache xmlns=\"http://schemas.microsoft.com/analysisservices/2003/engine\">" +
               $"<Object><DatabaseID>{System.Security.SecurityElement.Escape(databaseId)}</DatabaseID></Object>" +
               "</ClearCache>";
    }

    /// <summary>Executes a previously built command.</summary>
    public void Execute(string command) => _session.ExecuteCommand(command);

    private static string RefreshTmsl(RefreshType type, IReadOnlyList<Dictionary<string, string>> objects, int? maxParallelism = null)
    {
        var refresh = new Dictionary<string, object>
        {
            ["type"] = TypeName(type),
            ["objects"] = objects,
        };

        // A plain refresh processes objects in parallel. To honour the listed order, wrap it in a
        // Sequence with maxParallelism (1 = strictly sequential). See TMSL Sequence command.
        object payload = maxParallelism is { } mp
            ? new Dictionary<string, object>
            {
                ["sequence"] = new Dictionary<string, object>
                {
                    ["maxParallelism"] = mp,
                    ["operations"] = new[] { new Dictionary<string, object> { ["refresh"] = refresh } },
                },
            }
            : new Dictionary<string, object> { ["refresh"] = refresh };

        return JsonSerializer.Serialize(payload, Json);
    }

    private static Dictionary<string, string> Target(string database, string? table = null, string? partition = null)
    {
        var target = new Dictionary<string, string> { ["database"] = database };
        if (table is not null) target["table"] = table;
        if (partition is not null) target["partition"] = partition;
        return target;
    }

    private static string TypeName(RefreshType type) => type switch
    {
        RefreshType.Full => "full",
        RefreshType.Automatic => "automatic",
        RefreshType.Calculate => "calculate",
        RefreshType.DataOnly => "dataOnly",
        RefreshType.ClearValues => "clearValues",
        _ => "full",
    };

    private List<string> PartitionNames(string table)
    {
        var idResult = _session.Execute(
            $"SELECT [ID] FROM $SYSTEM.TMSCHEMA_TABLES WHERE [Name] = '{Escape(table)}'");
        if (idResult.RowCount == 0)
        {
            throw new DaxterException($"Table not found in model: {table}");
        }

        var tableId = Convert.ToInt64(idResult.Rows[0][0]);
        var partitions = _session.Execute(
            $"SELECT [Name] FROM $SYSTEM.TMSCHEMA_PARTITIONS WHERE [TableID] = {tableId}");

        return partitions.Rows
            .Select(r => r[0]?.ToString())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .ToList();
    }

    private string DatabaseId()
    {
        var result = _session.Execute(
            $"SELECT [DATABASE_ID] FROM $SYSTEM.DBSCHEMA_CATALOGS WHERE [CATALOG_NAME] = '{Escape(_database)}'");
        if (result.RowCount == 0 || result.Rows[0][0] is null)
        {
            throw new DaxterException($"Could not resolve database id for: {_database}");
        }

        return result.Rows[0][0]!.ToString()!;
    }

    public static RefreshType ParseRefreshType(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "full" => RefreshType.Full,
        "automatic" => RefreshType.Automatic,
        "calculate" => RefreshType.Calculate,
        "dataonly" or "data-only" => RefreshType.DataOnly,
        "clearvalues" or "clear-values" => RefreshType.ClearValues,
        _ => throw new DaxterException(
            $"Unknown refresh type '{value}'. Use full, automatic, calculate, dataOnly, or clearValues."),
    };

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
}
