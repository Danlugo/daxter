using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Metadata;
using Daxter.Core.Query;

namespace Daxter.Core.Rest;

/// <summary>One pipeline stage column in the parameter matrix (ordered Dev→…→Prod).</summary>
public sealed record StageColumn(int Order, string Workspace);
/// <summary>A parameter and its value per stage; <see cref="Differs"/> ⇒ a deployment rule is in effect.</summary>
public sealed record ParamRow(string Name, IReadOnlyList<string?> Values, bool Differs);
/// <summary>A model's parameters compared across a pipeline's stages.</summary>
public sealed record PipelineParamMatrix(IReadOnlyList<StageColumn> Stages, IReadOnlyList<ParamRow> Rows, IReadOnlyList<string> Notes);

/// <summary>One model's parameter matrix within a pipeline-wide scan (with its dataset owner).</summary>
public sealed record ModelMatrix(string Model, PipelineParamMatrix Matrix, string? Owner = null);

/// <summary>How a scanned model fares in the deployment-rule audit.</summary>
public enum ModelRuleStatus { HasRules, NoRules, NoReadableParameters }
/// <summary>The result of a pipeline-wide scan (all models, each with its parameter matrix).</summary>
public sealed record PipelineScan(string PipelineId, IReadOnlyList<StageColumn> Stages, IReadOnlyList<ModelMatrix> Models);

/// <summary>A model that matches a rule's condition, with the value it has in that stage.</summary>
public sealed record RuleMatch(string Model, string? Actual);
/// <summary>
/// Result of running a saved check against a scan: <see cref="Checked"/> = models that have the
/// param in that stage; <see cref="Matches"/> = the ones whose value satisfies the condition
/// (the finder hits — for "≠ X", the models that are ≠ X), with <see cref="Matched"/> their count.
/// </summary>
public sealed record RuleResult(
    string Stage, string Param, string Value, bool NotEquals,
    int Checked, int Matched, IReadOnlyList<RuleMatch> Matches);

/// <summary>
/// Compares a model's parameter values across a pipeline's stages — the public Power BI REST API
/// has no operation to read deployment rules directly, so this <b>infers</b> them from per-stage
/// value differences (a value that changes Dev→Prod is where a rule or manual override applies).
/// Shared by the CLI, MCP and web layers.
/// </summary>
public static class PipelineRulesService
{
    /// <summary>
    /// Reads <paramref name="model"/>'s parameters from each stage's workspace over XMLA and
    /// builds a side-by-side matrix. Stages where the model isn't accessible are recorded in
    /// <see cref="PipelineParamMatrix.Notes"/> rather than failing the whole operation.
    /// </summary>
    /// <param name="stageConcurrency">
    /// How many stages to read in parallel. Default 4 (a single model's ~3 stages run concurrently
    /// → ≈ slowest-stage latency instead of the sum). Callers that already parallelize at a higher
    /// level — e.g. <see cref="ScanPipelineAsync"/> running many models at once — pass 1 to avoid a
    /// connection explosion against the XMLA endpoint.
    /// </param>
    public static async Task<PipelineParamMatrix> ComputeAsync(
        PowerBiRestClient rest, DaxterConfig baseConfig, ITokenProvider tokens,
        string pipelineId, string model, int stageConcurrency = 4, CancellationToken ct = default)
    {
        if (stageConcurrency < 1) stageConcurrency = 1;
        var stagesQr = await rest.PipelineStagesAsync(pipelineId, ct);
        int oi = Col(stagesQr, "order"), wi = Col(stagesQr, "workspaceName");

        var stages = stagesQr.Rows
            .Select(r => new StageColumn(
                oi >= 0 && r[oi] is not null ? Convert.ToInt32(r[oi]) : 0,
                wi >= 0 ? r[wi]?.ToString() ?? "" : ""))
            .Where(s => s.Workspace.Length > 0)
            .OrderBy(s => s.Order)
            .ToList();

        // Read each stage's parameters concurrently (each opens its own XMLA session — thread-safe).
        // Results stay aligned to stage order via the index; per-stage failures become notes.
        var sem = new System.Threading.SemaphoreSlim(stageConcurrency);
        var notesByStage = new string?[stages.Count];
        var perStage = new Dictionary<string, string?>?[stages.Count];
        await Task.WhenAll(stages.Select(async (s, i) =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var cfg = baseConfig.With(s.Workspace, model);
                var factory = new AdomdXmlaSessionFactory(cfg, tokens);
                using var session = await factory.CreateAsync(ct);
                var pr = new ModelMetadataService(session).Parameters();
                int ni = Col(pr, "Name"), ei = Col(pr, "Expression");
                var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                foreach (var row in pr.Rows)
                {
                    var name = ni >= 0 ? row[ni]?.ToString() ?? "" : "";
                    if (name.Length == 0) continue;
                    dict[name] = ParseParamValue(ei >= 0 ? row[ei]?.ToString() : null);
                }
                perStage[i] = dict;
            }
            catch (Exception ex)
            {
                notesByStage[i] = $"'{model}' in '{s.Workspace}': {ex.Message}";
            }
            finally { sem.Release(); }
        }));
        var notes = notesByStage.Where(n => n is not null).Select(n => n!).ToList();

        var names = perStage.Where(d => d is not null)
            .SelectMany(d => d!.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var rows = new List<ParamRow>();
        foreach (var n in names)
        {
            var vals = perStage.Select(d => d is null ? null : (d.TryGetValue(n, out var v) ? v : null)).ToList();
            var distinct = vals.Where(v => v is not null).Distinct().ToList();
            rows.Add(new ParamRow(n, vals, distinct.Count > 1));
        }

        return new PipelineParamMatrix(stages, rows, notes);
    }

    /// <summary>
    /// Scans every model in the pipeline's source (first) stage and computes its parameter matrix
    /// across all stages. Yields one <see cref="ModelMatrix"/> per model. <paramref name="onProgress"/>
    /// fires after each model with <c>(done, total)</c> so callers can show a progress indicator.
    /// </summary>
    public static async Task<PipelineScan> ScanPipelineAsync(
        PowerBiRestClient rest, DaxterConfig baseConfig, ITokenProvider tokens,
        string pipelineId, int concurrency = 5,
        Action<int, int>? onProgress = null, CancellationToken ct = default)
    {
        if (concurrency < 1) concurrency = 1;
        // Stages first — we need them to (a) find the source workspace for the model inventory,
        // and (b) reuse them for every per-model matrix below (no need to re-fetch).
        var stagesQr = await rest.PipelineStagesAsync(pipelineId, ct);
        int oi = Col(stagesQr, "order"), wi = Col(stagesQr, "workspaceName");
        var stages = stagesQr.Rows
            .Select(r => new StageColumn(
                oi >= 0 && r[oi] is not null ? Convert.ToInt32(r[oi]) : 0,
                wi >= 0 ? r[wi]?.ToString() ?? "" : ""))
            .Where(s => s.Workspace.Length > 0)
            .OrderBy(s => s.Order)
            .ToList();

        if (stages.Count == 0)
            return new PipelineScan(pipelineId, stages, Array.Empty<ModelMatrix>());

        // Model inventory comes from the source (Dev) stage — same naming applies across stages.
        var groupId = await rest.ResolveGroupIdAsync(stages[0].Workspace, ct);
        var ds = await rest.DatasetsAsync(groupId, ct);
        int ni = Col(ds, "name"), owni = Col(ds, "configuredBy");
        var owners = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in ds.Rows)
        {
            var nm = ni >= 0 ? r[ni]?.ToString() : null;
            if (!string.IsNullOrEmpty(nm)) owners[nm!] = owni >= 0 ? r[owni]?.ToString() : null;
        }
        var models = owners.Keys.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();

        // Run per-model in parallel — each model still serializes its own per-stage XMLA reads,
        // but N models are processed concurrently. PowerBiRestClient + ITokenProvider are thread-safe;
        // each ComputeAsync creates its own AdomdXmlaSessionFactory + session.
        var sem = new System.Threading.SemaphoreSlim(concurrency);
        var done = 0;
        var tasks = models.Select(async model =>
        {
            await sem.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                try
                {
                    // Stages sequential here: the scan already runs many models in parallel, so
                    // parallelizing stages too would multiply concurrent XMLA connections.
                    var matrix = await ComputeAsync(rest, baseConfig, tokens, pipelineId, model, stageConcurrency: 1, ct);
                    return new ModelMatrix(model, matrix, owners.GetValueOrDefault(model));
                }
                catch (Exception ex)
                {
                    // Per-model failure shouldn't kill the whole scan — record the empty matrix.
                    return new ModelMatrix(model,
                        new PipelineParamMatrix(stages, Array.Empty<ParamRow>(), new[] { ex.Message }),
                        owners.GetValueOrDefault(model));
                }
            }
            finally
            {
                var n = System.Threading.Interlocked.Increment(ref done);
                onProgress?.Invoke(n, models.Count);
                sem.Release();
            }
        }).ToList();

        var results = await Task.WhenAll(tasks);
        // Restore alphabetical order (Task.WhenAll preserves input order, but be defensive).
        var ordered = results.OrderBy(m => m.Model, StringComparer.OrdinalIgnoreCase).ToList();
        return new PipelineScan(pipelineId, stages, ordered);
    }

    /// <summary>Buckets a scanned model: has deployment rules, has none, or had no readable parameters
    /// (0 M parameters, or a dataset we couldn't read over XMLA).</summary>
    public static ModelRuleStatus ClassifyModel(ModelMatrix m)
        => m.Matrix.Rows.Count == 0 ? ModelRuleStatus.NoReadableParameters
         : m.Matrix.Rows.All(r => !r.Differs) ? ModelRuleStatus.NoRules
         : ModelRuleStatus.HasRules;

    /// <summary>
    /// Scans a single model across the pipeline's stages and returns it as a one-model
    /// <see cref="PipelineScan"/> — so the same evaluators (rule checks, param checks) work whether
    /// the scope is the whole pipeline or one model. Much cheaper than a full pipeline scan.
    /// </summary>
    public static async Task<PipelineScan> ScanModelAsync(
        PowerBiRestClient rest, DaxterConfig baseConfig, ITokenProvider tokens,
        string pipelineId, string model, int stageConcurrency = 4, CancellationToken ct = default)
    {
        var matrix = await ComputeAsync(rest, baseConfig, tokens, pipelineId, model, stageConcurrency, ct);
        return new PipelineScan(pipelineId, matrix.Stages, new[] { new ModelMatrix(model, matrix) });
    }

    /// <summary>
    /// Evaluates a saved check (param @ stage == value, or != for <paramref name="notEquals"/>)
    /// against a completed scan as a <b>finder</b>: counts models that <i>have</i> the param in that
    /// stage (<see cref="RuleResult.Checked"/>) and lists the ones whose value <i>satisfies</i> the
    /// condition (the matches — for "≠ X", the models that are ≠ X). Pure in-memory — no extra XMLA.
    /// </summary>
    public static RuleResult EvaluateRule(PipelineScan scan, string stage, string param, string value, bool notEquals)
    {
        var sIdx = scan.Stages.ToList().FindIndex(s => string.Equals(s.Workspace, stage, StringComparison.OrdinalIgnoreCase));
        if (sIdx < 0) return new RuleResult(stage, param, value, notEquals, 0, 0, Array.Empty<RuleMatch>());

        int checkedCount = 0;
        var matches = new List<RuleMatch>();
        foreach (var m in scan.Models)
        {
            var row = m.Matrix.Rows.FirstOrDefault(r => string.Equals(r.Name, param, StringComparison.OrdinalIgnoreCase));
            var actual = row?.Values[sIdx];
            if (actual is null) continue; // model doesn't have this param in this stage
            checkedCount++;
            var equals = string.Equals(actual, value, StringComparison.Ordinal);
            if (notEquals ? !equals : equals) matches.Add(new RuleMatch(m.Model, actual)); // condition holds → a hit
        }
        return new RuleResult(stage, param, value, notEquals, checkedCount, matches.Count, matches);
    }

    /// <summary>
    /// Flattens a matrix into a tabular <see cref="QueryResult"/> with columns
    /// <c>Parameter | &lt;stage 1&gt; | &lt;stage 2&gt; | … | RuleApplied</c>, for CLI/MCP output.
    /// </summary>
    public static QueryResult ToTable(PipelineParamMatrix matrix)
    {
        var columns = new List<string> { "Parameter" };
        columns.AddRange(matrix.Stages.Select(s => s.Workspace));
        columns.Add("RuleApplied");

        var rows = new List<object?[]>();
        foreach (var p in matrix.Rows)
        {
            var row = new object?[columns.Count];
            row[0] = p.Name;
            for (var i = 0; i < matrix.Stages.Count; i++) row[i + 1] = p.Values[i];
            row[^1] = p.Differs;
            rows.Add(row);
        }
        return new QueryResult(columns, rows);
    }

    private static int Col(QueryResult r, string column)
    {
        for (var i = 0; i < r.Columns.Count; i++)
            if (string.Equals(r.Columns[i], column, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    // "DB_DEV" meta [IsParameterQuery=true, …]  →  DB_DEV   (#datetime(…)/numbers passed through)
    private static string? ParseParamValue(string? expr)
    {
        if (string.IsNullOrEmpty(expr)) return null;
        var idx = expr.IndexOf(" meta [", StringComparison.Ordinal);
        var v = (idx >= 0 ? expr[..idx] : expr).Trim();
        if (v.Length >= 2 && v[0] == '"' && v[^1] == '"') v = v[1..^1].Replace("\"\"", "\"");
        return v;
    }
}
