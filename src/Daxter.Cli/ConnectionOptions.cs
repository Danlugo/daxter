using System.CommandLine;
using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;

namespace Daxter.Cli;

/// <summary>
/// The shared connection/auth options added to every command. Option values
/// override the corresponding environment variables when present.
/// </summary>
internal sealed class ConnectionOptions
{
    private readonly Option<string?> _workspace = new("--workspace", "-w")
    {
        Description = "Workspace name or full XMLA data source.",
    };

    private readonly Option<string?> _dataset = new("--dataset", "-d")
    {
        Description = "Dataset / semantic model name (Initial Catalog).",
    };

    private readonly Option<string?> _tenant = new("--tenant")
    {
        Description = "Entra ID tenant id or domain.",
    };

    private readonly Option<string?> _clientId = new("--client-id")
    {
        Description = "App registration (client) id.",
    };

    private readonly Option<string?> _clientSecret = new("--client-secret")
    {
        Description = "Client secret (service-principal auth). Prefer the env var.",
    };

    private readonly Option<string?> _auth = new("--auth")
    {
        Description = "Auth mode: device-code (default) or service-principal.",
    };

    private readonly Option<string?> _env = new("--env", "-e")
    {
        Description = "Environment profile (e.g. dev) → uses DAXTER_WORKSPACE_<ENV>.",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(_workspace);
        command.Options.Add(_dataset);
        command.Options.Add(_tenant);
        command.Options.Add(_clientId);
        command.Options.Add(_clientSecret);
        command.Options.Add(_auth);
        command.Options.Add(_env);
    }

    /// <summary>Merges CLI options with environment variables into a validated config.</summary>
    public DaxterConfig Resolve(ParseResult parseResult)
    {
        var authValue = parseResult.GetValue(_auth);
        AuthMode? authMode = authValue switch
        {
            null or "" => null,
            "device-code" or "devicecode" or "device" or "interactive" => AuthMode.DeviceCode,
            "service-principal" or "serviceprincipal" or "sp" or "app" => AuthMode.ServicePrincipal,
            _ => throw new DaxterException(
                $"Unknown --auth value '{authValue}'. Use device-code or service-principal."),
        };

        return DaxterConfig.FromEnvironment(
            workspace: parseResult.GetValue(_workspace),
            dataset: parseResult.GetValue(_dataset),
            tenantId: parseResult.GetValue(_tenant),
            clientId: parseResult.GetValue(_clientId),
            clientSecret: parseResult.GetValue(_clientSecret),
            authMode: authMode,
            environment: parseResult.GetValue(_env));
    }
}
