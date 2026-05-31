using System.Text.Json;
using Daxter.Cli.Mcp;

namespace Daxter.Core.Tests;

// Manipulates env vars + HOME, so it must run serialized with other env-touching tests.
[Collection("EnvSerial")]
public class MaintenanceGateTests
{
    // Runs body with the given env values and an isolated HOME; if consoleAllowWrites is set,
    // writes ~/.daxter/console-config.json with that AllowWrites value first.
    private static void With(string? allowWrites, string? blockProd, bool? consoleAllowWrites, Action body)
    {
        string[] keys = ["DAXTER_MCP_ALLOW_WRITES", "DAXTER_MCP_BLOCK_PROD_WRITES", "HOME"];
        var saved = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        var home = Path.Combine(Path.GetTempPath(), "daxter-gate-test");
        try
        {
            if (Directory.Exists(home)) Directory.Delete(home, true);
            Directory.CreateDirectory(Path.Combine(home, ".daxter"));
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("DAXTER_MCP_ALLOW_WRITES", allowWrites);
            Environment.SetEnvironmentVariable("DAXTER_MCP_BLOCK_PROD_WRITES", blockProd);
            if (consoleAllowWrites is not null)
            {
                File.WriteAllText(Path.Combine(home, ".daxter", "console-config.json"),
                    JsonSerializer.Serialize(new { AllowWrites = consoleAllowWrites.Value }));
            }
            body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            try { if (Directory.Exists(home)) Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void WritesAllowed_true_when_env_set()
        => With("true", null, null, () => Assert.True(DaxterToolRuntime.WritesAllowed()));

    // The web console's saved "Allow writes" toggle enables MCP writes (shared volume).
    [Fact]
    public void WritesAllowed_true_when_console_toggle_on()
        => With(null, null, consoleAllowWrites: true, () => Assert.True(DaxterToolRuntime.WritesAllowed()));

    [Fact]
    public void WritesAllowed_false_when_env_unset_and_toggle_off()
        => With(null, null, consoleAllowWrites: false, () => Assert.False(DaxterToolRuntime.WritesAllowed()));

    // PROD is allowed by default; the opt-out env var re-blocks it.
    [Fact]
    public void ProdWritesBlocked_is_false_by_default()
        => With(null, null, null, () => Assert.False(DaxterToolRuntime.ProdWritesBlocked()));

    [Fact]
    public void ProdWritesBlocked_true_when_optout_set()
        => With(null, "true", null, () => Assert.True(DaxterToolRuntime.ProdWritesBlocked()));
}
