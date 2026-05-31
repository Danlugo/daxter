using Daxter.Core.Configuration;

namespace Daxter.Core.Tests;

// Verifies the single-source config: DaxterConfig.FromEnvironment layers the volume config
// (~/.daxter/console-config.json, written by the web console) over env vars.
[Collection("EnvSerial")]
public class PersistedSettingsTests
{
    private static readonly string[] EnvVars =
    [
        DaxterConfig.EnvWorkspace, DaxterConfig.EnvDataset, DaxterConfig.EnvTenantId,
        DaxterConfig.EnvClientId, DaxterConfig.EnvClientSecret, DaxterConfig.EnvAuthMode,
        DaxterConfig.EnvEnvironment, DaxterConfig.EnvProdWorkspaces,
        "DAXTER_WORKSPACE_DEV", "DAXTER_WORKSPACE_QA", "DAXTER_DATASET_DEV",
    ];

    private static void With(IDictionary<string, string?> env, PersistedSettings? saved, Action body)
    {
        var savedEnv = EnvVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        var savedHome = Environment.GetEnvironmentVariable("HOME");
        var home = Path.Combine(Path.GetTempPath(), "daxter-persist-test");
        try
        {
            if (Directory.Exists(home)) Directory.Delete(home, true);
            Directory.CreateDirectory(Path.Combine(home, ".daxter"));
            Environment.SetEnvironmentVariable("HOME", home);
            foreach (var v in EnvVars) Environment.SetEnvironmentVariable(v, env.TryGetValue(v, out var val) ? val : null);
            saved?.Save();
            body();
        }
        finally
        {
            foreach (var (k, v) in savedEnv) Environment.SetEnvironmentVariable(k, v);
            Environment.SetEnvironmentVariable("HOME", savedHome);
            try { if (Directory.Exists(home)) Directory.Delete(home, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void Volume_config_supplies_settings_when_no_env()
        => With(new Dictionary<string, string?>(), new PersistedSettings(Workspace: "UI Workspace", Dataset: "UI Model"), () =>
        {
            var cfg = DaxterConfig.FromEnvironment();
            Assert.Equal("UI Workspace", cfg.Workspace);
            Assert.Equal("UI Model", cfg.Dataset);
        });

    [Fact]
    public void Volume_config_wins_over_env()
        => With(new Dictionary<string, string?> { [DaxterConfig.EnvWorkspace] = "Env Workspace" },
                new PersistedSettings(Workspace: "UI Workspace"),
                () => Assert.Equal("UI Workspace", DaxterConfig.FromEnvironment().Workspace));

    [Fact]
    public void Explicit_argument_wins_over_volume_config()
        => With(new Dictionary<string, string?>(), new PersistedSettings(Workspace: "UI Workspace"),
                () => Assert.Equal("Arg Workspace", DaxterConfig.FromEnvironment(workspace: "Arg Workspace").Workspace));

    [Fact]
    public void Env_fills_in_when_volume_config_absent()
        => With(new Dictionary<string, string?> { [DaxterConfig.EnvWorkspace] = "Env Workspace" }, saved: null,
                () => Assert.Equal("Env Workspace", DaxterConfig.FromEnvironment().Workspace));

    [Fact]
    public void Garbled_config_falls_back_to_env_without_throwing()
        => With(new Dictionary<string, string?> { [DaxterConfig.EnvWorkspace] = "Env Workspace" }, saved: null, () =>
        {
            File.WriteAllText(PersistedSettings.FilePath, "{ not json");
            Assert.Equal("Env Workspace", DaxterConfig.FromEnvironment().Workspace);
        });
}
