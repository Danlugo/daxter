using System.CommandLine;
using System.Diagnostics;
using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Editing;
using Daxter.Core.Formatting;
using Daxter.Core.Maintenance;
using Daxter.Core.Metadata;
using Daxter.Core.Query;
using Daxter.Core.Rest;

namespace Daxter.Cli;

/// <summary>
/// DAXter — a Mac/Linux XMLA query client for the Power BI Service.
/// Runs DAX, MDX, and DMV queries against a Premium/PPU/Fabric XMLA endpoint.
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        var connectionOptions = new ConnectionOptions();

        var outputOption = new Option<string>("--output", "-o")
        {
            Description = "Output format: table, csv, or json.",
            DefaultValueFactory = _ => "table",
        };
        var fileOption = new Option<string?>("--file", "-f")
        {
            Description = "Read the query text from a file instead of the argument.",
        };
        var queryArgument = new Argument<string?>("query")
        {
            Description = "Query text (DAX/MDX, or a DMV SELECT). Optional when --file is used.",
            Arity = ArgumentArity.ZeroOrOne,
        };

        // daxter query "EVALUATE ..."
        var queryCommand = new Command("query", "Run a DAX or MDX query.")
        {
            queryArgument, fileOption, outputOption,
        };
        connectionOptions.AddTo(queryCommand);
        queryCommand.SetAction((parseResult, ct) => RunQueryAsync(
            () => QueryTextFrom(parseResult, queryArgument, fileOption),
            () => connectionOptions.Resolve(parseResult),
            () => parseResult.GetValue(outputOption),
            ct));

        // daxter dmv "SELECT * FROM $SYSTEM.DISCOVER_STORAGE_TABLES"
        var dmvArgument = new Argument<string?>("statement")
        {
            Description = "A DMV statement, e.g. SELECT * FROM $SYSTEM.TMSCHEMA_TABLES.",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var dmvCommand = new Command("dmv", "Run a DMV ($SYSTEM) query.")
        {
            dmvArgument, fileOption, outputOption,
        };
        connectionOptions.AddTo(dmvCommand);
        dmvCommand.SetAction((parseResult, ct) => RunQueryAsync(
            () => QueryTextFrom(parseResult, dmvArgument, fileOption),
            () => connectionOptions.Resolve(parseResult),
            () => parseResult.GetValue(outputOption),
            ct));

        // daxter ls — list tables in the model
        var lsCommand = new Command("ls", "List tables in the semantic model.")
        {
            outputOption,
        };
        connectionOptions.AddTo(lsCommand);
        lsCommand.SetAction((parseResult, ct) => RunQueryAsync(
            () => "SELECT [Name] AS [Table] FROM $SYSTEM.TMSCHEMA_TABLES ORDER BY [Name]",
            () => connectionOptions.Resolve(parseResult),
            () => parseResult.GetValue(outputOption),
            ct));

        // daxter login — interactive device-code sign-in (caches the token)
        var loginCommand = new Command("login", "Sign in interactively and cache the token.");
        connectionOptions.AddTo(loginCommand);
        loginCommand.SetAction((parseResult, ct) => RunLoginAsync(
            () => connectionOptions.Resolve(parseResult), ct));

        var modelCommand = BuildModelCommand(connectionOptions, outputOption);
        var envCommand = BuildEnvCommand();
        var refreshCommand = BuildRefreshCommand(connectionOptions, outputOption);
        var cacheCommand = BuildCacheCommand(connectionOptions);
        var wsCommand = BuildWorkspaceCommand(connectionOptions, outputOption);
        var testRlsCommand = BuildTestRlsCommand(connectionOptions, outputOption);
        var pipelineCommand = BuildPipelineCommand(connectionOptions, outputOption);

        var mcpCommand = new Command("mcp", "Run DAXter as a Model Context Protocol (stdio) server.");
        mcpCommand.SetAction((_, ct) => Mcp.McpServer.RunAsync(ct));

        var webPort = new Option<int>("--port") { Description = "Port for the web console.", DefaultValueFactory = _ => 8080 };
        var webCommand = new Command("web", "Run the DAXter web console (status, configure, explore).") { webPort };
        webCommand.SetAction((pr, ct) => RunWebAsync(pr.GetValue(webPort), ct));

        var root = new RootCommand(
            "DAXter — Power BI Service CLI: query, model metadata, maintenance, and inventory.")
        {
            queryCommand, dmvCommand, lsCommand, loginCommand, modelCommand, envCommand,
            refreshCommand, cacheCommand, wsCommand, testRlsCommand, pipelineCommand, mcpCommand, webCommand,
        };

        return await root.Parse(args).InvokeAsync();
    }

    private static async Task<int> RunQueryAsync(
        Func<string> queryFactory,
        Func<DaxterConfig> configFactory,
        Func<string?> outputFactory,
        CancellationToken ct)
    {
        try
        {
            var query = queryFactory();
            var config = configFactory();
            var format = ResultFormatterFactory.Parse(outputFactory());
            var factory = BuildSessionFactory(config);

            using var session = await factory.CreateAsync(ct);
            var result = session.Execute(query);

            Console.Out.Write(ResultFormatterFactory.Create(format).Format(result));
            Console.Error.WriteLine($"({result.RowCount} row{(result.RowCount == 1 ? "" : "s")})");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static async Task<int> RunLoginAsync(Func<DaxterConfig> configFactory, CancellationToken ct)
    {
        try
        {
            var config = configFactory();

            // login is always interactive (device code).
            var interactive = new DaxterConfig
            {
                Workspace = config.Workspace,
                Dataset = config.Dataset,
                TenantId = config.TenantId,
                ClientId = config.ClientId,
                AuthMode = AuthMode.DeviceCode,
            };

            var provider = new MsalTokenProvider(interactive, deviceCodePrompt: WriteToStdErr);
            var token = await provider.GetTokenAsync(ct);
            Console.Error.WriteLine($"Signed in. Token valid until {token.ExpiresOn:u}.");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static Command BuildModelCommand(ConnectionOptions connectionOptions, Option<string> outputOption)
    {
        var tableOption = new Option<string?>("--table", "-t") { Description = "Table name." };
        var roleOption = new Option<string?>("--role") { Description = "RLS role name." };
        var withExprOption = new Option<bool>("--with-expr") { Description = "Include DAX expressions." };

        var measures = new Command("measures", "List measures (optionally with DAX).") { withExprOption, outputOption };
        connectionOptions.AddTo(measures);
        measures.SetAction((pr, ct) => RunModelAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            svc => svc.Measures(pr.GetValue(withExprOption)), ct));

        var measureName = new Argument<string?>("name") { Description = "Measure name.", Arity = ArgumentArity.ZeroOrOne };
        var measure = new Command("measure", "Show one measure's full definition.") { measureName, outputOption };
        connectionOptions.AddTo(measure);
        measure.SetAction((pr, ct) => RunModelAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            svc => svc.Measure(RequireArg(pr, measureName, "measure name")), ct));

        var mcode = new Command("mcode", "Show the M (Power Query) code for a table's partitions.") { tableOption, outputOption };
        connectionOptions.AddTo(mcode);
        mcode.SetAction((pr, ct) => RunModelAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            svc => svc.MCode(RequireOption(pr, tableOption, "--table")), ct));

        var parameters = new Command("parameters", "List shared M expressions / parameters.") { outputOption };
        connectionOptions.AddTo(parameters);
        parameters.SetAction((pr, ct) => RunModelAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            svc => svc.Parameters(), ct));

        var partitions = new Command("partitions", "List partitions and last-refresh times.") { tableOption, outputOption };
        connectionOptions.AddTo(partitions);
        partitions.SetAction((pr, ct) => RunModelAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            svc => svc.Partitions(pr.GetValue(tableOption)), ct));

        var columns = new Command("columns", "List columns (name, hidden), optionally for one table.") { tableOption, outputOption };
        connectionOptions.AddTo(columns);
        columns.SetAction((pr, ct) => RunModelAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            svc => svc.Columns(pr.GetValue(tableOption)), ct));

        var rls = new Command("rls", "Show RLS roles, or a role's filters + members.") { roleOption, outputOption };
        connectionOptions.AddTo(rls);
        rls.SetAction((pr, ct) => RunRlsAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            pr.GetValue(roleOption), ct));

        var outFileOption = new Option<string?>("--out") { Description = "Write to this file instead of stdout." };
        var export = new Command("export", "Export the model definition as .bim (TOM).") { outFileOption };
        connectionOptions.AddTo(export);
        export.SetAction((pr, ct) => RunExportAsync(
            () => connectionOptions.Resolve(pr), pr.GetValue(outFileOption), ct));

        var otherArg = new Argument<string?>("other")
        {
            Description = "The other dataset to compare against (same workspace).",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var diff = new Command("diff", "Compare measures between this model and another.") { otherArg, outputOption };
        connectionOptions.AddTo(diff);
        diff.SetAction((pr, ct) => RunDiffAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            RequireArg(pr, otherArg, "other dataset"), ct));

        return new Command("model", "Inspect model metadata (measures, M code, RLS, partitions, columns, export, diff); edit with `model edit`.")
        {
            measures, measure, mcode, parameters, partitions, columns, rls, export, diff,
            BuildModelEditCommand(connectionOptions),
        };
    }

    private static Command BuildModelEditCommand(ConnectionOptions connectionOptions)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the TMSL without applying." };
        var yes = new Option<bool>("--yes") { Description = "Apply the edit (required for mutations)." };
        var force = new Option<bool>("--force") { Description = "Allow applying against a PROD-looking target." };
        var table = new Option<string?>("--table", "-t") { Description = "Table name." };
        var name = new Option<string?>("--name", "-n") { Description = "Object name (measure / column / parameter / role / table)." };
        var dax = new Option<string?>("--dax") { Description = "DAX expression." };
        var m = new Option<string?>("--m") { Description = "M (Power Query) expression." };
        var format = new Option<string?>("--format") { Description = "Measure format string, e.g. $#,0.00." };
        var folder = new Option<string?>("--folder") { Description = "Display folder." };
        var desc = new Option<string?>("--description") { Description = "Description." };
        var dataType = new Option<string?>("--data-type") { Description = "Column data type (inferred if omitted)." };
        var perm = new Option<string?>("--permission") { Description = "Role model permission: read|readRefresh|refresh|administrator|none." };
        var membersOpt = new Option<string?>("--members") { Description = "Comma-separated member UPNs / groups." };
        var filterTable = new Option<string?>("--filter-table") { Description = "Table to apply an RLS filter to." };
        var filterDax = new Option<string?>("--filter") { Description = "DAX filter expression for --filter-table." };
        var partition = new Option<string?>("--partition") { Description = "Partition name." };
        var tmslOpt = new Option<string?>("--tmsl") { Description = "Raw TMSL command (escape hatch)." };

        Command Edit(string n, string d, Func<ParseResult, ModelEditService, string> build, params Option[] opts)
        {
            var c = new Command(n, d) { dryRun, yes, force };
            foreach (var o in opts) c.Add(o);
            connectionOptions.AddTo(c);
            c.SetAction((pr, ct) => RunModelEditAsync(() => connectionOptions.Resolve(pr),
                svc => build(pr, svc), pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));
            return c;
        }

        return new Command("edit",
            "Edit the model (measures, parameters, roles, calculated columns, partition sources, tables). " +
            "DRY-RUN unless --yes. IRREVERSIBLE for PBIX download; a .bim backup is written before applying.")
        {
            Edit("measure", "Create or alter a measure.",
                (pr, svc) => svc.UpsertMeasure(RequireOption(pr, table, "--table"), RequireOption(pr, name, "--name"),
                    RequireOption(pr, dax, "--dax"), pr.GetValue(format), pr.GetValue(folder), pr.GetValue(desc)),
                table, name, dax, format, folder, desc),
            Edit("delete-measure", "Delete a measure.",
                (pr, svc) => svc.DeleteMeasure(RequireOption(pr, table, "--table"), RequireOption(pr, name, "--name")),
                table, name),
            Edit("parameter", "Create or alter a shared M expression / parameter.",
                (pr, svc) => svc.UpsertExpression(RequireOption(pr, name, "--name"), RequireOption(pr, m, "--m"), pr.GetValue(desc)),
                name, m, desc),
            Edit("delete-parameter", "Delete a shared M expression / parameter.",
                (pr, svc) => svc.DeleteExpression(RequireOption(pr, name, "--name")),
                name),
            Edit("role", "Create or alter an RLS/OLS role.",
                (pr, svc) => svc.UpsertRole(RequireOption(pr, name, "--name"), pr.GetValue(perm),
                    (pr.GetValue(membersOpt) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x => new RoleMember(x)),
                    string.IsNullOrWhiteSpace(pr.GetValue(filterTable)) ? null : [new TableFilter(pr.GetValue(filterTable)!, pr.GetValue(filterDax) ?? "")]),
                name, perm, membersOpt, filterTable, filterDax),
            Edit("delete-role", "Delete an RLS/OLS role.",
                (pr, svc) => svc.DeleteRole(RequireOption(pr, name, "--name")),
                name),
            Edit("column", "Create or alter a calculated column.",
                (pr, svc) => svc.UpsertCalculatedColumn(RequireOption(pr, table, "--table"), RequireOption(pr, name, "--name"),
                    RequireOption(pr, dax, "--dax"), pr.GetValue(dataType)),
                table, name, dax, dataType),
            Edit("delete-column", "Delete a column.",
                (pr, svc) => svc.DeleteColumn(RequireOption(pr, table, "--table"), RequireOption(pr, name, "--name")),
                table, name),
            Edit("source", "Set a partition's M (Power Query) source.",
                (pr, svc) => svc.SetPartitionSource(RequireOption(pr, table, "--table"), RequireOption(pr, partition, "--partition"), RequireOption(pr, m, "--m")),
                table, partition, m),
            Edit("calc-table", "Create or replace a calculated table.",
                (pr, svc) => svc.CreateCalculatedTable(RequireOption(pr, name, "--name"), RequireOption(pr, dax, "--dax")),
                name, dax),
            Edit("delete-table", "Delete an entire table.",
                (pr, svc) => svc.DeleteTable(RequireOption(pr, name, "--name")),
                name),
            Edit("tmsl", "Execute a raw TMSL command (escape hatch).",
                (pr, svc) => svc.Raw(RequireOption(pr, tmslOpt, "--tmsl")),
                tmslOpt),
        };
    }

    private static async Task<int> RunModelEditAsync(
        Func<DaxterConfig> configFactory,
        Func<ModelEditService, string> build,
        bool dryRun, bool yes, bool force, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            var token = await BuildTokenProvider(config).GetTokenAsync(ct);

            using var service = new ModelEditService(config, token);
            var change = build(service);   // stages the change in-memory (TOM); not yet saved

            Console.Error.WriteLine(
                "⚠ Editing a Power BI Desktop model over XMLA is IRREVERSIBLE for PBIX download. " +
                "A .bim backup is written before applying.");

            return ApplySafety(config, change, dryRun, yes, force, () =>
            {
                var backup = new ModelBackupService(config, token).Backup();
                Console.Error.WriteLine($"Backup written: {backup}");
                service.Apply();
            }, successMessage: "Model edit applied.");
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static async Task<int> RunExportAsync(
        Func<DaxterConfig> configFactory, string? outFile, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            var token = await BuildTokenProvider(config).GetTokenAsync(ct);
            var bim = new Daxter.Core.Export.ModelExportService(config, token).ExportBim();

            if (string.IsNullOrWhiteSpace(outFile))
            {
                Console.Out.WriteLine(bim);
            }
            else
            {
                await File.WriteAllTextAsync(outFile, bim, ct);
                Console.Error.WriteLine($"Wrote {bim.Length:N0} bytes to {outFile}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static async Task<int> RunDiffAsync(
        Func<DaxterConfig> configFactory, Func<string?> outputFactory, string other, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            var formatter = ResultFormatterFactory.Create(ResultFormatterFactory.Parse(outputFactory()));

            var otherConfig = new DaxterConfig
            {
                Workspace = config.Workspace,
                Dataset = other,
                TenantId = config.TenantId,
                ClientId = config.ClientId,
                ClientSecret = config.ClientSecret,
                AuthMode = config.AuthMode,
                Environment = config.Environment,
            };

            using var left = await BuildSessionFactory(config).CreateAsync(ct);
            using var right = await BuildSessionFactory(otherConfig).CreateAsync(ct);
            var diff = ModelDiffService.DiffMeasures(left, right);

            Console.Error.WriteLine($"Comparing measures: '{config.Dataset}' (left) vs '{other}' (right)");
            Console.Out.Write(formatter.Format(diff));
            Console.Error.WriteLine(RowSummary(diff));
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static Command BuildEnvCommand()
    {
        var ls = new Command("ls", "List configured environment profiles.");
        ls.SetAction((pr, ct) => Task.FromResult(EnvLs()));
        return new Command("env", "Manage environment profiles (one SP, workspace per env).") { ls };
    }

    private static int EnvLs()
    {
        var envs = DaxterConfig.ListEnvironments();
        if (envs.Count == 0)
        {
            Console.Error.WriteLine(
                $"No environments configured. Define {DaxterConfig.EnvWorkspace}_<ENV> variables " +
                $"(e.g. {DaxterConfig.EnvWorkspace}_DEV).");
            return 0;
        }

        var active = Environment.GetEnvironmentVariable(DaxterConfig.EnvEnvironment)?.Trim().ToLowerInvariant();
        foreach (var name in envs)
        {
            Console.Out.WriteLine(name == active ? $"* {name}" : $"  {name}");
        }

        return 0;
    }

    private static async Task<int> RunModelAsync(
        Func<DaxterConfig> configFactory,
        Func<string?> outputFactory,
        Func<ModelMetadataService, QueryResult> select,
        CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            var format = ResultFormatterFactory.Parse(outputFactory());
            var factory = BuildSessionFactory(config);

            using var session = await factory.CreateAsync(ct);
            var result = select(new ModelMetadataService(session));

            Console.Out.Write(ResultFormatterFactory.Create(format).Format(result));
            Console.Error.WriteLine(RowSummary(result));
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static async Task<int> RunRlsAsync(
        Func<DaxterConfig> configFactory,
        Func<string?> outputFactory,
        string? role,
        CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            var formatter = ResultFormatterFactory.Create(ResultFormatterFactory.Parse(outputFactory()));
            var factory = BuildSessionFactory(config);

            using var session = await factory.CreateAsync(ct);
            var svc = new ModelMetadataService(session);

            if (string.IsNullOrWhiteSpace(role))
            {
                var roles = svc.Roles();
                Console.Out.Write(formatter.Format(roles));
                Console.Error.WriteLine(RowSummary(roles));
                return 0;
            }

            var filters = svc.RoleFilters(role);
            Console.Error.WriteLine($"Role '{role}' — table filters:");
            Console.Out.Write(formatter.Format(filters));

            var members = svc.RoleMembers(role);
            Console.Error.WriteLine($"Role '{role}' — members:");
            Console.Out.Write(formatter.Format(members));
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static void RequireDataset(DaxterConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Dataset))
        {
            throw new DaxterException(
                "This command targets a model — set a dataset with --dataset or DAXTER_DATASET.");
        }
    }

    private static string RequireOption(ParseResult parseResult, Option<string?> option, string label)
    {
        var value = parseResult.GetValue(option);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DaxterException($"{label} is required.");
        }

        return value;
    }

    private static string RequireArg(ParseResult parseResult, Argument<string?> argument, string label)
    {
        var value = parseResult.GetValue(argument);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new DaxterException($"{label} is required.");
        }

        return value;
    }

    private static string RowSummary(QueryResult result)
        => $"({result.RowCount} row{(result.RowCount == 1 ? "" : "s")})";

    // ---- Deployment pipelines (REST) ----

    private static Command BuildPipelineCommand(ConnectionOptions connectionOptions, Option<string> outputOption)
    {
        var pipelineOption = new Option<string?>("--pipeline") { Description = "Deployment pipeline id." };
        var modelOption = new Option<string?>("--model")
        {
            Description = "Model (semantic dataset) name — same in every stage.",
        };

        var ls = new Command("ls", "List deployment pipelines.") { outputOption };
        connectionOptions.AddTo(ls);
        ls.SetAction((pr, ct) => RunRestQueryAsync(() => connectionOptions.Resolve(pr),
            () => pr.GetValue(outputOption), (rest, _, c) => rest.PipelinesAsync(c), ct));

        var stages = new Command("stages", "List a pipeline's stages (dev/test/prod → workspace).")
        {
            pipelineOption, outputOption,
        };
        connectionOptions.AddTo(stages);
        stages.SetAction((pr, ct) => RunRestQueryAsync(() => connectionOptions.Resolve(pr),
            () => pr.GetValue(outputOption),
            (rest, _, c) => rest.PipelineStagesAsync(RequireOption(pr, pipelineOption, "--pipeline"), c), ct));

        var operations = new Command("operations", "List a pipeline's deployment history.")
        {
            pipelineOption, outputOption,
        };
        connectionOptions.AddTo(operations);
        operations.SetAction((pr, ct) => RunRestQueryAsync(() => connectionOptions.Resolve(pr),
            () => pr.GetValue(outputOption),
            (rest, _, c) => rest.PipelineOperationsAsync(RequireOption(pr, pipelineOption, "--pipeline"), c), ct));

        // Deployment-rule check: read the model's parameters from each stage over XMLA and
        // flag values that differ across stages — where a deployment rule (or manual override) applies.
        var rules = new Command("rules",
            "Show a model's parameter values across each pipeline stage and flag where a deployment rule is applied.")
        {
            pipelineOption, modelOption, outputOption,
        };
        connectionOptions.AddTo(rules);
        rules.SetAction((pr, ct) => RunPipelineRulesAsync(
            () => connectionOptions.Resolve(pr),
            () => pr.GetValue(outputOption),
            RequireOption(pr, pipelineOption, "--pipeline"),
            RequireOption(pr, modelOption, "--model"),
            ct));

        // Pipeline-wide audits — scans every model in the pipeline once, runs the chosen check.
        var stageOption = new Option<string?>("--stage") { Description = "Stage workspace name (e.g. 'Prod'). For --check." };
        var paramOption = new Option<string?>("--param") { Description = "Parameter name (e.g. DATABASE_NAME). For --check." };
        var valueOption = new Option<string?>("--value") { Description = "Expected value to match against. For --check." };
        var notEqualsOption = new Option<bool>("--not-equals") { Description = "Invert the value check (find non-matching models)." };
        var modeOption = new Option<string?>("--mode")
        {
            Description = "Audit mode: no-rules (default) or check.",
        };
        var concurrencyOption = new Option<int>("--concurrency")
        {
            Description = "Number of models to scan in parallel (default 5).",
            DefaultValueFactory = _ => 5,
        };
        var savedOption = new Option<string?>("--saved") { Description = "Run a saved check by name (shared with the web console)." };
        var listSavedOption = new Option<bool>("--list-saved") { Description = "List saved checks and exit." };
        var runAllSavedOption = new Option<bool>("--run-all-saved") { Description = "Run every saved rule for --pipeline and list the models matching each." };

        var audit = new Command("audit",
            "Audit a pipeline (all models) or one --model: models without rules, a parameter check, or run saved checks/rules (--saved / --list-saved / --run-all-saved).")
        {
            pipelineOption, modelOption, modeOption, stageOption, paramOption, valueOption, notEqualsOption, concurrencyOption, savedOption, listSavedOption, runAllSavedOption, outputOption,
        };
        connectionOptions.AddTo(audit);
        audit.SetAction((pr, ct) => RunPipelineAuditAsync(
            () => connectionOptions.Resolve(pr),
            () => pr.GetValue(outputOption),
            pr.GetValue(pipelineOption), pr.GetValue(modelOption),
            pr.GetValue(modeOption),
            pr.GetValue(stageOption), pr.GetValue(paramOption), pr.GetValue(valueOption),
            pr.GetValue(notEqualsOption), pr.GetValue(concurrencyOption),
            pr.GetValue(savedOption), pr.GetValue(listSavedOption), pr.GetValue(runAllSavedOption), ct));

        return new Command("pipeline", "Deployment pipelines: stages, history, and deployment-rule checks.")
        {
            ls, stages, operations, rules, audit,
        };
    }

    private static async Task<int> RunPipelineAuditAsync(
        Func<DaxterConfig> configFactory, Func<string?> outputFactory,
        string? pipelineId, string? model, string? mode, string? stage, string? param, string? value, bool notEquals,
        int concurrency, string? saved, bool listSaved, bool runAllSaved, CancellationToken ct)
    {
        try
        {
            var formatter = ResultFormatterFactory.Create(ResultFormatterFactory.Parse(outputFactory()));
            var savedStore = new Daxter.Core.Audit.SavedAuditCheckStore();

            // --list-saved: just print the saved checks (no pipeline / auth needed).
            if (listSaved)
            {
                var rows = savedStore.All()
                    .Select(c => new object?[] { c.Name, c.PipelineId, c.Stage, c.Param, (c.NotEquals ? "!=" : "=") + " " + c.Value })
                    .ToList();
                Console.Out.Write(formatter.Format(new QueryResult(new[] { "Name", "Pipeline", "Stage", "Param", "Expected" }, rows)));
                Console.Error.WriteLine($"({rows.Count} saved check{(rows.Count == 1 ? "" : "s")})");
                return 0;
            }

            var resolvedMode = (mode ?? "no-rules").Trim().ToLowerInvariant();

            // --saved <name>: load the spec and run it as a check.
            if (!string.IsNullOrWhiteSpace(saved))
            {
                var c = savedStore.FindByName(saved)
                    ?? throw new DaxterException($"No saved check named '{saved}'. See `pipeline audit --list-saved`.");
                pipelineId = c.PipelineId; stage = c.Stage; param = c.Param; value = c.Value; notEquals = c.NotEquals;
                resolvedMode = "check";
                Console.Error.WriteLine($"Running saved check '{c.Name}': {c.Param} {(c.NotEquals ? "!=" : "=")} '{c.Value}' in {c.Stage}");
            }

            if (resolvedMode is not ("no-rules" or "check"))
                throw new DaxterException("Unknown --mode. Use 'no-rules' or 'check'.");
            if (string.IsNullOrWhiteSpace(pipelineId))
                throw new DaxterException("Provide --pipeline (or --saved <name> / --list-saved).");

            var config = configFactory();
            var tokens = BuildTokenProvider(config);
            using var rest = new PowerBiRestClient(tokens);

            PipelineScan scan;
            if (!string.IsNullOrWhiteSpace(model))
            {
                Console.Error.WriteLine($"Reading model '{model}' across stages…");
                scan = await PipelineRulesService.ScanModelAsync(rest, config, tokens, pipelineId!, model, ct: ct);
            }
            else
            {
                Console.Error.WriteLine($"Scanning pipeline — {concurrency} models in parallel…");
                scan = await PipelineRulesService.ScanPipelineAsync(rest, config, tokens, pipelineId!, concurrency,
                    (done, total) => Console.Error.Write($"\r  {done}/{total}"), ct);
                Console.Error.WriteLine();
            }

            // --run-all-saved: evaluate every saved rule for this pipeline against the scan.
            if (runAllSaved)
            {
                var rules = savedStore.All().Where(c => c.PipelineId == pipelineId).ToList();
                if (rules.Count == 0) throw new DaxterException("No saved rules for this pipeline. Save some in the web console first.");
                var rows = rules.Select(c =>
                {
                    var r = PipelineRulesService.EvaluateRule(scan, c.Stage, c.Param, c.Value, c.NotEquals);
                    return new object?[] { c.Name, c.Param, c.Stage, (c.NotEquals ? "!=" : "=") + " " + c.Value, $"{r.Matched}/{r.Checked}" };
                }).ToList();
                Console.Out.Write(formatter.Format(new QueryResult(new[] { "Rule", "Param", "Stage", "Expected", "Matches" }, rows)));
                Console.Error.WriteLine($"({rules.Count} rule(s) evaluated against {scan.Models.Count} models)");
                return 0;
            }

            QueryResult table;
            if (resolvedMode == "no-rules")
            {
                var rows = scan.Models
                    .Where(m => m.Matrix.Rows.Count > 0 && m.Matrix.Rows.All(r => !r.Differs))
                    .Select(m => new object?[] { m.Model })
                    .ToList();
                table = new QueryResult(new[] { "Model" }, rows);
                Console.Out.Write(formatter.Format(table));
                Console.Error.WriteLine(
                    $"({rows.Count} of {scan.Models.Count} models have no detected rules across {scan.Stages.Count} stage{(scan.Stages.Count == 1 ? "" : "s")})");
            }
            else
            {
                if (string.IsNullOrWhiteSpace(stage) || string.IsNullOrWhiteSpace(param) || string.IsNullOrWhiteSpace(value))
                    throw new DaxterException("--mode check requires --stage, --param, and --value.");
                var sIdx = scan.Stages.ToList().FindIndex(s => string.Equals(s.Workspace, stage, StringComparison.OrdinalIgnoreCase));
                if (sIdx < 0)
                    throw new DaxterException($"Stage '{stage}' isn't in this pipeline. Available: " +
                        string.Join(", ", scan.Stages.Select(s => s.Workspace)));

                var rows = scan.Models
                    .Select(m => new { m.Model, Row = m.Matrix.Rows.FirstOrDefault(r => string.Equals(r.Name, param, StringComparison.OrdinalIgnoreCase)) })
                    .Where(x => x.Row is not null)
                    .Select(x => new { x.Model, Value = x.Row!.Values[sIdx] })
                    .Where(x => x.Value is not null && (notEquals
                        ? !string.Equals(x.Value, value, StringComparison.Ordinal)
                        : string.Equals(x.Value, value, StringComparison.Ordinal)))
                    .Select(x => new object?[] { x.Model, x.Value })
                    .ToList();
                table = new QueryResult(new[] { "Model", $"{param}@{stage}" }, rows);
                Console.Out.Write(formatter.Format(table));
                Console.Error.WriteLine(
                    $"({rows.Count} of {scan.Models.Count} models have {param} {(notEquals ? "!=" : "==")} '{value}' in {stage})");
            }
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static async Task<int> RunPipelineRulesAsync(
        Func<DaxterConfig> configFactory, Func<string?> outputFactory,
        string pipelineId, string model, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            var formatter = ResultFormatterFactory.Create(ResultFormatterFactory.Parse(outputFactory()));
            var tokens = BuildTokenProvider(config);
            using var rest = new PowerBiRestClient(tokens);

            var matrix = await PipelineRulesService.ComputeAsync(rest, config, tokens, pipelineId, model, ct: ct);
            var table = PipelineRulesService.ToTable(matrix);
            Console.Out.Write(formatter.Format(table));

            var rules = matrix.Rows.Count(r => r.Differs);
            Console.Error.WriteLine(
                $"({matrix.Stages.Count} stage{(matrix.Stages.Count == 1 ? "" : "s")}, " +
                $"{matrix.Rows.Count} parameter{(matrix.Rows.Count == 1 ? "" : "s")}, " +
                $"{rules} differ → deployment rule{(rules == 1 ? "" : "s")} inferred)");
            foreach (var note in matrix.Notes) Console.Error.WriteLine("note: " + note);
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    // ---- Test RLS (XMLA impersonation) ----

    private static Command BuildTestRlsCommand(ConnectionOptions connectionOptions, Option<string> outputOption)
    {
        var roleOption = new Option<string?>("--role") { Description = "RLS role to assume." };
        var userOption = new Option<string?>("--user") { Description = "User to impersonate (EffectiveUserName)." };
        var queryOption = new Option<string?>("--query", "-q")
        {
            Description = "DAX to run under the identity (default: show the effective identity).",
        };

        var command = new Command("test-rls", "Run a query under an RLS role / impersonated user.")
        {
            roleOption, userOption, queryOption, outputOption,
        };
        connectionOptions.AddTo(command);
        command.SetAction((pr, ct) => RunTestRlsAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            pr.GetValue(roleOption), pr.GetValue(userOption), pr.GetValue(queryOption), ct));
        return command;
    }

    private static async Task<int> RunTestRlsAsync(
        Func<DaxterConfig> configFactory, Func<string?> outputFactory,
        string? role, string? user, string? query, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            if (string.IsNullOrWhiteSpace(role) && string.IsNullOrWhiteSpace(user))
            {
                throw new DaxterException("Provide --role and/or --user to test RLS.");
            }

            var formatter = ResultFormatterFactory.Create(ResultFormatterFactory.Parse(outputFactory()));
            var factory = new AdomdXmlaSessionFactory(config, BuildTokenProvider(config), role, user);

            using var session = await factory.CreateAsync(ct);
            var dax = string.IsNullOrWhiteSpace(query)
                ? "EVALUATE ROW(\"EffectiveUser\", USERPRINCIPALNAME(), \"UserName\", USERNAME())"
                : query;

            var result = session.Execute(dax);
            Console.Error.WriteLine($"Under role='{role ?? "-"}' user='{user ?? "-"}':");
            Console.Out.Write(formatter.Format(result));
            Console.Error.WriteLine(RowSummary(result));
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    // ---- Workspace inventory (REST) ----

    private static Command BuildWorkspaceCommand(ConnectionOptions connectionOptions, Option<string> outputOption)
    {
        Command Sub(string name, string desc,
            Func<PowerBiRestClient, DaxterConfig, CancellationToken, Task<QueryResult>> op,
            bool requiresWorkspace = true)
        {
            var cmd = new Command(name, desc) { outputOption };
            connectionOptions.AddTo(cmd);
            cmd.SetAction((pr, ct) => RunRestQueryAsync(
                () => connectionOptions.Resolve(pr, requiresWorkspace), () => pr.GetValue(outputOption), op, ct));
            return cmd;
        }

        // Tenant-level — no workspace needed (matches the MCP daxter_workspaces / daxter_gateways tools).
        var ls = Sub("ls", "List workspaces (group ids).", (rest, _, ct) => rest.GroupsAsync(ct),
            requiresWorkspace: false);
        var datasets = Sub("datasets", "List datasets in the workspace.", async (rest, cfg, ct) =>
            await rest.DatasetsAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, ct), ct));
        var reports = Sub("reports", "List reports in the workspace.", async (rest, cfg, ct) =>
            await rest.ReportsAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, ct), ct));
        var lineage = Sub("lineage", "Report → dataset lineage.", async (rest, cfg, ct) =>
            await rest.LineageAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, ct), ct));
        var gateways = Sub("gateways", "List gateways (needs gateway admin).", (rest, _, ct) => rest.GatewaysAsync(ct),
            requiresWorkspace: false);
        var permissions = Sub("permissions", "Who has access (workspace, or a model via --dataset).",
            async (rest, cfg, ct) =>
            {
                var groupId = await rest.ResolveGroupIdAsync(cfg.Workspace, ct);
                if (string.IsNullOrWhiteSpace(cfg.Dataset))
                {
                    return await rest.WorkspaceUsersAsync(groupId, ct);
                }

                var datasetId = await rest.ResolveDatasetIdAsync(groupId, cfg.Dataset!, ct);
                return await rest.DatasetUsersAsync(groupId, datasetId, ct);
            });
        var datasources = Sub("datasources", "Data sources for a model (requires --dataset).",
            async (rest, cfg, ct) =>
            {
                RequireDataset(cfg);
                var groupId = await rest.ResolveGroupIdAsync(cfg.Workspace, ct);
                var datasetId = await rest.ResolveDatasetIdAsync(groupId, cfg.Dataset!, ct);
                return await rest.DatasourcesAsync(groupId, datasetId, ct);
            });

        return new Command("ws", "Workspace inventory (REST): datasets, reports, lineage, permissions, gateways.")
        {
            ls, datasets, reports, lineage, gateways, permissions, datasources,
        };
    }

    private static async Task<int> RunRestQueryAsync(
        Func<DaxterConfig> configFactory,
        Func<string?> outputFactory,
        Func<PowerBiRestClient, DaxterConfig, CancellationToken, Task<QueryResult>> op,
        CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            var formatter = ResultFormatterFactory.Create(ResultFormatterFactory.Parse(outputFactory()));
            using var rest = new PowerBiRestClient(BuildTokenProvider(config));
            var result = await op(rest, config, ct);

            Console.Out.Write(formatter.Format(result));
            Console.Error.WriteLine(RowSummary(result));
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static async Task<int> RunWebAsync(int port, CancellationToken ct)
    {
        var dll = Path.Combine(AppContext.BaseDirectory, "Daxter.Web.dll");
        if (!File.Exists(dll))
        {
            Console.Error.WriteLine($"daxter: web console not found at {dll}.");
            return 1;
        }

        Console.Error.WriteLine($"DAXter web console → http://localhost:{port}  (Ctrl+C to stop)");
        var psi = new ProcessStartInfo("dotnet") { UseShellExecute = false };
        psi.ArgumentList.Add(dll);
        psi.ArgumentList.Add("--urls");
        psi.ArgumentList.Add($"http://0.0.0.0:{port}");

        using var proc = Process.Start(psi)!;
        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
        }

        return proc.HasExited ? proc.ExitCode : 0;
    }

    private static ITokenProvider BuildTokenProvider(DaxterConfig config)
        => new MsalTokenProvider(config, deviceCodePrompt: WriteToStdErr);

    private static IXmlaSessionFactory BuildSessionFactory(DaxterConfig config)
        => new AdomdXmlaSessionFactory(config, BuildTokenProvider(config));

    // ---- Ops: refresh / cache ----

    private static Command BuildRefreshCommand(ConnectionOptions connectionOptions, Option<string> outputOption)
    {
        var tableOption = new Option<string?>("--table", "-t") { Description = "Table name." };
        var partitionOption = new Option<string?>("--partition", "-p") { Description = "Partition name (for refresh partition)." };
        var partitionsListOption = new Option<string?>("--partitions") { Description = "Comma-separated partition names (a subset) for refresh partitions; omit for all." };
        var typeOption = new Option<string?>("--type") { Description = "Refresh type: full|automatic|calculate|dataOnly|clearValues." };
        var orderOption = new Option<string?>("--order") { Description = "Partition order: newest-first (default) | oldest-first." };
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the command without executing." };
        var yes = new Option<bool>("--yes") { Description = "Execute the operation (required for mutations)." };
        var force = new Option<bool>("--force") { Description = "Allow execution against a PROD-looking target." };
        var topOption = new Option<int>("--top") { Description = "Rows of refresh history.", DefaultValueFactory = _ => 10 };

        var model = new Command("model", "Refresh the whole model (TMSL).") { typeOption, dryRun, yes, force };
        connectionOptions.AddTo(model);
        model.SetAction((pr, ct) => RunTmslAsync(() => connectionOptions.Resolve(pr),
            svc => svc.BuildModelRefresh(MaintenanceService.ParseRefreshType(pr.GetValue(typeOption))),
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));

        var table = new Command("table", "Refresh one table (TMSL).") { tableOption, typeOption, dryRun, yes, force };
        connectionOptions.AddTo(table);
        table.SetAction((pr, ct) => RunTmslAsync(() => connectionOptions.Resolve(pr),
            svc => svc.BuildTableRefresh(RequireOption(pr, tableOption, "--table"),
                MaintenanceService.ParseRefreshType(pr.GetValue(typeOption))),
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));

        var partition = new Command("partition", "Refresh one named partition of a table (TMSL).")
        {
            tableOption, partitionOption, typeOption, dryRun, yes, force,
        };
        connectionOptions.AddTo(partition);
        partition.SetAction((pr, ct) => RunTmslAsync(() => connectionOptions.Resolve(pr),
            svc => svc.BuildPartitionRefresh(
                RequireOption(pr, tableOption, "--table"),
                RequireOption(pr, partitionOption, "--partition"),
                MaintenanceService.ParseRefreshType(pr.GetValue(typeOption))),
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));

        var partitions = new Command("partitions", "Refresh a table's partitions in order, or a --partitions subset (TMSL, sequential).")
        {
            tableOption, partitionsListOption, orderOption, typeOption, dryRun, yes, force,
        };
        connectionOptions.AddTo(partitions);
        partitions.SetAction((pr, ct) => RunTmslAsync(() => connectionOptions.Resolve(pr),
            svc =>
            {
                var table = RequireOption(pr, tableOption, "--table");
                var type = MaintenanceService.ParseRefreshType(pr.GetValue(typeOption));
                var subset = pr.GetValue(partitionsListOption);
                // maxParallelism=1 → process in the listed order (a plain refresh runs in parallel).
                return string.IsNullOrWhiteSpace(subset)
                    ? svc.BuildPartitionsRefresh(table, ParseOrder(pr.GetValue(orderOption)), type, maxParallelism: 1)
                    : svc.BuildPartitionsRefresh(table,
                        subset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                        type, maxParallelism: 1);
            },
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));

        var trigger = new Command("trigger", "Trigger a model refresh via REST (no XMLA write needed).") { dryRun, yes, force };
        connectionOptions.AddTo(trigger);
        trigger.SetAction((pr, ct) => RunRefreshTriggerAsync(() => connectionOptions.Resolve(pr),
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));

        var history = new Command("history", "Show recent refresh history (REST).") { topOption, outputOption };
        connectionOptions.AddTo(history);
        history.SetAction((pr, ct) => RunRefreshHistoryAsync(() => connectionOptions.Resolve(pr),
            () => pr.GetValue(outputOption), pr.GetValue(topOption), ct));

        return new Command("refresh", "Refresh models / tables / partition(s); view history.")
        {
            model, table, partition, partitions, trigger, history,
        };
    }

    private static Command BuildCacheCommand(ConnectionOptions connectionOptions)
    {
        var dryRun = new Option<bool>("--dry-run") { Description = "Print the command without executing." };
        var yes = new Option<bool>("--yes") { Description = "Execute (required)." };
        var force = new Option<bool>("--force") { Description = "Allow execution against a PROD-looking target." };

        var clear = new Command("clear", "Clear the model's data cache (XMLA ClearCache).") { dryRun, yes, force };
        connectionOptions.AddTo(clear);
        clear.SetAction((pr, ct) => RunTmslAsync(() => connectionOptions.Resolve(pr),
            svc => svc.BuildClearCache(),
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));

        return new Command("cache", "Cache operations.") { clear };
    }

    private static async Task<int> RunTmslAsync(
        Func<DaxterConfig> configFactory,
        Func<MaintenanceService, string> build,
        bool dryRun, bool yes, bool force, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            var factory = BuildSessionFactory(config);

            using var session = await factory.CreateAsync(ct);
            var service = new MaintenanceService(session, config.Dataset!);
            var command = build(service);

            return ApplySafety(config, command, dryRun, yes, force, () => service.Execute(command));
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static async Task<int> RunRefreshTriggerAsync(
        Func<DaxterConfig> configFactory, bool dryRun, bool yes, bool force, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            using var rest = new PowerBiRestClient(BuildTokenProvider(config));

            var groupId = await rest.ResolveGroupIdAsync(config.Workspace, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, config.Dataset!, ct);
            var description = $"POST groups/{groupId}/datasets/{datasetId}/refreshes (full, REST)";

            return ApplySafety(config, description, dryRun, yes, force,
                () => rest.TriggerRefreshAsync(groupId, datasetId, ct).GetAwaiter().GetResult(),
                successMessage: "Refresh triggered (async). Check `daxter refresh history`.");
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static async Task<int> RunRefreshHistoryAsync(
        Func<DaxterConfig> configFactory, Func<string?> outputFactory, int top, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            var formatter = ResultFormatterFactory.Create(ResultFormatterFactory.Parse(outputFactory()));
            using var rest = new PowerBiRestClient(BuildTokenProvider(config));

            var groupId = await rest.ResolveGroupIdAsync(config.Workspace, ct);
            var datasetId = await rest.ResolveDatasetIdAsync(groupId, config.Dataset!, ct);
            var history = await rest.RefreshHistoryAsync(groupId, datasetId, top, ct);

            Console.Out.Write(formatter.Format(history));
            Console.Error.WriteLine(RowSummary(history));
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    private static int ApplySafety(
        DaxterConfig config, string command, bool dryRun, bool yes, bool force,
        Action execute, string successMessage = "Done.")
    {
        if (dryRun)
        {
            Console.Error.WriteLine("-- dry run; not executed --");
            Console.Out.WriteLine(command);
            return 0;
        }

        if (!yes)
        {
            Console.Out.WriteLine(command);
            Console.Error.WriteLine("Refusing to execute without --yes. Re-run with --yes (or --dry-run to preview).");
            return 0;
        }

        if (LooksLikeProd(config) && !force)
        {
            Console.Error.WriteLine(
                $"daxter: target looks like PRODUCTION ('{config.Workspace}'). Re-run with --force to proceed.");
            return 1;
        }

        execute();
        Console.Error.WriteLine(successMessage);
        return 0;
    }

    private static bool LooksLikeProd(DaxterConfig config) => config.IsProductionTarget();

    private static PartitionOrder ParseOrder(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "newest-first" or "newest" => PartitionOrder.NewestFirst,
        "oldest-first" or "oldest" => PartitionOrder.OldestFirst,
        _ => throw new DaxterException($"Unknown --order '{value}'. Use newest-first or oldest-first."),
    };

    private static string QueryTextFrom(
        ParseResult parseResult,
        Argument<string?> argument,
        Option<string?> fileOption)
    {
        var file = parseResult.GetValue(fileOption);
        if (!string.IsNullOrWhiteSpace(file))
        {
            if (!File.Exists(file))
            {
                throw new DaxterException($"Query file not found: {file}");
            }

            return File.ReadAllText(file);
        }

        var inline = parseResult.GetValue(argument);
        if (string.IsNullOrWhiteSpace(inline))
        {
            throw new DaxterException("No query provided. Pass query text or --file <path>.");
        }

        return inline;
    }

    private static void WriteToStdErr(string message) => Console.Error.WriteLine(message);

    private static int Fail(Exception ex)
    {
        if (ex is DaxterException or ArgumentException)
        {
            Console.Error.WriteLine($"daxter: {ex.Message}");
            return 1;
        }

        Console.Error.WriteLine($"daxter: unexpected error: {ex}");
        return 2;
    }
}
