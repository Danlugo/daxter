using System.Text.Json;
using Daxter.Cli.Mcp;

namespace Daxter.Core.Tests;

// Manipulates env vars + HOME, so it must run serialized with other env-touching tests.
[Collection("EnvSerial")]
public class MaintenanceGateTests
{
    // Runs body with the given env values and an isolated HOME; if consoleLevel is set, writes
    // ~/.daxter/console-config.json with that permission Level first. (v1.46.0 permission model.)
    private static void With(string? level, string? blockProd, string? consoleLevel, Action body)
    {
        string[] keys = ["DAXTER_LEVEL", "DAXTER_MCP_BLOCK_PROD_WRITES", "HOME"];
        var saved = keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        var home = Path.Combine(Path.GetTempPath(), "daxter-gate-test");
        try
        {
            if (Directory.Exists(home)) Directory.Delete(home, true);
            Directory.CreateDirectory(Path.Combine(home, ".daxter"));
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("DAXTER_LEVEL", level);
            Environment.SetEnvironmentVariable("DAXTER_MCP_BLOCK_PROD_WRITES", blockProd);
            if (consoleLevel is not null)
            {
                File.WriteAllText(Path.Combine(home, ".daxter", "console-config.json"),
                    JsonSerializer.Serialize(new { Level = consoleLevel }));
            }
            body();
        }
        finally
        {
            foreach (var (k, v) in saved) Environment.SetEnvironmentVariable(k, v);
            try { if (Directory.Exists(home)) Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }

    // DAXTER_LEVEL=modify (env ceiling, headless ⇒ also the active level) permits modify-class writes.
    [Fact]
    public void WritesAllowed_true_when_env_level_modify()
        => With("modify", null, null, () => Assert.True(DaxterToolRuntime.WritesAllowed()));

    // The web console's saved permission level enables MCP writes (shared volume).
    [Fact]
    public void WritesAllowed_true_when_console_level_modify()
        => With(null, null, consoleLevel: "modify", () => Assert.True(DaxterToolRuntime.WritesAllowed()));

    [Fact]
    public void WritesAllowed_false_at_read_level()
        => With("read", null, null, () => Assert.False(DaxterToolRuntime.WritesAllowed()));

    // A fresh install (no env, no saved level) defaults to read — no writes until the level is raised.
    [Fact]
    public void Defaults_to_read_when_unconfigured()
        => With(null, null, null, () => Assert.False(DaxterToolRuntime.WritesAllowed()));

    // Execute level permits refresh but NOT modify-class writes.
    [Fact]
    public void Execute_level_allows_refresh_not_writes()
        => With("execute", null, null, () =>
        {
            Assert.True(DaxterToolRuntime.RefreshAllowed());
            Assert.False(DaxterToolRuntime.WritesAllowed());
        });

    // PROD is allowed by default; the opt-out env var re-blocks it.
    [Fact]
    public void ProdWritesBlocked_is_false_by_default()
        => With(null, null, null, () => Assert.False(DaxterToolRuntime.ProdWritesBlocked()));

    [Fact]
    public void ProdWritesBlocked_true_when_optout_set()
        => With(null, "true", null, () => Assert.True(DaxterToolRuntime.ProdWritesBlocked()));
}
