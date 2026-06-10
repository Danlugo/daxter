using System.CommandLine;
using System.Diagnostics;
using Daxter.Core;
using Daxter.Core.Artifacts;
using Daxter.Core.Auth;
using Daxter.Core.Context;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Editing;
using Daxter.Core.Formatting;
using Daxter.Core.Maintenance;
using Daxter.Core.Metadata;
using Daxter.Core.Query;
using Daxter.Core.Rest;
using Daxter.Core.Scheduling;
using Daxter.Core.Sql;

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
        var loginTargetOpt = new Option<string?>("--target")
        {
            Description = "Scope to sign in for: powerbi (default — XMLA + REST) or sql " +
                          "(Fabric SQL endpoint, separate one-time sign-in — Power BI's client id isn't pre-authorized for database.windows.net).",
        };
        var loginCommand = new Command("login", "Sign in interactively and cache the token.") { loginTargetOpt };
        connectionOptions.AddTo(loginCommand);
        loginCommand.SetAction((parseResult, ct) => RunLoginAsync(
            () => connectionOptions.Resolve(parseResult, requireWorkspace: false),
            parseResult.GetValue(loginTargetOpt), ct));

        var modelCommand = BuildModelCommand(connectionOptions, outputOption);
        var envCommand = BuildEnvCommand();
        var refreshCommand = BuildRefreshCommand(connectionOptions, outputOption);
        var cacheCommand = BuildCacheCommand(connectionOptions);
        var wsCommand = BuildWorkspaceCommand(connectionOptions, outputOption);
        var testRlsCommand = BuildTestRlsCommand(connectionOptions, outputOption);
        var pipelineCommand = BuildPipelineCommand(connectionOptions, outputOption);
        var sqlCommand = BuildSqlCommand(connectionOptions, outputOption, fileOption);
        var fabricCommand = BuildFabricCommand(connectionOptions, outputOption);
        var artifactCommand = BuildArtifactCommand(outputOption);
        var contextCommand = BuildContextCommand(outputOption);

        var mcpCommand = new Command("mcp", "Run DAXter as a Model Context Protocol (stdio) server.");
        mcpCommand.SetAction((_, ct) => Mcp.McpServer.RunAsync(ct));

        var webPort = new Option<int>("--port") { Description = "Port for the web console.", DefaultValueFactory = _ => 8080 };
        var webCommand = new Command("web", "Run the DAXter web console (status, configure, explore).") { webPort };
        webCommand.SetAction((pr, ct) => RunWebAsync(pr.GetValue(webPort), ct));

        var root = new RootCommand(
            "DAXter — Power BI Service CLI: query, model metadata, maintenance, and inventory.")
        {
            queryCommand, dmvCommand, lsCommand, loginCommand, modelCommand, envCommand,
            refreshCommand, cacheCommand, wsCommand, testRlsCommand, pipelineCommand, sqlCommand, fabricCommand,
            artifactCommand, contextCommand, mcpCommand, webCommand,
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

    private static async Task<int> RunLoginAsync(Func<DaxterConfig> configFactory, string? target, CancellationToken ct)
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
                PublicClientId = config.PublicClientId,
                FabricSqlClientId = config.FabricSqlClientId,
                AuthMode = AuthMode.DeviceCode,
            };

            var provider = new MsalTokenProvider(interactive, deviceCodePrompt: WriteToStdErr);
            var forSql = string.Equals(target, "sql", StringComparison.OrdinalIgnoreCase);
            // GetTokenAsync (XMLA scope) is the existing behaviour; GetFabricSqlTokenAsync uses the
            // SQL-side client id and a separate cache slice — needed once because of AADSTS65002.
            var token = forSql
                ? await provider.GetFabricSqlTokenAsync(ct)
                : await provider.GetTokenAsync(ct);
            var which = forSql ? "Fabric SQL" : "Power BI";
            Console.Error.WriteLine($"Signed in to {which}. Token valid until {token.ExpiresOn:u}.");
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
        var columnsOpt = new Option<string?>("--columns") { Description = "Import-table columns: 'Name:dataType:sourceColumn', comma-separated." };
        var fromTable = new Option<string?>("--from-table") { Description = "Relationship 'from' (many-side) table." };
        var fromColumn = new Option<string?>("--from-column") { Description = "Relationship 'from' column." };
        var toTable = new Option<string?>("--to-table") { Description = "Relationship 'to' (one-side) table." };
        var toColumn = new Option<string?>("--to-column") { Description = "Relationship 'to' column." };
        var sortBy = new Option<string?>("--sort-by") { Description = "Sort this column by another column on the same table (empty to clear)." };
        var summarizeBy = new Option<string?>("--summarize-by") { Description = "Default summarization: none|sum|average|min|max|count|distinctCount." };
        var hidden = new Option<bool?>("--hidden") { Description = "Hide the column from report view (true|false)." };
        var dataCategory = new Option<string?>("--data-category") { Description = "Data category, e.g. Years|Months|Place|WebUrl." };
        var crossFilter = new Option<string?>("--cross-filter") { Description = "Cross-filter: single | both | automatic (default single)." };
        var active = new Option<bool>("--active") { Description = "Relationship is active (default true).", DefaultValueFactory = _ => true };

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
            Edit("edit-column", "Edit an existing column's properties (format, data type, sort-by, summarize-by, folder, hidden, …).",
                (pr, svc) => svc.EditColumn(RequireOption(pr, table, "--table"), RequireOption(pr, name, "--name"),
                    pr.GetValue(format), pr.GetValue(dataType), pr.GetValue(sortBy), pr.GetValue(summarizeBy),
                    pr.GetValue(folder), pr.GetValue(desc), pr.GetValue(hidden), pr.GetValue(dataCategory)),
                table, name, format, dataType, sortBy, summarizeBy, folder, desc, hidden, dataCategory),
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
            Edit("import-table", "Create or replace an import table (M source + typed columns).",
                (pr, svc) => svc.CreateImportTable(RequireOption(pr, name, "--name"), RequireOption(pr, m, "--m"),
                    RequireOption(pr, columnsOpt, "--columns")
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(part => part.Split(':', StringSplitOptions.TrimEntries))
                        .Select(p => p.Length >= 3
                            ? new SourceColumn(p[0], p[1], p[2])
                            : throw new DaxterException("Bad --columns spec. Use Name:dataType:sourceColumn (comma-separated)."))),
                name, m, columnsOpt),
            Edit("relationship", "Create or alter a relationship: fromTable[fromColumn] -> toTable[toColumn] (many-to-one).",
                (pr, svc) => svc.UpsertRelationship(
                    RequireOption(pr, fromTable, "--from-table"), RequireOption(pr, fromColumn, "--from-column"),
                    RequireOption(pr, toTable, "--to-table"), RequireOption(pr, toColumn, "--to-column"),
                    pr.GetValue(name), pr.GetValue(crossFilter), pr.GetValue(active)),
                fromTable, fromColumn, toTable, toColumn, name, crossFilter, active),
            Edit("delete-relationship", "Delete a relationship by name.",
                (pr, svc) => svc.DeleteRelationship(RequireOption(pr, name, "--name")),
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
        var reportInventory = Sub("report-inventory", "Classify reports: thin/thick/paginated + downloadable (.pbix).",
            async (rest, cfg, ct) => await rest.ReportInventoryAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, ct), ct));
        var gateways = Sub("gateways", "List gateways (needs gateway admin).", (rest, _, ct) => rest.GatewaysAsync(ct),
            requiresWorkspace: false);
        var connections = Sub("connections", "List all connections you can access (cloud + gateway), via the Fabric API.",
            (rest, _, ct) => rest.ConnectionsAsync(ct), requiresWorkspace: false);
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

        // ---- take ownership + gateway binding (service config; XMLA can't do these) ----
        var itemConnections = Sub("item-connections", "A model's connections + names/connectivity (Fabric API; requires --dataset).",
            async (rest, cfg, ct) =>
            {
                RequireDataset(cfg);
                var g = await rest.ResolveGroupIdAsync(cfg.Workspace, ct);
                var d = await rest.ResolveDatasetIdAsync(g, cfg.Dataset!, ct);
                return await rest.ItemConnectionsAsync(g, d, ct);
            });

        var discoverGateways = Sub("discover-gateways", "Gateways a model can bind to (requires --dataset).",
            async (rest, cfg, ct) =>
            {
                RequireDataset(cfg);
                var g = await rest.ResolveGroupIdAsync(cfg.Workspace, ct);
                var d = await rest.ResolveDatasetIdAsync(g, cfg.Dataset!, ct);
                return await rest.DiscoverGatewaysAsync(g, d, ct);
            });

        var gwDsOpt = new Option<string>("--gateway") { Description = "Gateway id (GUID)." };
        var gatewayDatasources = new Command("gateway-datasources", "List a gateway's data sources (--gateway).")
            { gwDsOpt, outputOption };
        gatewayDatasources.SetAction((pr, ct) => RunRestQueryAsync(
            () => connectionOptions.Resolve(pr, requireWorkspace: false),
            () => pr.GetValue(outputOption),
            (rest, _, c) => rest.GatewayDatasourcesAsync(
                pr.GetValue(gwDsOpt) ?? throw new DaxterException("--gateway is required."), c), ct));

        var yesOpt = new Option<bool>("--yes") { Description = "Apply the change (omit for a dry run)." };

        var takeover = new Command("takeover", "Take over ownership of a model (requires --dataset).") { yesOpt };
        connectionOptions.AddTo(takeover);
        takeover.SetAction((pr, ct) => RunRestActionAsync(
            () => connectionOptions.Resolve(pr), pr.GetValue(yesOpt),
            cfg => $"take over '{cfg.Dataset}' in '{cfg.Workspace}'",
            async (rest, cfg, c) =>
            {
                RequireDataset(cfg);
                var g = await rest.ResolveGroupIdAsync(cfg.Workspace, c);
                var d = await rest.ResolveDatasetIdAsync(g, cfg.Dataset!, c);
                await rest.TakeOverAsync(g, d, c);
                return $"Took over '{cfg.Dataset}' — you are now the owner.";
            }, ct));

        var bindGwOpt = new Option<string>("--gateway") { Description = "Gateway id (GUID) — from `ws discover-gateways`." };
        var bindDsOpt = new Option<string>("--datasources") { Description = "Comma-separated gateway connection ids to map (optional)." };
        var bindGateway = new Command("bind-gateway", "Bind a model to a gateway (requires --dataset, --gateway).")
            { bindGwOpt, bindDsOpt, yesOpt };
        connectionOptions.AddTo(bindGateway);
        bindGateway.SetAction((pr, ct) => RunRestActionAsync(
            () => connectionOptions.Resolve(pr), pr.GetValue(yesOpt),
            cfg => $"bind '{cfg.Dataset}' to gateway {pr.GetValue(bindGwOpt)}",
            async (rest, cfg, c) =>
            {
                RequireDataset(cfg);
                var gw = pr.GetValue(bindGwOpt) ?? throw new DaxterException("--gateway is required.");
                var raw = pr.GetValue(bindDsOpt);
                var ids = string.IsNullOrWhiteSpace(raw) ? null
                    : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var g = await rest.ResolveGroupIdAsync(cfg.Workspace, c);
                var d = await rest.ResolveDatasetIdAsync(g, cfg.Dataset!, c);
                await rest.BindToGatewayAsync(g, d, gw, ids, c);
                return $"Bound '{cfg.Dataset}' to gateway {gw}" + (ids is { Length: > 0 } ? $" — {ids.Length} data source(s) mapped." : ".");
            }, ct));

        var reportOpt = new Option<string?>("--report") { Description = "Report name or id (for export-report)." };
        var outDirOpt = new Option<string?>("--out") { Description = "Directory to write the report definition / .pbix into (prints a manifest if omitted)." };
        var pbixOpt = new Option<bool>("--pbix") { Description = "Also download the .pbix (Export Report In Group)." };
        var exportReport = new Command("export-report",
            "Export a report's definition (PBIR / legacy JSON — the field references for analysis) and optionally its .pbix.")
            { reportOpt, outDirOpt, pbixOpt };
        connectionOptions.AddTo(exportReport);
        exportReport.SetAction((pr, ct) => RunExportReportAsync(
            () => connectionOptions.Resolve(pr, requireWorkspace: true),
            pr.GetValue(reportOpt), pr.GetValue(outDirOpt), pr.GetValue(pbixOpt), ct));

        var bcSourceType = new Option<string?>("--source-type") { Description = "Data source type (SQL | Snowflake | … — from `ws item-connections`)." };
        var bcSourcePath = new Option<string?>("--source-path") { Description = "Data source path identifying the source, e.g. 'server;database'." };
        var bcConnType = new Option<string?>("--connectivity") { Description = "ShareableCloud | OnPremisesGateway | VirtualNetworkGateway | PersonalCloud | Automatic | None." };
        var bcConnId = new Option<string?>("--connection") { Description = "Target connection id (from `ws connections`); omit for Automatic/None." };
        var bindConnection = new Command("bind-connection",
            "Bind ONE data source to a connection (cloud / gateway / SSO) — per source, via the Fabric bindConnection API.")
            { bcSourceType, bcSourcePath, bcConnType, bcConnId, yesOpt };
        connectionOptions.AddTo(bindConnection);
        bindConnection.SetAction((pr, ct) => RunRestActionAsync(
            () => connectionOptions.Resolve(pr), pr.GetValue(yesOpt),
            cfg => $"bind {pr.GetValue(bcSourceType)} source '{pr.GetValue(bcSourcePath)}' of '{cfg.Dataset}' to {pr.GetValue(bcConnType)}",
            async (rest, cfg, c) =>
            {
                RequireDataset(cfg);
                var g = await rest.ResolveGroupIdAsync(cfg.Workspace, c);
                var d = await rest.ResolveDatasetIdAsync(g, cfg.Dataset!, c);
                await rest.BindConnectionAsync(g, d, pr.GetValue(bcConnId),
                    RequireOption(pr, bcConnType, "--connectivity"),
                    RequireOption(pr, bcSourceType, "--source-type"),
                    RequireOption(pr, bcSourcePath, "--source-path"), c);
                return $"Bound {pr.GetValue(bcSourceType)} source to {pr.GetValue(bcConnType)}.";
            }, ct));

        return new Command("ws", "Workspace inventory (REST) + ownership/gateway binding: datasets, reports, lineage, permissions, gateways, takeover, bind-gateway, bind-connection.")
        {
            ls, datasets, reports, lineage, reportInventory, gateways, connections, permissions, datasources,
            itemConnections, discoverGateways, gatewayDatasources, exportReport, takeover, bindGateway, bindConnection,
        };
    }

    private static async Task<int> RunExportReportAsync(
        Func<DaxterConfig> configFactory, string? report, string? outDir, bool pbix, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(report)) throw new DaxterException("--report is required.");
            var config = configFactory();
            var tokens = BuildTokenProvider(config);
            using var rest = new PowerBiRestClient(tokens);
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace, ct);
            var reportId = await rest.ResolveReportIdAsync(groupId, report, ct);

            var parts = await rest.ReportDefinitionAsync(groupId, reportId, ct);
            Console.Error.WriteLine($"Definition: {parts.Count} part(s).");

            if (string.IsNullOrWhiteSpace(outDir))
            {
                foreach (var p in parts)
                    Console.Out.WriteLine($"{p.Content.Length,8}  {p.Path}");
            }
            else
            {
                var baseDir = Path.Combine(outDir, SanitizeFileName(report));
                foreach (var p in parts)
                {
                    var dest = Path.Combine(baseDir, p.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    await File.WriteAllTextAsync(dest, p.Content, ct);
                }
                Console.Error.WriteLine($"Wrote {parts.Count} part(s) to {baseDir}");
            }

            if (pbix)
            {
                var bytes = await rest.ExportReportPbixAsync(groupId, reportId, ct);
                var pbixPath = Path.Combine(string.IsNullOrWhiteSpace(outDir) ? "." : outDir, SanitizeFileName(report) + ".pbix");
                await File.WriteAllBytesAsync(pbixPath, bytes, ct);
                Console.Error.WriteLine($"Wrote {bytes.Length:N0} bytes to {pbixPath}");
            }
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static string SanitizeFileName(string name)
        => string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>Runs a REST write that returns a status line. Dry-run unless <paramref name="yes"/>.</summary>
    private static async Task<int> RunRestActionAsync(
        Func<DaxterConfig> configFactory, bool yes,
        Func<DaxterConfig, string> planFactory,
        Func<PowerBiRestClient, DaxterConfig, CancellationToken, Task<string>> action,
        CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            if (!yes)
            {
                Console.Out.WriteLine($"DRY RUN — would {planFactory(config)}. Pass --yes to apply.");
                return 0;
            }

            using var rest = new PowerBiRestClient(BuildTokenProvider(config));
            Console.Out.WriteLine(await action(rest, config, ct));
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
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

    private static MsalTokenProvider BuildMsalProvider(DaxterConfig config)
        => new MsalTokenProvider(config, deviceCodePrompt: WriteToStdErr);

    // ---- Fabric items: Copy Jobs + Notebooks (view + run + monitor) ----

    private static Command BuildFabricCommand(ConnectionOptions connectionOptions, Option<string> outputOption)
    {
        Command List(string name, string desc,
            Func<PowerBiRestClient, DaxterConfig, CancellationToken, Task<QueryResult>> op)
        {
            var cmd = new Command(name, desc) { outputOption };
            connectionOptions.AddTo(cmd);
            cmd.SetAction((pr, ct) => RunRestQueryAsync(
                () => connectionOptions.Resolve(pr, requireWorkspace: true),
                () => pr.GetValue(outputOption), op, ct));
            return cmd;
        }

        // --- copy-jobs ---
        var cjLs = List("ls", "List Copy Jobs in the workspace.",
            async (rest, cfg, c) => await rest.CopyJobsAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, c), c));

        var cjIdOpt = new Option<string>("--copy-job") { Description = "Copy Job item id (GUID; from `fabric copy-jobs ls`)." };
        var cjShow = new Command("show", "Show a Copy Job's definition (the copyjob-content.json).") { cjIdOpt };
        connectionOptions.AddTo(cjShow);
        cjShow.SetAction((pr, ct) => RunFabricDefinitionAsync(
            () => connectionOptions.Resolve(pr), kind: "copyJob",
            itemId: pr.GetValue(cjIdOpt) ?? throw new DaxterException("--copy-job is required."),
            format: null, ct));

        var yesOpt = new Option<bool>("--yes") { Description = "Actually run / cancel (omit for a dry run)." };
        var cjRun = new Command("run", "Start a Copy Job on demand (dry-run unless --yes).") { cjIdOpt, yesOpt };
        connectionOptions.AddTo(cjRun);
        cjRun.SetAction((pr, ct) => RunItemJobActionAsync(
            () => connectionOptions.Resolve(pr),
            itemId: pr.GetValue(cjIdOpt) ?? throw new DaxterException("--copy-job is required."),
            jobType: "Execute", execute: pr.GetValue(yesOpt), ct: ct));

        // `runs` command — can't use the List() helper because we need to reach the --copy-job
        // option inside the op lambda, which the helper doesn't expose. Wire it explicitly.
        var cjRunsCmd = new Command("runs", "List recent run instances for a Copy Job.") { cjIdOpt, outputOption };
        connectionOptions.AddTo(cjRunsCmd);
        cjRunsCmd.SetAction((pr, ct) => RunRestQueryAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            async (rest, cfg, c) => await rest.ListItemJobInstancesAsync(
                await rest.ResolveGroupIdAsync(cfg.Workspace, c),
                pr.GetValue(cjIdOpt) ?? throw new DaxterException("--copy-job is required."), c), ct));

        var cjInstOpt = new Option<string>("--instance") { Description = "Job instance id." };
        var cjCancel = new Command("cancel", "Cancel a running Copy Job instance (dry-run unless --yes).")
            { cjIdOpt, cjInstOpt, yesOpt };
        connectionOptions.AddTo(cjCancel);
        cjCancel.SetAction((pr, ct) => RunCancelItemJobAsync(
            () => connectionOptions.Resolve(pr),
            itemId: pr.GetValue(cjIdOpt) ?? throw new DaxterException("--copy-job is required."),
            instanceId: pr.GetValue(cjInstOpt) ?? throw new DaxterException("--instance is required."),
            execute: pr.GetValue(yesOpt), ct));

        var copyJobs = new Command("copy-jobs", "Fabric Copy Jobs — list, view, run, monitor.")
            { cjLs, cjShow, cjRun, cjRunsCmd, cjCancel };

        // --- notebooks ---
        var nbLs = List("ls", "List Notebooks in the workspace.",
            async (rest, cfg, c) => await rest.NotebooksAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, c), c));

        var nbIdOpt = new Option<string>("--notebook") { Description = "Notebook item id (GUID; from `fabric notebooks ls`)." };
        var nbFormatOpt = new Option<string?>("--format")
            { Description = "Definition format: \"ipynb\" (default — standard Jupyter) or \"FabricGitSource\" (language-specific source file)." };
        var nbShow = new Command("show", "Show a Notebook's definition.") { nbIdOpt, nbFormatOpt };
        connectionOptions.AddTo(nbShow);
        nbShow.SetAction((pr, ct) => RunFabricDefinitionAsync(
            () => connectionOptions.Resolve(pr), kind: "notebook",
            itemId: pr.GetValue(nbIdOpt) ?? throw new DaxterException("--notebook is required."),
            format: pr.GetValue(nbFormatOpt) ?? "ipynb", ct));

        var nbExecOpt = new Option<string?>("--execution-data")
            { Description = "JSON payload for parameterized run (see Fabric Job Scheduler docs)." };
        var nbRun = new Command("run", "Start a Notebook on demand (dry-run unless --yes).") { nbIdOpt, nbExecOpt, yesOpt };
        connectionOptions.AddTo(nbRun);
        nbRun.SetAction((pr, ct) => RunItemJobActionAsync(
            () => connectionOptions.Resolve(pr),
            itemId: pr.GetValue(nbIdOpt) ?? throw new DaxterException("--notebook is required."),
            jobType: "RunNotebook", execute: pr.GetValue(yesOpt),
            executionData: pr.GetValue(nbExecOpt), ct: ct));

        var nbRunsCmd = new Command("runs", "List recent run instances for a Notebook.") { nbIdOpt, outputOption };
        connectionOptions.AddTo(nbRunsCmd);
        nbRunsCmd.SetAction((pr, ct) => RunRestQueryAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            async (rest, cfg, c) => await rest.ListItemJobInstancesAsync(
                await rest.ResolveGroupIdAsync(cfg.Workspace, c),
                pr.GetValue(nbIdOpt) ?? throw new DaxterException("--notebook is required."), c), ct));

        var nbCancel = new Command("cancel", "Cancel a running Notebook instance (dry-run unless --yes).")
            { nbIdOpt, cjInstOpt, yesOpt };
        connectionOptions.AddTo(nbCancel);
        nbCancel.SetAction((pr, ct) => RunCancelItemJobAsync(
            () => connectionOptions.Resolve(pr),
            itemId: pr.GetValue(nbIdOpt) ?? throw new DaxterException("--notebook is required."),
            instanceId: pr.GetValue(cjInstOpt) ?? throw new DaxterException("--instance is required."),
            execute: pr.GetValue(yesOpt), ct));

        var notebooks = new Command("notebooks", "Fabric Notebooks — list, view, run, monitor.")
            { nbLs, nbShow, nbRun, nbRunsCmd, nbCancel };

        return new Command("fabric", "Fabric items — Copy Jobs, Notebooks (view, run, monitor).") { copyJobs, notebooks };
    }

    /// <summary>Prints a Fabric item's definition to stdout — joining all parts (rare; usually 1) with
    /// a <c># ----- path</c> marker so multi-part definitions stay distinguishable.</summary>
    private static async Task<int> RunFabricDefinitionAsync(
        Func<DaxterConfig> configFactory, string kind, string itemId, string? format, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            using var rest = new PowerBiRestClient(BuildTokenProvider(config));
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace, ct);
            var parts = kind switch
            {
                "copyJob" => await rest.CopyJobDefinitionAsync(groupId, itemId, ct),
                "notebook" => await rest.NotebookDefinitionAsync(groupId, itemId, format, ct),
                _ => throw new DaxterException($"Unknown item kind: {kind}"),
            };
            if (parts.Count == 0) { Console.Error.WriteLine("(empty definition)"); return 0; }
            for (var i = 0; i < parts.Count; i++)
            {
                if (parts.Count > 1) Console.Out.WriteLine($"# ----- {parts[i].Path}");
                Console.Out.Write(parts[i].Content);
                if (!parts[i].Content.EndsWith('\n')) Console.Out.WriteLine();
            }
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunItemJobActionAsync(
        Func<DaxterConfig> configFactory, string itemId, string jobType, bool execute,
        string? executionData = null, CancellationToken ct = default)
    {
        try
        {
            var config = configFactory();
            if (!execute)
            {
                Console.Error.WriteLine($"DRY RUN — would start jobType={jobType} on item {itemId} in '{config.Workspace}'. Pass --yes to actually run.");
                return 0;
            }
            using var rest = new PowerBiRestClient(BuildTokenProvider(config));
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace, ct);
            var instanceId = await rest.StartItemJobAsync(groupId, itemId, jobType, executionData, ct);
            Console.Error.WriteLine($"Started — instanceId: {instanceId}. Use `runs --{(jobType == "Execute" ? "copy-job" : "notebook")} {itemId}` to track it.");
            Console.Out.WriteLine(instanceId);
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunCancelItemJobAsync(
        Func<DaxterConfig> configFactory, string itemId, string instanceId, bool execute, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            if (!execute)
            {
                Console.Error.WriteLine($"DRY RUN — would cancel instance {instanceId} on item {itemId} in '{config.Workspace}'. Pass --yes to actually cancel.");
                return 0;
            }
            using var rest = new PowerBiRestClient(BuildTokenProvider(config));
            var groupId = await rest.ResolveGroupIdAsync(config.Workspace, ct);
            await rest.CancelItemJobInstanceAsync(groupId, itemId, instanceId, ct);
            Console.Error.WriteLine($"Cancellation requested for instance {instanceId}. Next status check should report 'Cancelled'.");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    // ---- SQL: Fabric Warehouse / Lakehouse SQL endpoint ----

    private static Command BuildSqlCommand(
        ConnectionOptions connectionOptions, Option<string> outputOption, Option<string?> fileOption)
    {
        // daxter sql endpoints --workspace W
        // Lists Warehouses + Lakehouse SQL endpoints in a workspace (so users see real options, not
        // GUID hostnames). Read-only Fabric REST — no SQL token needed for discovery.
        var endpoints = new Command("endpoints", "List Fabric SQL endpoints in a workspace (Warehouses + Lakehouse SQL).")
            { outputOption };
        connectionOptions.AddTo(endpoints);
        endpoints.SetAction((pr, ct) => RunRestQueryAsync(
            () => connectionOptions.Resolve(pr, requireWorkspace: true),
            () => pr.GetValue(outputOption),
            async (rest, cfg, c) => await rest.SqlEndpointsAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, c), c),
            ct));

        // daxter sql query --workspace W --endpoint NAME --query "SELECT TOP 10 …" [--file path]
        // Runs T-SQL on the SQL endpoint named NAME (matched against the discovery list, so the user
        // types the warehouse/lakehouse name, not its GUID hostname). Reuses the user's signed-in
        // MSAL account for a silent database.windows.net token. Read-only by default; --allow-writes
        // (matching the Web 'Allow writes' gate) lets non-SELECT statements through.
        var endpointOpt = new Option<string?>("--endpoint")
            { Description = "Warehouse or Lakehouse name (from `sql endpoints`)." };
        var serverOpt = new Option<string?>("--server")
            { Description = "Override server hostname (skips discovery — e.g. <ws>.datawarehouse.fabric.microsoft.com)." };
        var databaseOpt = new Option<string?>("--database")
            { Description = "Override database name (warehouse / lakehouse) — used with --server." };
        var queryArg = new Argument<string?>("query")
            { Description = "T-SQL to execute. Optional when --file is used.", Arity = ArgumentArity.ZeroOrOne };
        var allowWritesOpt = new Option<bool>("--allow-writes")
            { Description = "Allow non-SELECT statements (INSERT/UPDATE/DELETE/MERGE/DDL/EXEC/…)." };
        var outFileOpt = new Option<string?>("--out")
            { Description = "Stream the full result set as CSV to this file path (no in-memory materialization — safe for SELECT * on huge tables). When set, --output is ignored." };
        var quoteAllOpt = new Option<bool>("--quote-all")
            { Description = "Wrap every CSV field in quotes (matches Power BI / Excel \"Export data\" style). Default off = RFC 4180 (quote-when-needed). Only meaningful with --out." };
        var crlfOpt = new Option<bool>("--crlf")
            { Description = "End each CSV line with CRLF (\\r\\n) instead of LF (\\n). Excel-on-Windows convention. Only meaningful with --out." };
        var artifactKeyOpt = new Option<string?>("--artifact-key")
            { Description = "Also mirror the exported CSV into the artifact store under this key (e.g. 'sql/sales-2026.csv'). Fetch via `daxter artifact get` / the /artifacts page / GET /api/artifacts. Only meaningful with --out." };

        var query = new Command("query", "Run T-SQL on a Fabric SQL endpoint.")
            { endpointOpt, serverOpt, databaseOpt, queryArg, fileOption, outputOption, allowWritesOpt, outFileOpt, quoteAllOpt, crlfOpt, artifactKeyOpt };
        connectionOptions.AddTo(query);
        query.SetAction((pr, ct) => RunSqlQueryAsync(
            () => connectionOptions.Resolve(pr, requireWorkspace: pr.GetValue(serverOpt) is null),
            () => QueryTextFrom(pr, queryArg, fileOption),
            pr.GetValue(endpointOpt),
            pr.GetValue(serverOpt),
            pr.GetValue(databaseOpt),
            () => pr.GetValue(outputOption),
            pr.GetValue(allowWritesOpt),
            pr.GetValue(outFileOpt),
            pr.GetValue(quoteAllOpt),
            pr.GetValue(crlfOpt),
            pr.GetValue(artifactKeyOpt),
            ct));

        // daxter sql objects --workspace W --endpoint NAME
        // Lists schemas + tables/views/functions/stored procedures in one INFORMATION_SCHEMA round-trip.
        // No GUID hostname required — endpoint name from `sql endpoints` resolves to (server, database).
        var objectsEndpointOpt = new Option<string?>("--endpoint")
            { Description = "Warehouse or Lakehouse name (from `sql endpoints`)." };
        var objectsServerOpt = new Option<string?>("--server")
            { Description = "Override server hostname (skips discovery)." };
        var objectsDatabaseOpt = new Option<string?>("--database")
            { Description = "Override database name (used with --server)." };
        var objects = new Command("objects", "List schemas + tables / views / functions / stored procedures.")
            { objectsEndpointOpt, objectsServerOpt, objectsDatabaseOpt, outputOption };
        connectionOptions.AddTo(objects);
        objects.SetAction((pr, ct) => RunSqlObjectsAsync(
            () => connectionOptions.Resolve(pr, requireWorkspace: pr.GetValue(objectsServerOpt) is null),
            pr.GetValue(objectsEndpointOpt),
            pr.GetValue(objectsServerOpt),
            pr.GetValue(objectsDatabaseOpt),
            () => pr.GetValue(outputOption),
            ct));

        return new Command("sql",
            "Fabric Warehouse / Lakehouse SQL endpoint — list endpoints, browse objects, run T-SQL.")
        {
            endpoints, objects, query,
        };
    }

    /// <summary>Lists schemas + tables/views/functions/stored procedures on a Fabric SQL endpoint. Same
    /// INFORMATION_SCHEMA round-trip the Web /sql tree uses; resolves endpoint NAME → (server, database)
    /// via the discovery list so callers don't have to know the GUID hostname.</summary>
    private static async Task<int> RunSqlObjectsAsync(
        Func<DaxterConfig> configFactory,
        string? endpointName, string? server, string? database,
        Func<string?> outputFactory, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            var msal = BuildMsalProvider(config);

            if (string.IsNullOrWhiteSpace(server))
            {
                if (string.IsNullOrWhiteSpace(endpointName))
                    throw new DaxterException("Pass --endpoint <name> (from `daxter sql endpoints`) or --server + --database.");
                using var rest = new PowerBiRestClient(msal);
                var groupId = await rest.ResolveGroupIdAsync(config.Workspace, ct);
                var list = await rest.SqlEndpointsAsync(groupId, ct);
                var match = list.Rows.FirstOrDefault(r =>
                    string.Equals(r[0]?.ToString(), endpointName, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    throw new DaxterException(
                        $"Endpoint '{endpointName}' not found in '{config.Workspace}'. Run `daxter sql endpoints` to see what's available.");
                server = match[1]?.ToString();
                database ??= match[2]?.ToString();
            }
            if (string.IsNullOrWhiteSpace(database))
                throw new DaxterException("--database is required when --server is passed without --endpoint.");

            var client = new FabricSqlClient(msal);
            var result = await client.ListObjectsAsync(server!, database!, ct);

            var format = ResultFormatterFactory.Parse(outputFactory());
            Console.Out.Write(ResultFormatterFactory.Create(format).Format(result));
            Console.Error.WriteLine($"({result.RowCount} row{(result.RowCount == 1 ? "" : "s")})");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    /// <summary>Runs a T-SQL query against a Fabric SQL endpoint. Resolves the (server, database)
    /// either from <paramref name="server"/>/<paramref name="database"/> (explicit override) or by
    /// looking up <paramref name="endpointName"/> in the workspace's endpoint discovery list — so the
    /// user can pass a friendly name instead of the GUID hostname. When <paramref name="outFile"/> is
    /// set, the result set is STREAMED as CSV straight to that file path (no in-memory
    /// materialization) — safe for <c>SELECT *</c> on multi-million-row tables.</summary>
    private static async Task<int> RunSqlQueryAsync(
        Func<DaxterConfig> configFactory, Func<string> sqlFactory,
        string? endpointName, string? server, string? database,
        Func<string?> outputFactory, bool allowWrites, string? outFile,
        bool quoteAll, bool crlf, string? artifactKey, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            var sql = sqlFactory();
            var msal = BuildMsalProvider(config);

            // Resolve endpoint -> (server, database).
            if (string.IsNullOrWhiteSpace(server))
            {
                if (string.IsNullOrWhiteSpace(endpointName))
                    throw new DaxterException("Pass --endpoint <name> (from `daxter sql endpoints`) or --server + --database.");
                using var rest = new PowerBiRestClient(msal);
                var groupId = await rest.ResolveGroupIdAsync(config.Workspace, ct);
                var list = await rest.SqlEndpointsAsync(groupId, ct);
                var match = list.Rows.FirstOrDefault(r =>
                    string.Equals(r[0]?.ToString(), endpointName, StringComparison.OrdinalIgnoreCase));
                if (match is null)
                    throw new DaxterException(
                        $"Endpoint '{endpointName}' not found in '{config.Workspace}'. Run `daxter sql endpoints` to see what's available.");
                server = match[1]?.ToString();
                database ??= match[2]?.ToString();
            }
            if (string.IsNullOrWhiteSpace(database))
                throw new DaxterException("--database is required when --server is passed without --endpoint.");

            var client = new FabricSqlClient(msal);

            // --out triggers the streaming path: no in-memory QueryResult. Stays low-memory regardless
            // of row count; the grid in --output table mode would OOM on millions of rows.
            if (!string.IsNullOrWhiteSpace(outFile))
            {
                var style = new Daxter.Core.Formatting.CsvStyle(QuoteAll: quoteAll, Crlf: crlf);
                long rows;
                await using (var fs = new FileStream(outFile, FileMode.Create, FileAccess.Write, FileShare.Read))
                await using (var sw = new StreamWriter(fs))
                {
                    rows = await client.StreamCsvAsync(server!, database!, sql, allowWrites, sw, ct, style: style);
                }

                // Optional mirror into the artifact store — same source-tool stamp the MCP path
                // uses, so the /artifacts page can show "produced by sql_export" regardless of
                // which surface wrote it.
                if (!string.IsNullOrWhiteSpace(artifactKey))
                {
                    var store = new LocalArtifactStore();
                    await using var src = File.OpenRead(outFile);
                    var aref = await store.PutAsync(artifactKey, src,
                        new ArtifactMeta(SourceTool: "daxter_sql_export"), ct);
                    Console.Error.WriteLine($"Mirrored to artifact '{aref.Key}' ({aref.Bytes:N0} bytes).");
                }
                Console.Error.WriteLine($"Wrote {rows} row{(rows == 1 ? "" : "s")} to {outFile}" +
                    (quoteAll || crlf ? $" (QuoteAll:{quoteAll}, CRLF:{crlf})." : "."));
                return 0;
            }

            var result = await client.ExecuteAsync(server!, database!, sql, allowWrites, ct);

            var format = ResultFormatterFactory.Parse(outputFactory());
            Console.Out.Write(ResultFormatterFactory.Create(format).Format(result));
            Console.Error.WriteLine($"({result.RowCount} row{(result.RowCount == 1 ? "" : "s")})");
            return 0;
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

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
        var retriesOption = new Option<int>("--retries") { Description = "Retry on transient failure: number of extra attempts (default 0; e.g. 3). Linear backoff." };

        var model = new Command("model", "Queue a whole-model refresh (runs in the shared worker).") { typeOption, dryRun, yes, force, retriesOption };
        connectionOptions.AddTo(model);
        model.SetAction((pr, ct) => EnqueueRefreshAsync(() => connectionOptions.Resolve(pr),
            RefreshKind.Model, null, null, PartitionOrder.NewestFirst,
            MaintenanceService.ParseRefreshType(pr.GetValue(typeOption)), null,
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), pr.GetValue(retriesOption), ct));

        var table = new Command("table", "Queue a single-table refresh.") { tableOption, typeOption, dryRun, yes, force, retriesOption };
        connectionOptions.AddTo(table);
        table.SetAction((pr, ct) => EnqueueRefreshAsync(() => connectionOptions.Resolve(pr),
            RefreshKind.Table, RequireOption(pr, tableOption, "--table"), null, PartitionOrder.NewestFirst,
            MaintenanceService.ParseRefreshType(pr.GetValue(typeOption)), null,
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), pr.GetValue(retriesOption), ct));

        var partition = new Command("partition", "Queue a single named-partition refresh.")
        {
            tableOption, partitionOption, typeOption, dryRun, yes, force, retriesOption,
        };
        connectionOptions.AddTo(partition);
        partition.SetAction((pr, ct) => EnqueueRefreshAsync(() => connectionOptions.Resolve(pr),
            RefreshKind.Partition, RequireOption(pr, tableOption, "--table"),
            RequireOption(pr, partitionOption, "--partition"), PartitionOrder.NewestFirst,
            MaintenanceService.ParseRefreshType(pr.GetValue(typeOption)), null,
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), pr.GetValue(retriesOption), ct));

        var partitions = new Command("partitions", "Queue a partition refresh — all of a table in order (newest-first), or a --partitions subset.")
        {
            tableOption, partitionsListOption, orderOption, typeOption, dryRun, yes, force, retriesOption,
        };
        connectionOptions.AddTo(partitions);
        partitions.SetAction((pr, ct) =>
        {
            var subset = pr.GetValue(partitionsListOption);
            var parts = string.IsNullOrWhiteSpace(subset)
                ? null
                : subset.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            return EnqueueRefreshAsync(() => connectionOptions.Resolve(pr),
                parts is null ? RefreshKind.AllPartitions : RefreshKind.SomePartitions,
                RequireOption(pr, tableOption, "--table"), null, ParseOrder(pr.GetValue(orderOption)),
                MaintenanceService.ParseRefreshType(pr.GetValue(typeOption)), parts,
                pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), pr.GetValue(retriesOption), ct);
        });

        var trigger = new Command("trigger", "Trigger a model refresh via REST (async, server-managed — not queued through the worker).") { dryRun, yes, force, retriesOption };
        connectionOptions.AddTo(trigger);
        trigger.SetAction((pr, ct) => RunRefreshTriggerAsync(() => connectionOptions.Resolve(pr),
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct, pr.GetValue(retriesOption)));

        var history = new Command("history", "Show recent refresh history (REST).") { topOption, outputOption };
        connectionOptions.AddTo(history);
        history.SetAction((pr, ct) => RunRefreshHistoryAsync(() => connectionOptions.Resolve(pr),
            () => pr.GetValue(outputOption), pr.GetValue(topOption), ct));

        var status = new Command("status", "Show refresh jobs on the shared queue (queued/running/finished, all interfaces).") { topOption };
        connectionOptions.AddTo(status);
        status.SetAction((pr, ct) => RunRefreshStatusAsync(() => connectionOptions.Resolve(pr), pr.GetValue(topOption)));

        var jobIdArg = new Argument<int>("job-id") { Description = "Job id to resume (from `refresh status`)." };
        var fullOption = new Option<bool>("--full") { Description = "Full re-run; default re-runs only the not-yet-done partitions." };
        var resume = new Command("resume", "Re-run a finished/interrupted job — only the not-yet-done partitions by default, or --full.")
        { jobIdArg, fullOption, dryRun, yes, force };
        resume.SetAction((pr, ct) => RunResumeAsync(
            pr.GetValue(jobIdArg), pr.GetValue(fullOption),
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));

        return new Command("refresh", "Queue model / table / partition(s) refreshes; resume; view status & history.")
        {
            model, table, partition, partitions, resume, trigger, history, status,
        };
    }

    /// <summary>Resumes a finished/interrupted job — by default only the not-yet-done partitions
    /// (a partition job that recorded its order + progress), or a full re-run with <c>--full</c>.</summary>
    private static Task<int> RunResumeAsync(int jobId, bool full, bool dryRun, bool yes, bool force, CancellationToken ct)
    {
        try
        {
            var store = new RefreshQueueStore();
            var resume = store.ResumeSpec(jobId, remainingOnly: !full);
            if (resume is null)
            {
                Console.Error.WriteLine($"daxter: job #{jobId} not found.");
                return Task.FromResult(1);
            }

            var (spec, count, partial) = resume.Value;
            var plan = RefreshTitle.Describe(spec);
            var what = partial ? $"resume {count} remaining partition(s)" : "full re-run";

            if (dryRun || !yes)
            {
                Console.Error.WriteLine($"DRY RUN — would {what}: {plan}.");
                if (!yes && !dryRun) Console.Error.WriteLine("Re-run with --yes to queue.");
                return Task.FromResult(0);
            }

            var config = DaxterConfig.FromEnvironment(workspace: spec.Workspace, dataset: spec.Dataset);
            if (LooksLikeProd(config) && !force)
            {
                Console.Error.WriteLine($"daxter: target looks like PRODUCTION ('{spec.Workspace}'). Re-run with --force to proceed.");
                return Task.FromResult(1);
            }

            var job = store.Enqueue(spec, RefreshTitle.For(spec), JobOrigin.Cli);
            Console.Out.WriteLine(job.Id);
            Console.Error.WriteLine($"Queued as job #{job.Id} ({what}) — {plan}. Track with `daxter refresh status`.");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex));
        }
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
        bool dryRun, bool yes, bool force, CancellationToken ct, int retries = 0)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            var factory = BuildSessionFactory(config);

            using var session = await factory.CreateAsync(ct);
            var service = new MaintenanceService(session, config.Dataset!);
            var command = build(service);

            return ApplySafety(config, command, dryRun, yes, force, () =>
                RetryPolicy.Execute(() => service.Execute(command), retries,
                    onRetry: (a, m, ex) => Console.Error.WriteLine($"daxter: transient failure (retry {a}/{m}): {ex.Message}")));
        }
        catch (Exception ex)
        {
            return Fail(ex);
        }
    }

    /// <summary>
    /// Enqueues a refresh onto the shared queue (no inline execution). The single worker — hosted by
    /// the DAXter web container — drains the queue and runs it, serialized one refresh per model.
    /// Dry-run prints the plan; without --yes it refuses; PROD-looking targets need --force. Prints the
    /// job id to stdout. Connection-free: enqueueing only writes to the shared <c>~/.daxter</c> volume.
    /// </summary>
    private static Task<int> EnqueueRefreshAsync(
        Func<DaxterConfig> configFactory,
        RefreshKind kind, string? table, string? partition,
        PartitionOrder order, RefreshType type, IReadOnlyList<string>? partitions,
        bool dryRun, bool yes, bool force, int retries, CancellationToken ct)
    {
        try
        {
            var config = configFactory();
            RequireDataset(config);
            if (string.IsNullOrWhiteSpace(config.Workspace))
                throw new DaxterException("A workspace is required (set DAXTER_WORKSPACE or --workspace).");

            var spec = new RefreshSpec(kind, config.Workspace!, config.Dataset!, table, partition, order, type, partitions, retries);
            var plan = RefreshTitle.Describe(spec);

            if (dryRun)
            {
                Console.Error.WriteLine("-- dry run; not queued --");
                Console.Out.WriteLine(plan);
                return Task.FromResult(0);
            }

            if (!yes)
            {
                Console.Out.WriteLine(plan);
                Console.Error.WriteLine("Refusing to queue without --yes. Re-run with --yes (or --dry-run to preview).");
                return Task.FromResult(0);
            }

            if (LooksLikeProd(config) && !force)
            {
                Console.Error.WriteLine($"daxter: target looks like PRODUCTION ('{config.Workspace}'). Re-run with --force to proceed.");
                return Task.FromResult(1);
            }

            var store = new RefreshQueueStore();
            var job = store.Enqueue(spec, RefreshTitle.For(spec), JobOrigin.Cli);
            Console.Out.WriteLine(job.Id);
            Console.Error.WriteLine($"Queued as job #{job.Id} — {plan}. The DAXter worker (web container) runs it, serialized per model.");

            var age = store.HeartbeatAge();
            Console.Error.WriteLine(age is null || age > TimeSpan.FromSeconds(30)
                ? "⚠ No refresh worker detected — start the DAXter web container so queued jobs execute. Track with `daxter refresh status`."
                : "Track with `daxter refresh status`.");
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex));
        }
    }

    /// <summary>Prints the shared refresh queue (all interfaces) plus whether a worker is draining it.</summary>
    private static Task<int> RunRefreshStatusAsync(Func<DaxterConfig> configFactory, int top)
    {
        try
        {
            var store = new RefreshQueueStore();

            DaxterConfig? config = null;
            try { config = configFactory(); } catch { /* status needs no resolvable config */ }

            var jobs = (config is { Workspace: { Length: > 0 } ws, Dataset: { Length: > 0 } ds })
                ? store.For(ws, ds)
                : store.All();

            var age = store.HeartbeatAge();
            var worker = age is null ? "none detected"
                : age > TimeSpan.FromSeconds(30) ? $"stale ({(int)age.Value.TotalSeconds}s ago)"
                : "alive";
            Console.Error.WriteLine($"worker: {worker}   jobs: {jobs.Count}");

            foreach (var j in jobs.Take(Math.Max(1, top)))
            {
                var parts = j.PartitionTotal is { } t ? $" [{j.PartitionDone ?? 0}/{t}]" : "";
                var dur = j.Duration is { } d ? $" {(int)d.TotalSeconds}s" : "";
                Console.Out.WriteLine($"#{j.Id,-4} {j.Status,-11} {j.Origin,-3} {j.Title}{parts}{dur}");
                if (!string.IsNullOrWhiteSpace(j.Step) && j.IsActive)
                    Console.Out.WriteLine($"        → {j.Step}");
                if (!string.IsNullOrWhiteSpace(j.Error))
                    Console.Out.WriteLine($"        ! {j.Error}");
            }
            return Task.FromResult(0);
        }
        catch (Exception ex)
        {
            return Task.FromResult(Fail(ex));
        }
    }

    private static async Task<int> RunRefreshTriggerAsync(
        Func<DaxterConfig> configFactory, bool dryRun, bool yes, bool force, CancellationToken ct, int retries = 0)
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
                () => RetryPolicy.Execute(() => rest.TriggerRefreshAsync(groupId, datasetId, ct).GetAwaiter().GetResult(), retries,
                    onRetry: (a, m, ex) => Console.Error.WriteLine($"daxter: transient failure (retry {a}/{m}): {ex.Message}")),
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

    private static bool LooksLikeProd(DaxterConfig config) => config.IsReadOnlyTarget();

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

    // ── Artifact-store CLI verbs ──────────────────────────────────────────────────────────────
    // The artifact subcommand group is the CLI face of the transport-agnostic file plane (see
    // src/Daxter.Core/Artifacts/ArtifactStore.cs). Five verbs: list / get / bundle / meta / rm.
    // Phase 1 = read + delete; put + extract land in Phase 2.
    //
    // Important: no DaxterConfig / no MSAL token here. The artifact store is a LOCAL filesystem
    // resource; there's no Power BI / Fabric REST call involved. We instantiate LocalArtifactStore
    // per invocation — the CLI process is short-lived, so no singleton coordination needed.
    private static Command BuildArtifactCommand(Option<string> outputOption)
    {
        var prefixArg = new Argument<string?>("prefix")
        {
            Description = "Optional prefix to narrow the scan (e.g. reports/, sql/, alignment/).",
            Arity = ArgumentArity.ZeroOrOne,
        };
        var ls = new Command("list", "List artifacts in the store. Optional <prefix> narrows the scan.")
            { prefixArg, outputOption };
        ls.SetAction((pr, ct) => RunArtifactListAsync(pr.GetValue(prefixArg), () => pr.GetValue(outputOption), ct));

        var keyArg = new Argument<string>("key") { Description = "Artifact key (forward-slash path, e.g. reports/sales/page.json)." };
        var outFileOpt = new Option<string?>("--out", "-o")
            { Description = "Write content to this file path (defaults to the key's filename in the current directory)." };
        var get = new Command("get", "Download a single artifact's content.") { keyArg, outFileOpt };
        get.SetAction((pr, ct) => RunArtifactGetAsync(pr.GetValue(keyArg)!, pr.GetValue(outFileOpt), ct));

        var prefixArg2 = new Argument<string>("prefix")
            { Description = "Key prefix to zip (every file under it goes into the archive)." };
        var bundleOutOpt = new Option<string?>("--out", "-o")
            { Description = "Write the zip to this file (defaults to <prefix>.zip in the current directory)." };
        var bundle = new Command("bundle", "Zip every file under a prefix into a single archive.") { prefixArg2, bundleOutOpt };
        bundle.SetAction((pr, ct) => RunArtifactBundleAsync(pr.GetValue(prefixArg2)!, pr.GetValue(bundleOutOpt), ct));

        var metaKeyArg = new Argument<string>("key") { Description = "Artifact key to inspect." };
        var meta = new Command("meta", "Show one artifact's metadata (size, created, expires, source tool).") { metaKeyArg };
        meta.SetAction((pr, ct) => RunArtifactMetaAsync(pr.GetValue(metaKeyArg)!, ct));

        var rmKeyArg = new Argument<string>("prefix") { Description = "Artifact key OR key prefix (deletes recursively)." };
        var rmYesOpt = new Option<bool>("--yes") { Description = "Skip the are-you-sure confirmation." };
        var rm = new Command("rm", "Delete one artifact or an entire prefix.") { rmKeyArg, rmYesOpt };
        rm.SetAction((pr, ct) => RunArtifactRmAsync(pr.GetValue(rmKeyArg)!, pr.GetValue(rmYesOpt), ct));

        // put — Phase 2. Streams a local file INTO the store at <key>. Optional --ttl-hours
        // attaches an expiry so the nightly purge sweeps it; --source-tool stamps provenance.
        var putKeyArg = new Argument<string>("key") { Description = "Artifact key to write to (forward-slash path)." };
        var putFileArg = new Argument<string>("file") { Description = "Local file path whose contents become the artifact." };
        var putTtlOpt = new Option<double?>("--ttl-hours") { Description = "Attach a TTL — artifact expires after this many hours; the nightly purge will sweep it." };
        var putSourceOpt = new Option<string?>("--source-tool") { Description = "Source-tool label that shows up on /artifacts." };
        var put = new Command("put", "Upload one local file into the artifact store.") { putKeyArg, putFileArg, putTtlOpt, putSourceOpt };
        put.SetAction((pr, ct) => RunArtifactPutAsync(
            pr.GetValue(putKeyArg)!, pr.GetValue(putFileArg)!,
            pr.GetValue(putTtlOpt), pr.GetValue(putSourceOpt), ct));

        // extract — Phase 2. Unzips a local zip file INTO the store under a key prefix; each
        // entry becomes a separate artifact at <prefix>/<entry-path>. Same TTL/source-tool
        // plumbing as put.
        var extPrefixArg = new Argument<string>("prefix") { Description = "Key prefix to unzip into (e.g. 'alignment/sales-dashboard-fixed')." };
        var extZipArg = new Argument<string>("zip") { Description = "Local zip file." };
        var extTtlOpt = new Option<double?>("--ttl-hours") { Description = "Every entry inherits this TTL." };
        var extSourceOpt = new Option<string?>("--source-tool") { Description = "Source-tool label." };
        var extract = new Command("extract", "Unzip a local archive INTO the store under a key prefix.")
            { extPrefixArg, extZipArg, extTtlOpt, extSourceOpt };
        extract.SetAction((pr, ct) => RunArtifactExtractAsync(
            pr.GetValue(extPrefixArg)!, pr.GetValue(extZipArg)!,
            pr.GetValue(extTtlOpt), pr.GetValue(extSourceOpt), ct));

        // purge-expired — Phase 2. On-demand sweep of every TTL-expired entry. Mirrors the
        // /artifacts page button + the nightly hosted-service tick.
        var purge = new Command("purge-expired", "Delete every artifact whose TTL is in the past. Returns bytes freed.");
        purge.SetAction((_, ct) => RunArtifactPurgeAsync(ct));

        return new Command("artifact",
            "Local artifact store — list / get / bundle / meta / put / extract / rm / purge-expired. " +
            "The transport-agnostic file plane DAXter uses to ferry bytes between itself and the agent " +
            "(PBIR exports, SQL CSVs, definition uploads). Phase 1 added read + delete; Phase 2 adds put + extract + purge.")
        { ls, get, bundle, meta, rm, put, extract, purge };
    }

    private static async Task<int> RunArtifactListAsync(string? prefix, Func<string?> outputFactory, CancellationToken ct)
    {
        try
        {
            var store = new LocalArtifactStore();
            var items = await store.ListAsync(prefix, ct);
            var cols = new List<string> { "key", "bytes", "created_utc", "expires_utc", "source_tool" };
            var rows = items.Select(a => new object?[]
            {
                a.Key, a.Bytes,
                a.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                a.ExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                a.SourceTool ?? "",
            }).ToList();
            var result = new QueryResult(cols, rows);
            var format = ResultFormatterFactory.Parse(outputFactory());
            Console.Out.Write(ResultFormatterFactory.Create(format).Format(result));
            Console.Error.WriteLine($"({result.RowCount} artifact{(result.RowCount == 1 ? "" : "s")}, " +
                $"{await store.CurrentUsageBytesAsync(ct):N0} bytes used of {store.QuotaBytes:N0} quota)");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunArtifactGetAsync(string key, string? outFile, CancellationToken ct)
    {
        try
        {
            var store = new LocalArtifactStore();
            var dest = outFile ?? Path.GetFileName(key.TrimEnd('/'));
            if (string.IsNullOrWhiteSpace(dest)) dest = "artifact.bin";
            await using var src = await store.OpenReadAsync(key, ct);
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, ct);
            Console.Error.WriteLine($"Wrote {new FileInfo(dest).Length:N0} bytes → {dest}");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunArtifactBundleAsync(string prefix, string? outFile, CancellationToken ct)
    {
        try
        {
            var store = new LocalArtifactStore();
            // Best-effort filename: trailing segment of the prefix, .zip-suffixed.
            var dest = outFile ?? (prefix.TrimEnd('/').Split('/').LastOrDefault() ?? "bundle") + ".zip";
            await using var src = await store.OpenBundleAsync(prefix, ct);
            await using var dst = File.Create(dest);
            await src.CopyToAsync(dst, ct);
            Console.Error.WriteLine($"Bundled {new FileInfo(dest).Length:N0} bytes → {dest}");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunArtifactMetaAsync(string key, CancellationToken ct)
    {
        try
        {
            var store = new LocalArtifactStore();
            var meta = await store.GetMetaAsync(key, ct);
            if (meta is null) { Console.Error.WriteLine($"Not found: {key}"); return 1; }
            Console.Out.WriteLine($"key:         {meta.Key}");
            Console.Out.WriteLine($"bytes:       {meta.Bytes:N0}");
            Console.Out.WriteLine($"created_utc: {meta.CreatedAt:yyyy-MM-dd HH:mm:ss}");
            Console.Out.WriteLine($"expires_utc: {meta.ExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "(no TTL)"}");
            Console.Out.WriteLine($"source_tool: {meta.SourceTool ?? "(not recorded)"}");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunArtifactRmAsync(string prefix, bool yes, CancellationToken ct)
    {
        try
        {
            var store = new LocalArtifactStore();
            if (!yes)
            {
                // Show what'll get hit BEFORE asking — so the user can sanity-check the prefix.
                var hits = await store.ListAsync(prefix, ct);
                if (hits.Count == 0) { Console.Error.WriteLine($"Nothing matches '{prefix}'."); return 0; }
                Console.Error.WriteLine($"About to delete {hits.Count} artifact(s) ({hits.Sum(h => h.Bytes):N0} bytes) under '{prefix}':");
                foreach (var h in hits.Take(20)) Console.Error.WriteLine($"  {h.Key}");
                if (hits.Count > 20) Console.Error.WriteLine($"  ... and {hits.Count - 20} more");
                Console.Error.Write("Proceed? [y/N] ");
                var line = Console.ReadLine();
                if (line is null || !line.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Aborted."); return 1;
                }
            }
            var removed = await store.DeleteAsync(prefix, ct);
            Console.Error.WriteLine($"Removed {removed} artifact(s).");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunArtifactPutAsync(string key, string file, double? ttlHours, string? sourceTool, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(file)) throw new DaxterException($"File not found: {file}");
            var store = new LocalArtifactStore();
            var meta = new ArtifactMeta(
                ExpiresAt: ttlHours is { } h && h > 0 ? DateTime.UtcNow.AddHours(h) : null,
                SourceTool: sourceTool ?? "daxter_cli");
            await using var fs = File.OpenRead(file);
            var aref = await store.PutAsync(key, fs, meta, ct);
            Console.Error.WriteLine($"Stored '{aref.Key}' ({aref.Bytes:N0} bytes" +
                (aref.ExpiresAt is { } e ? $", expires {e:yyyy-MM-dd HH:mm:ss}Z" : "") +
                $", source_tool={aref.SourceTool}).");
            return 0;
        }
        catch (ArtifactQuotaExceededException ex)
        {
            Console.Error.WriteLine($"daxter: {ex.Message}");
            return 1;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunArtifactExtractAsync(string prefix, string zip, double? ttlHours, string? sourceTool, CancellationToken ct)
    {
        try
        {
            if (!File.Exists(zip)) throw new DaxterException($"Zip not found: {zip}");
            var store = new LocalArtifactStore();
            var meta = new ArtifactMeta(
                ExpiresAt: ttlHours is { } h && h > 0 ? DateTime.UtcNow.AddHours(h) : null,
                SourceTool: sourceTool ?? "daxter_cli");
            await using var fs = File.OpenRead(zip);
            var written = await store.ExtractAsync(prefix, fs, meta, ct);
            Console.Error.WriteLine($"Extracted {written.Count} entries ({written.Sum(w => w.Bytes):N0} bytes) under '{prefix}'.");
            return 0;
        }
        catch (ArtifactQuotaExceededException ex)
        {
            Console.Error.WriteLine($"daxter: {ex.Message}");
            return 1;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunArtifactPurgeAsync(CancellationToken ct)
    {
        try
        {
            var store = new LocalArtifactStore();
            var bytes = await store.PurgeExpiredAsync(ct);
            Console.Error.WriteLine($"Purge freed {bytes:N0} bytes.");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    // ── Context CLI verbs (Phase 4) ───────────────────────────────────────────────────────────
    // Same subcommand pattern as `daxter artifact`. The context plane sits on top of the
    // artifact store under `context/`; these verbs let humans curate without the MCP route.
    // Five verbs: list / get / put / search / rm. `put` accepts content inline via --content,
    // OR from a file via --from-file (more practical for multi-line markdown).

    private static Command BuildContextCommand(Option<string> outputOption)
    {
        var nsArg = new Argument<string?>("namespace")
            { Description = "Optional sub-namespace to narrow (e.g. 'clients/acme' or 'skills').", Arity = ArgumentArity.ZeroOrOne };
        var ls = new Command("list", "List shared-knowledge context entries.") { nsArg, outputOption };
        ls.SetAction((pr, ct) => RunContextListAsync(pr.GetValue(nsArg), () => pr.GetValue(outputOption), ct));

        var keyArg = new Argument<string>("key") { Description = "Context key WITHOUT the 'context/' prefix." };
        var get = new Command("get", "Show a single context entry's full text.") { keyArg };
        get.SetAction((pr, ct) => RunContextGetAsync(pr.GetValue(keyArg)!, ct));

        var putKeyArg = new Argument<string>("key") { Description = "Context key WITHOUT the 'context/' prefix." };
        var contentOpt = new Option<string?>("--content") { Description = "Inline text content (for single-line entries)." };
        var fromFileOpt = new Option<string?>("--from-file") { Description = "Read content from this file (preferred for markdown / multi-line)." };
        var sourceOpt = new Option<string?>("--source-tool") { Description = "Provenance label (e.g. 'team-curator', your email)." };
        var ttlOpt = new Option<double?>("--ttl-hours") { Description = "Attach a TTL — entry expires after this many hours." };
        var put = new Command("put", "Create or update a context entry.") { putKeyArg, contentOpt, fromFileOpt, sourceOpt, ttlOpt };
        put.SetAction((pr, ct) => RunContextPutAsync(
            pr.GetValue(putKeyArg)!,
            pr.GetValue(contentOpt),
            pr.GetValue(fromFileOpt),
            pr.GetValue(sourceOpt),
            pr.GetValue(ttlOpt),
            ct));

        var queryArg = new Argument<string>("query") { Description = "Substring to search for (case-insensitive)." };
        var search = new Command("search", "Search keys + bodies across every context entry.") { queryArg, outputOption };
        search.SetAction((pr, ct) => RunContextSearchAsync(pr.GetValue(queryArg)!, () => pr.GetValue(outputOption), ct));

        var rmArg = new Argument<string>("key") { Description = "Context key OR sub-namespace (deletes recursively)." };
        var rmYesOpt = new Option<bool>("--yes") { Description = "Skip the confirmation prompt." };
        var rm = new Command("rm", "Delete a context entry or a sub-namespace.") { rmArg, rmYesOpt };
        rm.SetAction((pr, ct) => RunContextRmAsync(pr.GetValue(rmArg)!, pr.GetValue(rmYesOpt), ct));

        return new Command("context",
            "Shared-knowledge plane — curated team facts, glossaries, prompts, skills. " +
            "Lives under `context/` in the artifact store; visible to every MCP session connected to this DAXter. " +
            "Verbs: list / get / put / search / rm.")
        { ls, get, put, search, rm };
    }

    private static async Task<int> RunContextListAsync(string? ns, Func<string?> outputFactory, CancellationToken ct)
    {
        try
        {
            var svc = new ContextService(new LocalArtifactStore());
            var entries = await svc.ListAsync(ns, ct);
            var cols = new List<string> { "key", "namespace", "bytes", "created_utc", "expires_utc", "source_tool" };
            var rows = entries.Select(e => new object?[]
            {
                e.Key, e.Namespace, e.Bytes,
                e.CreatedAt.ToString("yyyy-MM-dd HH:mm:ss"),
                e.ExpiresAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "",
                e.SourceTool ?? "",
            }).ToList();
            var result = new QueryResult(cols, rows);
            var format = ResultFormatterFactory.Parse(outputFactory());
            Console.Out.Write(ResultFormatterFactory.Create(format).Format(result));
            Console.Error.WriteLine($"({result.RowCount} entr{(result.RowCount == 1 ? "y" : "ies")})");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunContextGetAsync(string key, CancellationToken ct)
    {
        try
        {
            var svc = new ContextService(new LocalArtifactStore());
            var (content, entry, tooBig) = await svc.GetAsync(key, ct);
            Console.Error.WriteLine($"# {entry.Key}  ({entry.Bytes:N0} B, source: {entry.SourceTool ?? "—"})");
            if (tooBig)
            {
                Console.Error.WriteLine($"# (body exceeds 1 MB inline limit — read directly from ~/.daxter/artifacts/{ContextService.RootPrefix}{key})");
                return 0;
            }
            Console.Out.WriteLine(content);
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunContextPutAsync(
        string key, string? inlineContent, string? fromFile, string? sourceTool, double? ttlHours, CancellationToken ct)
    {
        try
        {
            string content;
            if (!string.IsNullOrWhiteSpace(fromFile))
            {
                if (!File.Exists(fromFile)) throw new DaxterException($"File not found: {fromFile}");
                content = await File.ReadAllTextAsync(fromFile, ct);
            }
            else if (!string.IsNullOrEmpty(inlineContent))
            {
                content = inlineContent;
            }
            else
            {
                throw new DaxterException("Pass --content (inline) OR --from-file (path).");
            }
            var svc = new ContextService(new LocalArtifactStore());
            var entry = await svc.PutAsync(key, content, sourceTool ?? "daxter_cli", ttlHours, ct);
            Console.Error.WriteLine($"Stored '{entry.Key}' ({entry.Bytes:N0} B, source: {entry.SourceTool}).");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunContextSearchAsync(string query, Func<string?> outputFactory, CancellationToken ct)
    {
        try
        {
            var svc = new ContextService(new LocalArtifactStore());
            var hits = await svc.SearchAsync(query, ct);
            var cols = new List<string> { "key", "namespace", "match_count", "snippet" };
            var rows = hits.Select(h => new object?[] { h.Key, h.Namespace, h.MatchCount, h.Snippet }).ToList();
            var result = new QueryResult(cols, rows);
            var format = ResultFormatterFactory.Parse(outputFactory());
            Console.Out.Write(ResultFormatterFactory.Create(format).Format(result));
            Console.Error.WriteLine($"({result.RowCount} hit{(result.RowCount == 1 ? "" : "s")})");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }

    private static async Task<int> RunContextRmAsync(string key, bool yes, CancellationToken ct)
    {
        try
        {
            var svc = new ContextService(new LocalArtifactStore());
            if (!yes)
            {
                var hits = await svc.ListAsync(key, ct);
                if (hits.Count == 0) { Console.Error.WriteLine($"Nothing matches '{key}'."); return 0; }
                Console.Error.WriteLine($"About to delete {hits.Count} entr{(hits.Count == 1 ? "y" : "ies")} under '{key}':");
                foreach (var h in hits.Take(20)) Console.Error.WriteLine($"  {h.Key}");
                if (hits.Count > 20) Console.Error.WriteLine($"  ... and {hits.Count - 20} more");
                Console.Error.Write("Proceed? [y/N] ");
                var line = Console.ReadLine();
                if (line is null || !line.Trim().Equals("y", StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine("Aborted."); return 1;
                }
            }
            var removed = await svc.DeleteAsync(key, ct);
            Console.Error.WriteLine($"Removed {removed} entr{(removed == 1 ? "y" : "ies")}.");
            return 0;
        }
        catch (Exception ex) { return Fail(ex); }
    }
}
