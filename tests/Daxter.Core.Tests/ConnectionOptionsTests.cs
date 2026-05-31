using System.CommandLine;
using Daxter.Cli;
using Daxter.Core;
using Daxter.Core.Configuration;

namespace Daxter.Core.Tests;

// Touches env vars, so it must not run alongside other env-touching tests.
[Collection("EnvSerial")]
public class ConnectionOptionsTests
{
    private static readonly string[] WorkspaceVars =
    [
        DaxterConfig.EnvWorkspace, DaxterConfig.EnvEnvironment,
        "DAXTER_WORKSPACE_DEV", "DAXTER_WORKSPACE_QA",
    ];

    private static void WithNoWorkspaceEnv(Action body)
    {
        var saved = WorkspaceVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        var savedHome = Environment.GetEnvironmentVariable("HOME");
        // Empty HOME → no ~/.daxter/console-config.json, so the volume-config layer can't supply a workspace.
        var home = Path.Combine(Path.GetTempPath(), "daxter-conntest-home");
        try
        {
            if (Directory.Exists(home)) Directory.Delete(home, true);
            Directory.CreateDirectory(home);
            Environment.SetEnvironmentVariable("HOME", home);
            foreach (var v in WorkspaceVars) Environment.SetEnvironmentVariable(v, null);
            body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            Environment.SetEnvironmentVariable("HOME", savedHome);
            try { if (Directory.Exists(home)) Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }

    private static ParseResult ParseNoArgs(ConnectionOptions opts)
    {
        var cmd = new Command("test");
        opts.AddTo(cmd);
        return cmd.Parse(Array.Empty<string>());
    }

    // Regression: `ws ls` / `ws gateways` are tenant-level — listing workspaces/gateways must NOT
    // require a workspace (it previously failed with "No workspace configured").
    [Fact]
    public void Resolve_without_workspace_succeeds_when_not_required()
    {
        WithNoWorkspaceEnv(() =>
        {
            var opts = new ConnectionOptions();
            var cfg = opts.Resolve(ParseNoArgs(opts), requireWorkspace: false);
            Assert.True(string.IsNullOrEmpty(cfg.Workspace));
        });
    }

    // Workspace-scoped commands still enforce a workspace by default.
    [Fact]
    public void Resolve_without_workspace_throws_by_default()
    {
        WithNoWorkspaceEnv(() =>
        {
            var opts = new ConnectionOptions();
            Assert.Throws<DaxterException>(() => opts.Resolve(ParseNoArgs(opts)));
        });
    }
}
