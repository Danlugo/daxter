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

/// <summary>An existing column's editable presentation/metadata properties (for the editor to pre-fill).</summary>
public sealed record ColumnInfo(
    string Name, string Kind, string DataType, string FormatString, string SortByColumn,
    string SummarizeBy, string DisplayFolder, string Description, bool IsHidden, string DataCategory);

/// <summary>An incremental-refresh policy's editable settings (the M template lives in <paramref name="Source"/>).</summary>
public sealed record RefreshPolicyInfo(
    string Source, string Polling,
    string RollingGranularity, int RollingPeriods,
    string IncrementalGranularity, int IncrementalPeriods, int IncrementalOffset);

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

    /// <summary>Edits an <b>existing</b> column's presentation/metadata properties — format string,
    /// data type, sort-by column, summarize-by, display folder, description, hidden, data category —
    /// on <i>any</i> column (data or calculated), without needing a DAX expression. This is the path
    /// Tabular Editor / Desktop use: hand-built <c>createOrReplace.column</c> TMSL is rejected by the
    /// engine ("Unrecognized JSON property: column"), but TOM + SaveChanges edits a column in place.
    /// Only the properties you pass are changed (null = leave as-is; "" clears string properties).</summary>
    public string EditColumn(string table, string name,
        string? formatString = null, string? dataType = null, string? sortByColumn = null,
        string? summarizeBy = null, string? displayFolder = null, string? description = null,
        bool? isHidden = null, string? dataCategory = null)
    {
        var t = Table(table);
        var c = t.Columns.Find(name) ?? throw new DaxterException($"Column not found: [{name}] on [{table}].");
        if (c.Type == Tom.ColumnType.RowNumber)
            throw new DaxterException($"Column [{name}] on [{table}] is a system row-number column and can't be edited.");

        var changes = new List<string>();
        if (formatString is not null) { c.FormatString = formatString; changes.Add($"format='{formatString}'"); }
        if (!string.IsNullOrWhiteSpace(dataType)) { c.DataType = ParseDataType(dataType); changes.Add($"dataType={c.DataType}"); }
        if (sortByColumn is not null)
        {
            if (sortByColumn.Length == 0) { c.SortByColumn = null; changes.Add("sortBy=cleared"); }
            else
            {
                c.SortByColumn = t.Columns.Find(sortByColumn)
                    ?? throw new DaxterException($"Sort-by column not found on [{table}]: [{sortByColumn}].");
                changes.Add($"sortBy=[{sortByColumn}]");
            }
        }
        if (!string.IsNullOrWhiteSpace(summarizeBy)) { c.SummarizeBy = ParseSummarizeBy(summarizeBy); changes.Add($"summarizeBy={c.SummarizeBy}"); }
        if (displayFolder is not null) { c.DisplayFolder = displayFolder; changes.Add($"folder='{displayFolder}'"); }
        if (description is not null) { c.Description = description; changes.Add("description set"); }
        if (isHidden is not null) { c.IsHidden = isHidden.Value; changes.Add($"hidden={isHidden.Value}"); }
        if (dataCategory is not null) { c.DataCategory = dataCategory; changes.Add($"dataCategory='{dataCategory}'"); }

        if (changes.Count == 0)
            throw new DaxterException("No column properties to change — pass at least one of: formatString, " +
                                     "dataType, sortByColumn, summarizeBy, displayFolder, description, isHidden, dataCategory.");
        return $"alter column [{name}] on [{table}]: {string.Join(", ", changes)}";
    }

    /// <summary>Reads a table's columns and their current editable properties (for the editor to pre-fill).
    /// System row-number columns are omitted.</summary>
    public IReadOnlyList<ColumnInfo> ReadColumns(string table)
    {
        var t = Table(table);
        return t.Columns
            .Where(c => c.Type != Tom.ColumnType.RowNumber)
            .Select(c => new ColumnInfo(
                c.Name,
                c.Type switch
                {
                    Tom.ColumnType.Calculated => "calculated",
                    Tom.ColumnType.CalculatedTableColumn => "calculated-table",
                    _ => "data",
                },
                c.DataType.ToString(),
                c.FormatString ?? "",
                c.SortByColumn?.Name ?? "",
                c.SummarizeBy.ToString(),
                c.DisplayFolder ?? "",
                c.Description ?? "",
                c.IsHidden,
                c.DataCategory ?? ""))
            .ToList();
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

    /// <summary>Reads a table's current definition (for the editor to pre-fill): kind
    /// (<c>calculated</c>|<c>import</c>), the source expression (DAX or M), and the import columns as
    /// <c>Name:dataType:sourceColumn</c> lines.</summary>
    public (string Kind, string Expression, string Columns, bool HasPolicy) ReadTable(string name)
    {
        var t = _model.Tables.Find(name) ?? throw new DaxterException($"Table not found: [{name}].");
        string kind;
        string? expr;
        var hasPolicy = false;

        if (t.RefreshPolicy is Tom.BasicRefreshPolicy brp && !string.IsNullOrWhiteSpace(brp.SourceExpression))
        {
            // Incremental refresh: the M template lives on the POLICY, not the (auto-generated)
            // policy-range partitions — read it from there or the M view is blank.
            kind = "import";
            expr = brp.SourceExpression;
            hasPolicy = true;
        }
        else
        {
            var p = t.Partitions.FirstOrDefault();
            (kind, expr) = p?.Source switch
            {
                Tom.CalculatedPartitionSource cps => ("calculated", cps.Expression),
                Tom.MPartitionSource mps => ("import", mps.Expression),
                _ => ("calculated", ""),
            };
        }

        var cols = string.Join("\n", t.Columns.OfType<Tom.DataColumn>()
            .Select(c => $"{c.Name}:{c.DataType}:{c.SourceColumn}"));
        return (kind, expr ?? "", cols, hasPolicy);
    }

    // ---- incremental refresh policy ----

    /// <summary>Reads a table's incremental refresh policy settings, or null if it has none.</summary>
    public RefreshPolicyInfo? ReadRefreshPolicy(string name)
    {
        var t = _model.Tables.Find(name) ?? throw new DaxterException($"Table not found: [{name}].");
        if (t.RefreshPolicy is not Tom.BasicRefreshPolicy p) return null;
        return new RefreshPolicyInfo(
            p.SourceExpression ?? "", p.PollingExpression ?? "",
            p.RollingWindowGranularity.ToString(), (int)p.RollingWindowPeriods,
            p.IncrementalGranularity.ToString(), (int)p.IncrementalPeriods, (int)p.IncrementalPeriodsOffset);
    }

    /// <summary>v1.39.0 — enumerate every table in the model that has an incremental refresh
    /// policy defined (a <see cref="Tom.BasicRefreshPolicy"/>). Used by `daxter refresh
    /// apply-policy` and `daxter_apply_refresh_policy` to scope the operation: only these
    /// tables get the policy walked + partitions materialised; non-policy tables are
    /// untouched. Mirrors what Tabular Editor surfaces in its right-click menu.</summary>
    public IReadOnlyList<string> TablesWithRefreshPolicy()
    {
        var names = new List<string>();
        foreach (Tom.Table t in _model.Tables)
        {
            if (t.RefreshPolicy is Tom.BasicRefreshPolicy) names.Add(t.Name);
        }
        // Deterministic order so the worker's `objects` list and any human-readable plan
        // shown by the CLI/MCP match what the operator sees in the modelling tool.
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// <summary>v1.39.0 — true when the named table exists AND has a basic refresh policy.
    /// The CLI/MCP `--table T --apply-policy` paths call this for pre-flight validation, so
    /// "apply policy on a table that doesn't have one" fails with a clear message before we
    /// queue a job. Returns false on a missing table OR a table without a policy — the
    /// caller distinguishes the two cases with <see cref="ReadRefreshPolicy"/>.</summary>
    public bool HasRefreshPolicy(string name)
    {
        var t = _model.Tables.Find(name);
        return t?.RefreshPolicy is Tom.BasicRefreshPolicy;
    }

    /// <summary>Updates an <b>existing</b> basic incremental refresh policy in place — its M source and
    /// the rolling-window / incremental settings — without touching the table's partitions (so the
    /// policy is preserved). Throws if the table has no basic policy (create those in Desktop/TE).</summary>
    public string UpdateRefreshPolicy(string table, string source, string? polling,
        string rollingGranularity, int rollingPeriods,
        string incrementalGranularity, int incrementalPeriods, int incrementalOffset)
    {
        Require(source, "source");
        var t = Table(table);
        if (t.RefreshPolicy is not Tom.BasicRefreshPolicy p)
        {
            throw new DaxterException($"Table [{table}] has no basic incremental refresh policy to update.");
        }
        p.SourceExpression = source;
        p.PollingExpression = polling ?? "";   // "" explicitly clears it (null would silently skip)
        p.RollingWindowGranularity = ParseGranularity(rollingGranularity);
        p.RollingWindowPeriods = rollingPeriods;
        p.IncrementalGranularity = ParseGranularity(incrementalGranularity);
        p.IncrementalPeriods = incrementalPeriods;
        p.IncrementalPeriodsOffset = incrementalOffset;
        return $"update refresh policy on [{table}] (rolling {rollingPeriods} {rollingGranularity}, " +
               $"incremental {incrementalPeriods} {incrementalGranularity}, offset {incrementalOffset})";
    }

    private static Tom.RefreshGranularityType ParseGranularity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "day" or "days" => Tom.RefreshGranularityType.Day,
        "month" or "months" => Tom.RefreshGranularityType.Month,
        "quarter" or "quarters" => Tom.RefreshGranularityType.Quarter,
        "year" or "years" => Tom.RefreshGranularityType.Year,
        _ => throw new DaxterException($"Unknown granularity '{value}'. Use day|month|quarter|year."),
    };

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

    private static Tom.AggregateFunction ParseSummarizeBy(string value) => value.Trim().ToLowerInvariant() switch
    {
        "none" or "donotsummarize" => Tom.AggregateFunction.None,
        "sum" => Tom.AggregateFunction.Sum,
        "average" or "avg" => Tom.AggregateFunction.Average,
        "min" => Tom.AggregateFunction.Min,
        "max" => Tom.AggregateFunction.Max,
        "count" => Tom.AggregateFunction.Count,
        "distinctcount" or "distinct" => Tom.AggregateFunction.DistinctCount,
        _ => throw new DaxterException($"Unknown summarizeBy '{value}'. Use none|sum|average|min|max|count|distinctCount."),
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
