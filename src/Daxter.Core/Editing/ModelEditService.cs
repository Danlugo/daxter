using System.Text.Json;
using Daxter.Core.Connection;

namespace Daxter.Core.Editing;

/// <summary>A role member (UPN, group, or service principal) for an RLS/OLS role.</summary>
public sealed record RoleMember(string Name);

/// <summary>A table-level RLS filter: <paramref name="FilterDax"/> applied to <paramref name="Table"/>.</summary>
public sealed record TableFilter(string Table, string FilterDax);

/// <summary>
/// Builds (and optionally executes) TMSL model-edit commands over an open XMLA session — measures,
/// parameters/shared expressions, RLS/OLS roles, calculated columns, partition (M) sources, and
/// calculated tables, plus delete of each, plus a raw-TMSL escape hatch. Build and execute are
/// separate so a caller can <b>preview the exact TMSL as a dry-run</b> before applying — mirroring
/// <see cref="Maintenance.MaintenanceService"/>. The previewed TMSL <i>is</i> what executes.
///
/// SAFETY: editing a Power BI Desktop–authored model over XMLA is <b>irreversible for PBIX
/// download</b>. Callers MUST enforce the model-edit write gate and take a .bim backup <i>before</i>
/// calling <see cref="Execute"/>. <c>createOrReplace</c> requires the full definition of the target
/// object — surgical object paths (measure/column/partition/role/expression) avoid clobbering siblings.
/// </summary>
public sealed class ModelEditService
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly IXmlaSession _session;
    private readonly string _database;

    public ModelEditService(IXmlaSession session, string database)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        if (string.IsNullOrWhiteSpace(database))
        {
            throw new DaxterException("A dataset is required for model edits.");
        }

        _database = database;
    }

    // ---- measures ----

    /// <summary>TMSL to create-or-replace a measure on a table (upsert).</summary>
    public string BuildMeasureUpsert(
        string table, string name, string dax,
        string? formatString = null, string? displayFolder = null, string? description = null)
    {
        Require(table, "table");
        Require(name, "name");
        if (string.IsNullOrWhiteSpace(dax))
        {
            throw new DaxterException("A DAX expression is required to create or alter a measure.");
        }

        var measure = new Dictionary<string, object> { ["name"] = name, ["expression"] = dax };
        Put(measure, "formatString", formatString);
        Put(measure, "displayFolder", displayFolder);
        Put(measure, "description", description);
        return CreateOrReplace(Path(("table", table), ("measure", name)), "measure", measure);
    }

    /// <summary>TMSL to delete a measure from a table.</summary>
    public string BuildMeasureDelete(string table, string name)
        => Delete(Path(("table", RequireV(table, "table")), ("measure", RequireV(name, "name"))));

    // ---- parameters / shared M expressions ----

    /// <summary>TMSL to create-or-replace a shared M expression / parameter (model-level).</summary>
    public string BuildExpressionUpsert(string name, string mExpression, string? description = null)
    {
        Require(name, "name");
        if (string.IsNullOrWhiteSpace(mExpression))
        {
            throw new DaxterException("An M expression is required to create or alter a parameter/expression.");
        }

        var expr = new Dictionary<string, object> { ["name"] = name, ["kind"] = "m", ["expression"] = mExpression };
        Put(expr, "description", description);
        return CreateOrReplace(Path(("expression", name)), "expression", expr);
    }

    /// <summary>TMSL to delete a shared M expression / parameter.</summary>
    public string BuildExpressionDelete(string name)
        => Delete(Path(("expression", RequireV(name, "name"))));

    // ---- RLS / OLS roles ----

    /// <summary>TMSL to create-or-replace a security role with members and table filters.</summary>
    public string BuildRoleUpsert(
        string name, string? modelPermission = null,
        IEnumerable<RoleMember>? members = null, IEnumerable<TableFilter>? tableFilters = null)
    {
        Require(name, "name");
        var role = new Dictionary<string, object>
        {
            ["name"] = name,
            ["modelPermission"] = string.IsNullOrWhiteSpace(modelPermission) ? "read" : modelPermission,
        };

        var mem = members?.Where(m => !string.IsNullOrWhiteSpace(m.Name))
            .Select(m => new Dictionary<string, object> { ["memberName"] = m.Name }).ToList();
        if (mem is { Count: > 0 }) role["members"] = mem;

        var tp = tableFilters?.Where(f => !string.IsNullOrWhiteSpace(f.Table))
            .Select(f => new Dictionary<string, object> { ["name"] = f.Table, ["filterExpression"] = f.FilterDax ?? "" }).ToList();
        if (tp is { Count: > 0 }) role["tablePermissions"] = tp;

        return CreateOrReplace(Path(("role", name)), "role", role);
    }

    /// <summary>TMSL to delete a security role.</summary>
    public string BuildRoleDelete(string name)
        => Delete(Path(("role", RequireV(name, "name"))));

    // ---- calculated columns ----

    /// <summary>TMSL to create-or-replace a calculated column on a table.</summary>
    public string BuildCalculatedColumnUpsert(string table, string name, string dax, string? dataType = null)
    {
        Require(table, "table");
        Require(name, "name");
        if (string.IsNullOrWhiteSpace(dax))
        {
            throw new DaxterException("A DAX expression is required for a calculated column.");
        }

        var column = new Dictionary<string, object> { ["name"] = name, ["type"] = "calculated", ["expression"] = dax };
        Put(column, "dataType", dataType);
        return CreateOrReplace(Path(("table", table), ("column", name)), "column", column);
    }

    /// <summary>TMSL to delete a column from a table.</summary>
    public string BuildColumnDelete(string table, string name)
        => Delete(Path(("table", RequireV(table, "table")), ("column", RequireV(name, "name"))));

    // ---- partition (M) source ----

    /// <summary>TMSL to set a partition's Power Query (M) source (create-or-replace the partition).</summary>
    public string BuildPartitionSourceSet(string table, string partition, string mExpression)
    {
        Require(table, "table");
        Require(partition, "partition");
        if (string.IsNullOrWhiteSpace(mExpression))
        {
            throw new DaxterException("An M expression is required to set a partition source.");
        }

        var part = new Dictionary<string, object>
        {
            ["name"] = partition,
            ["source"] = new Dictionary<string, object> { ["type"] = "m", ["expression"] = mExpression },
        };
        return CreateOrReplace(Path(("table", table), ("partition", partition)), "partition", part);
    }

    // ---- tables ----

    /// <summary>TMSL to create-or-replace a calculated table (a table whose single partition is a DAX expression).</summary>
    public string BuildCalculatedTableCreate(string name, string dax)
    {
        Require(name, "name");
        if (string.IsNullOrWhiteSpace(dax))
        {
            throw new DaxterException("A DAX expression is required for a calculated table.");
        }

        var table = new Dictionary<string, object>
        {
            ["name"] = name,
            ["partitions"] = new List<object>
            {
                new Dictionary<string, object>
                {
                    ["name"] = name,
                    ["source"] = new Dictionary<string, object> { ["type"] = "calculated", ["expression"] = dax },
                },
            },
        };
        return CreateOrReplace(Path(("table", name)), "table", table);
    }

    /// <summary>TMSL to delete an entire table (and its children).</summary>
    public string BuildTableDelete(string name)
        => Delete(Path(("table", RequireV(name, "name"))));

    // ---- execution / validation ----

    /// <summary>Fail fast with a clear message if the target table isn't in the model.</summary>
    public void EnsureTableExists(string table)
    {
        Require(table, "table");
        var result = _session.Execute("SELECT [Name] FROM $SYSTEM.TMSCHEMA_TABLES");
        var exists = result.Rows.Any(r =>
            string.Equals(r.ElementAtOrDefault(0)?.ToString(), table, StringComparison.OrdinalIgnoreCase));
        if (!exists)
        {
            throw new DaxterException($"Table not found in model: '{table}'.");
        }
    }

    /// <summary>Executes a TMSL command (the mutation). The caller MUST gate and back up first.</summary>
    public void Execute(string command) => _session.ExecuteCommand(command);

    // ---- TMSL helpers ----

    /// <summary>Builds an object path rooted at the current database: <c>{ database, k1:v1, k2:v2, … }</c>.</summary>
    private Dictionary<string, string> Path(params (string Key, string Value)[] parts)
    {
        var path = new Dictionary<string, string> { ["database"] = _database };
        foreach (var (k, v) in parts) path[k] = v;
        return path;
    }

    private static string CreateOrReplace(Dictionary<string, string> path, string bodyKey, Dictionary<string, object> body)
        => Serialize(new Dictionary<string, object>
        {
            ["createOrReplace"] = new Dictionary<string, object> { ["object"] = path, [bodyKey] = body },
        });

    private static string Delete(Dictionary<string, string> path)
        => Serialize(new Dictionary<string, object>
        {
            ["delete"] = new Dictionary<string, object> { ["object"] = path },
        });

    private static string Serialize(object payload) => JsonSerializer.Serialize(payload, Json);

    private static void Put(Dictionary<string, object> target, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value)) target[key] = value;
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DaxterException($"{field} is required.");
        }
    }

    private static string RequireV(string value, string field)
    {
        Require(value, field);
        return value;
    }
}
