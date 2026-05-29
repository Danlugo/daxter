using System.CommandLine;
using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
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

        var root = new RootCommand(
            "DAXter — Power BI Service CLI: query, model metadata, maintenance, and inventory.")
        {
            queryCommand, dmvCommand, lsCommand, loginCommand, modelCommand, envCommand,
            refreshCommand, cacheCommand, wsCommand,
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

        var rls = new Command("rls", "Show RLS roles, or a role's filters + members.") { roleOption, outputOption };
        connectionOptions.AddTo(rls);
        rls.SetAction((pr, ct) => RunRlsAsync(
            () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption),
            pr.GetValue(roleOption), ct));

        return new Command("model", "Inspect model metadata (measures, M code, RLS, partitions).")
        {
            measures, measure, mcode, parameters, partitions, rls,
        };
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

    // ---- Workspace inventory (REST) ----

    private static Command BuildWorkspaceCommand(ConnectionOptions connectionOptions, Option<string> outputOption)
    {
        Command Sub(string name, string desc,
            Func<PowerBiRestClient, DaxterConfig, CancellationToken, Task<QueryResult>> op)
        {
            var cmd = new Command(name, desc) { outputOption };
            connectionOptions.AddTo(cmd);
            cmd.SetAction((pr, ct) => RunRestQueryAsync(
                () => connectionOptions.Resolve(pr), () => pr.GetValue(outputOption), op, ct));
            return cmd;
        }

        var ls = Sub("ls", "List workspaces (group ids).", (rest, _, ct) => rest.GroupsAsync(ct));
        var datasets = Sub("datasets", "List datasets in the workspace.", async (rest, cfg, ct) =>
            await rest.DatasetsAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, ct), ct));
        var reports = Sub("reports", "List reports in the workspace.", async (rest, cfg, ct) =>
            await rest.ReportsAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, ct), ct));
        var lineage = Sub("lineage", "Report → dataset lineage.", async (rest, cfg, ct) =>
            await rest.LineageAsync(await rest.ResolveGroupIdAsync(cfg.Workspace, ct), ct));
        var gateways = Sub("gateways", "List gateways (needs gateway admin).", (rest, _, ct) => rest.GatewaysAsync(ct));
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

    private static ITokenProvider BuildTokenProvider(DaxterConfig config)
        => new MsalTokenProvider(config, deviceCodePrompt: WriteToStdErr);

    private static IXmlaSessionFactory BuildSessionFactory(DaxterConfig config)
        => new AdomdXmlaSessionFactory(config, BuildTokenProvider(config));

    // ---- Ops: refresh / cache ----

    private static Command BuildRefreshCommand(ConnectionOptions connectionOptions, Option<string> outputOption)
    {
        var tableOption = new Option<string?>("--table", "-t") { Description = "Table name." };
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

        var partitions = new Command("partitions", "Refresh a table's partitions, newest-first (TMSL).")
        {
            tableOption, orderOption, typeOption, dryRun, yes, force,
        };
        connectionOptions.AddTo(partitions);
        partitions.SetAction((pr, ct) => RunTmslAsync(() => connectionOptions.Resolve(pr),
            svc => svc.BuildPartitionsRefresh(RequireOption(pr, tableOption, "--table"),
                ParseOrder(pr.GetValue(orderOption)),
                MaintenanceService.ParseRefreshType(pr.GetValue(typeOption))),
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));

        var trigger = new Command("trigger", "Trigger a model refresh via REST (no XMLA write needed).") { dryRun, yes, force };
        connectionOptions.AddTo(trigger);
        trigger.SetAction((pr, ct) => RunRefreshTriggerAsync(() => connectionOptions.Resolve(pr),
            pr.GetValue(dryRun), pr.GetValue(yes), pr.GetValue(force), ct));

        var history = new Command("history", "Show recent refresh history (REST).") { topOption, outputOption };
        connectionOptions.AddTo(history);
        history.SetAction((pr, ct) => RunRefreshHistoryAsync(() => connectionOptions.Resolve(pr),
            () => pr.GetValue(outputOption), pr.GetValue(topOption), ct));

        return new Command("refresh", "Refresh models / tables / partitions; view history.")
        {
            model, table, partitions, trigger, history,
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

    private static bool LooksLikeProd(DaxterConfig config)
        => string.Equals(config.Environment, "prod", StringComparison.OrdinalIgnoreCase)
           || config.Workspace.Contains("prod", StringComparison.OrdinalIgnoreCase);

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
