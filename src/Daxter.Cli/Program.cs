using System.CommandLine;
using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;
using Daxter.Core.Connection;
using Daxter.Core.Formatting;
using Daxter.Core.Metadata;
using Daxter.Core.Query;

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

        var root = new RootCommand(
            "DAXter — Power BI Service CLI: query, model metadata, and maintenance over XMLA.")
        {
            queryCommand, dmvCommand, lsCommand, loginCommand, modelCommand, envCommand,
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

    private static IXmlaSessionFactory BuildSessionFactory(DaxterConfig config)
    {
        var provider = new MsalTokenProvider(config, deviceCodePrompt: WriteToStdErr);
        return new AdomdXmlaSessionFactory(config, provider);
    }

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
