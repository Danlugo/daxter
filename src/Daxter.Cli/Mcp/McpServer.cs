using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Daxter.Cli.Mcp;

/// <summary>
/// Hosts DAXter as a Model Context Protocol server over stdio. All logging is routed
/// to stderr so stdout stays a clean JSON-RPC channel. Tools are discovered from this
/// assembly (<see cref="DaxterTools"/>).
/// </summary>
internal static class McpServer
{
    public static async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var builder = Host.CreateApplicationBuilder();

        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithToolsFromAssembly();

        await builder.Build().RunAsync(cancellationToken);
        return 0;
    }
}
