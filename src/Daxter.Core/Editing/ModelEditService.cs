using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Tom = Microsoft.AnalysisServices.Tabular;

namespace Daxter.Core.Editing;

/// <summary>A role member (UPN / group / service principal) for an RLS/OLS role.</summary>
public sealed record RoleMember(string Name);

/// <summary>A table-level RLS filter: <paramref name="FilterDax"/> applied to <paramref name="Table"/>.</summary>
public sealed record TableFilter(string Table, string FilterDax);

/// <summary>A sourced (import) column: <paramref name="Name"/> of <paramref name="DataType"/> mapped to
/// the M output column <paramref name="Source"/>.</summary>
public sealed record SourceColumn(string Name, string DataType, string Source);

/// <summary>
/// Edits a Power BI model via the <b>Tabular Object Model (TOM)</b>: connects, mutates the in-memory
/// model, and applies with <c>SaveChanges()</c> — the engine-sanctioned write path (hand-built
/// surgical TMSL like <c>createOrReplace.measure</c> is rejected by the service). Each operation
/// modifies the model and returns a human-readable <b>change description</b>; the caller decides
/// dry-run (discard) vs apply (<see cref="Apply"/>).
///
/// SAFETY: editing a Power BI Desktop–authored model over XMLA is <b>irreversible for PBIX
/// download</b>, and needs the workspace XMLA endpoint set to <b>Read/Write</b>. The caller MUST
/// enforce the model-edit gate and take a .bim backup before <see cref="Apply"/>.
/// </summary>
public sealed class ModelEditService : IDisposable
{
    private readonly Tom.Server _server;
    private readonly Tom.Model _model;
    private string? _rawTmsl;

    public ModelEditService(DaxterConfig config, XmlaAccessToken token)
    {
        if (string.IsNullOrWhiteSpace(config.Dataset))
        {
            throw new DaxterException("A dataset is required for model edits.");
        }

        _server = new Tom.Server
        {
            AccessToken = new Microsoft.AnalysisServices.AccessToken(token.Token, token.ExpiresOn, null),
        };
        try
        {
            _server.Connect(XmlaConnectionString.Build(config.Workspace, null));
        }
        catch (Exception ex)
        {
            _server.Dispose();
            throw new DaxterException($"Could not connect for model edit: {ex.Message}", ex);
        }

        var db = _server.Databases.FindByName(config.Dataset)
                 ?? throw Cleanup(new DaxterException($"Dataset not found in workspace: {config.Dataset}"));
        _model = db.Model;
    }

    // ---- measures ----

    public string UpsertMeasure(string table, string name, string dax,
        string? formatString = null, string? displayFolder = null, string? description = null)
    {
        Require(dax, "dax");
        var t = Table(table);
        var m = t.Measures.Find(name);
        var verb = m is null ? "create" : "alter";
        if (m is null) { m = new Tom.Measure { Name = name }; t.Measures.Add(m); }
        m.Expression = dax;
        if (formatString is not null) m.FormatString = formatString;
        if (displayFolder is not null) m.DisplayFolder = displayFolder;
        if (description is not null) m.Description = description;
        return $"{verb} measure [{name}] on table [{table}]  =  {dax}";
    }

    public string DeleteMeasure(string table, string name)
    {
        var t = Table(table);
        var m = t.Measures.Find(name) ?? throw new DaxterException($"Measure not found: [{name}] on [{table}].");
        t.Measures.Remove(m);
        return $"delete measure [{name}] from table [{table}]";
    }

    // ---- parameters / shared M expressions ----

    public string UpsertExpression(string name, string mExpression, string? description = null)
    {
        Require(name, "name");
        Require(mExpression, "m");
        var e = _model.Expressions.Find(name);
        var verb = e is null ? "create" : "alter";
        if (e is null) { e = new Tom.NamedExpression { Name = name }; _model.Expressions.Add(e); }
        e.Kind = Tom.ExpressionKind.M;
        e.Expression = mExpression;
        if (description is not null) e.Description = description;
        return $"{verb} parameter/expression [{name}] (M)";
    }

    public string DeleteExpression(string name)
    {
        var e = _model.Expressions.Find(name) ?? throw new DaxterException($"Parameter/expression not found: [{name}].");
        _model.Expressions.Remove(e);
        return $"delete parameter/expression [{name}]";
    }

    // ---- RLS / OLS roles ----

    public string UpsertRole(string name, string? modelPermission = null,
        IEnumerable<RoleMember>? members = null, IEnumerable<TableFilter>? tableFilters = null)
    {
        Require(name, "name");
        var r = _model.Roles.Find(name);
        var verb = r is null ? "create" : "alter";
        if (r is null) { r = new Tom.ModelRole { Name = name }; _model.Roles.Add(r); }
        r.ModelPermission = ParsePermission(modelPermission);

        var mem = (members ?? []).Where(x => !string.IsNullOrWhiteSpace(x.Name)).ToList();
        if (mem.Count > 0)
        {
            r.Members.Clear();
            foreach (var x in mem)
            {
                r.Members.Add(new Tom.ExternalModelRoleMember { MemberName = x.Name, IdentityProvider = "AzureAD" });
            }
        }

        foreach (var f in (tableFilters ?? []).Where(f => !string.IsNullOrWhiteSpace(f.Table)))
        {
            var tp = r.TablePermissions.Find(f.Table);
            if (tp is null) { tp = new Tom.TablePermission { Table = Table(f.Table) }; r.TablePermissions.Add(tp); }
            tp.FilterExpression = f.FilterDax ?? "";
        }

        return $"{verb} role [{name}] (permission={r.ModelPermission}, members={mem.Count})";
    }

    public string DeleteRole(string name)
    {
        var r = _model.Roles.Find(name) ?? throw new DaxterException($"Role not found: [{name}].");
        _model.Roles.Remove(r);
        return $"delete role [{name}]";
    }

    // ---- calculated columns ----

    public string UpsertCalculatedColumn(string table, string name, string dax, string? dataType = null)
    {
        Require(dax, "dax");
        var t = Table(table);
        var existing = t.Columns.Find(name);
        var verb = existing is null ? "create" : "alter";
        if (existing is not null && existing is not Tom.CalculatedColumn)
        {
            throw new DaxterException($"Column [{name}] on [{table}] exists and is not a calculated column.");
        }

        var c = existing as Tom.CalculatedColumn;
        if (c is null) { c = new Tom.CalculatedColumn { Name = name }; t.Columns.Add(c); }
        c.Expression = dax;
        if (!string.IsNullOrWhiteSpace(dataType)) c.DataType = ParseDataType(dataType);
        return $"{verb} calculated column [{name}] on table [{table}]  =  {dax}";
    }

    public string DeleteColumn(string table, string name)
    {
        var t = Table(table);
        var c = t.Columns.Find(name) ?? throw new DaxterException($"Column not found: [{name}] on [{table}].");
        t.Columns.Remove(c);
        return $"delete column [{name}] from table [{table}]";
    }

    // ---- partition (M) source ----

    public string SetPartitionSource(string table, string partition, string mExpression)
    {
        Require(mExpression, "m");
        var t = Table(table);
        var p = t.Partitions.Find(partition) ?? throw new DaxterException($"Partition not found: [{partition}] on [{table}].");
        p.Source = new Tom.MPartitionSource { Expression = mExpression };
        return $"set M source on partition [{partition}] of table [{table}]";
    }

    // ---- tables ----

    public string CreateCalculatedTable(string name, string dax)
    {
        Require(name, "name");
        Require(dax, "dax");
        var existing = _model.Tables.Find(name);
        var verb = existing is null ? "create" : "replace";
        var t = existing ?? new Tom.Table { Name = name };
        if (existing is null) _model.Tables.Add(t);
        t.Partitions.Clear();
        t.Partitions.Add(new Tom.Partition
        {
            Name = name,
            Source = new Tom.CalculatedPartitionSource { Expression = dax },
        });
        return $"{verb} calculated table [{name}]  =  {dax}";
    }

    public string DeleteTable(string name)
    {
        var t = _model.Tables.Find(name) ?? throw new DaxterException($"Table not found: [{name}].");
        _model.Tables.Remove(t);
        return $"delete table [{name}]";
    }

    /// <summary>Create or replace an <b>import table</b>: an M (Power Query) partition plus typed,
    /// sourced columns. The columns map to the M query's output columns by name.</summary>
    public string CreateImportTable(string name, string mExpression, IEnumerable<SourceColumn> columns)
    {
        Require(name, "name");
        Require(mExpression, "m");
        var cols = (columns ?? []).Where(c => !string.IsNullOrWhiteSpace(c.Name)).ToList();
        if (cols.Count == 0)
        {
            throw new DaxterException("At least one column (name:dataType:sourceColumn) is required for an import table.");
        }

        var existing = _model.Tables.Find(name);
        var verb = existing is null ? "create" : "replace";
        var t = existing ?? new Tom.Table { Name = name };
        if (existing is null) _model.Tables.Add(t);

        t.Partitions.Clear();
        t.Partitions.Add(new Tom.Partition
        {
            Name = name,
            Source = new Tom.MPartitionSource { Expression = mExpression },
        });

        foreach (var c in cols)
        {
            if (t.Columns.Find(c.Name) is null)
            {
                t.Columns.Add(new Tom.DataColumn
                {
                    Name = c.Name,
                    DataType = ParseDataType(c.DataType),
                    SourceColumn = c.Source,
                });
            }
        }

        return $"{verb} import table [{name}] (M source, {cols.Count} column(s))";
    }

    // ---- relationships ----

    /// <summary>Create or alter a single-column relationship fromTable[fromColumn] → toTable[toColumn]
    /// (many-to-one by default). <paramref name="name"/> is optional (auto-derived if omitted).</summary>
    public string UpsertRelationship(string fromTable, string fromColumn, string toTable, string toColumn,
        string? name = null, string? crossFilter = null, bool isActive = true)
    {
        var fc = Table(fromTable).Columns.Find(fromColumn)
                 ?? throw new DaxterException($"Column not found: [{fromColumn}] on [{fromTable}].");
        var tc = Table(toTable).Columns.Find(toColumn)
                 ?? throw new DaxterException($"Column not found: [{toColumn}] on [{toTable}].");

        var relName = string.IsNullOrWhiteSpace(name)
            ? $"{fromTable}_{fromColumn}_{toTable}_{toColumn}"
            : name;
        var existing = _model.Relationships.Find(relName) as Tom.SingleColumnRelationship;
        var verb = existing is null ? "create" : "alter";
        var r = existing ?? new Tom.SingleColumnRelationship { Name = relName };
        if (existing is null) _model.Relationships.Add(r);

        r.FromColumn = fc;
        r.ToColumn = tc;
        r.CrossFilteringBehavior = ParseCrossFilter(crossFilter);
        r.IsActive = isActive;
        return $"{verb} relationship [{fromTable}].[{fromColumn}] -> [{toTable}].[{toColumn}] " +
               $"(name={relName}, crossFilter={r.CrossFilteringBehavior}, active={isActive})";
    }

    /// <summary>Delete a relationship by name.</summary>
    public string DeleteRelationship(string name)
    {
        Require(name, "name");
        var r = _model.Relationships.Find(name) ?? throw new DaxterException($"Relationship not found: [{name}].");
        _model.Relationships.Remove(r);
        return $"delete relationship [{name}]";
    }

    // ---- raw TMSL escape hatch ----

    public string Raw(string tmsl)
    {
        Require(tmsl, "tmsl");
        _rawTmsl = tmsl;
        return "raw TMSL:\n" + tmsl;
    }

    // ---- apply ----

    /// <summary>Applies the staged change — <c>SaveChanges()</c> for typed edits, or <c>Execute</c> for raw TMSL.</summary>
    public void Apply()
    {
        if (_rawTmsl is not null)
        {
            var results = _server.Execute(_rawTmsl);
            foreach (Microsoft.AnalysisServices.XmlaResult r in results)
            {
                foreach (Microsoft.AnalysisServices.XmlaMessage msg in r.Messages)
                {
                    if (msg is Microsoft.AnalysisServices.XmlaError err)
                    {
                        throw new DaxterException($"TMSL failed: {err.Description}");
                    }
                }
            }

            return;
        }

        _model.SaveChanges();
    }

    public void Dispose() => _server.Dispose();

    // ---- helpers ----

    private Tom.Table Table(string name)
    {
        Require(name, "table");
        return _model.Tables.Find(name) ?? throw new DaxterException($"Table not found in model: [{name}].");
    }

    private static Tom.ModelPermission ParsePermission(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "read" => Tom.ModelPermission.Read,
        "readrefresh" => Tom.ModelPermission.ReadRefresh,
        "refresh" => Tom.ModelPermission.Refresh,
        "administrator" or "admin" => Tom.ModelPermission.Administrator,
        "none" => Tom.ModelPermission.None,
        _ => throw new DaxterException($"Unknown model permission '{value}'. Use read|readRefresh|refresh|administrator|none."),
    };

    private static Tom.CrossFilteringBehavior ParseCrossFilter(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "single" or "onedirection" or "one" => Tom.CrossFilteringBehavior.OneDirection,
        "both" or "bothdirections" => Tom.CrossFilteringBehavior.BothDirections,
        "automatic" or "auto" => Tom.CrossFilteringBehavior.Automatic,
        _ => throw new DaxterException($"Unknown cross-filter '{value}'. Use single|both|automatic."),
    };

    private static Tom.DataType ParseDataType(string value) => value.Trim().ToLowerInvariant() switch
    {
        "string" or "text" => Tom.DataType.String,
        "int64" or "int" or "integer" or "whole" => Tom.DataType.Int64,
        "double" or "float" => Tom.DataType.Double,
        "decimal" => Tom.DataType.Decimal,
        "datetime" or "date" => Tom.DataType.DateTime,
        "boolean" or "bool" => Tom.DataType.Boolean,
        _ => throw new DaxterException($"Unknown data type '{value}'. Use string|int64|double|decimal|dateTime|boolean."),
    };

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DaxterException($"{field} is required.");
        }
    }

    private DaxterException Cleanup(DaxterException ex)
    {
        _server.Dispose();
        return ex;
    }
}
