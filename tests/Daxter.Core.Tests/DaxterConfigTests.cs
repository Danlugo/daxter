using Daxter.Core;
using Daxter.Core.Auth;
using Daxter.Core.Configuration;

namespace Daxter.Core.Tests;

// Env-var manipulation must not run in parallel with other env-touching tests.
[Collection("EnvSerial")]
public class DaxterConfigTests
{
    private static readonly string[] AllVars =
    [
        DaxterConfig.EnvWorkspace, DaxterConfig.EnvDataset, DaxterConfig.EnvTenantId,
        DaxterConfig.EnvClientId, DaxterConfig.EnvClientSecret, DaxterConfig.EnvAuthMode,
        DaxterConfig.EnvEnvironment, DaxterConfig.EnvProdWorkspaces,
        "DAXTER_WORKSPACE_DEV", "DAXTER_WORKSPACE_QA", "DAXTER_DATASET_DEV",
    ];

    private static void WithEnv(IDictionary<string, string?> values, Action body)
    {
        var saved = AllVars.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var v in AllVars)
            {
                Environment.SetEnvironmentVariable(
                    v, values.TryGetValue(v, out var val) ? val : null);
            }

            body();
        }
        finally
        {
            foreach (var (k, v) in saved)
            {
                Environment.SetEnvironmentVariable(k, v);
            }
        }
    }

    [Fact]
    public void FromEnvironment_throws_when_workspace_missing()
    {
        WithEnv(new Dictionary<string, string?>(), () =>
            Assert.Throws<DaxterException>(() => DaxterConfig.FromEnvironment()));
    }

    [Fact]
    public void FromEnvironment_reads_workspace_and_defaults_to_device_code()
    {
        WithEnv(new Dictionary<string, string?> { [DaxterConfig.EnvWorkspace] = "WS" }, () =>
        {
            var config = DaxterConfig.FromEnvironment();
            Assert.Equal("WS", config.Workspace);
            Assert.Equal(AuthMode.DeviceCode, config.AuthMode);
        });
    }

    [Fact]
    public void Explicit_arguments_override_environment()
    {
        WithEnv(new Dictionary<string, string?> { [DaxterConfig.EnvWorkspace] = "EnvWs" }, () =>
        {
            var config = DaxterConfig.FromEnvironment(workspace: "ArgWs", dataset: "ArgDs");
            Assert.Equal("ArgWs", config.Workspace);
            Assert.Equal("ArgDs", config.Dataset);
        });
    }

    [Theory]
    [InlineData("service-principal", AuthMode.ServicePrincipal)]
    [InlineData("sp", AuthMode.ServicePrincipal)]
    [InlineData("device-code", AuthMode.DeviceCode)]
    public void FromEnvironment_parses_auth_mode(string raw, AuthMode expected)
    {
        WithEnv(new Dictionary<string, string?>
        {
            [DaxterConfig.EnvWorkspace] = "WS",
            [DaxterConfig.EnvAuthMode] = raw,
        }, () => Assert.Equal(expected, DaxterConfig.FromEnvironment().AuthMode));
    }

    [Fact]
    public void FromEnvironment_rejects_unknown_auth_mode()
    {
        WithEnv(new Dictionary<string, string?>
        {
            [DaxterConfig.EnvWorkspace] = "WS",
            [DaxterConfig.EnvAuthMode] = "bogus",
        }, () => Assert.Throws<DaxterException>(() => DaxterConfig.FromEnvironment()));
    }

    [Fact]
    public void Validate_service_principal_requires_all_credentials()
    {
        var config = new DaxterConfig { Workspace = "WS", AuthMode = AuthMode.ServicePrincipal };
        var ex = Assert.Throws<DaxterException>(config.Validate);
        Assert.Contains(DaxterConfig.EnvTenantId, ex.Message);
        Assert.Contains(DaxterConfig.EnvClientId, ex.Message);
        Assert.Contains(DaxterConfig.EnvClientSecret, ex.Message);
    }

    [Fact]
    public void Validate_passes_for_complete_service_principal()
    {
        var config = new DaxterConfig
        {
            Workspace = "WS",
            AuthMode = AuthMode.ServicePrincipal,
            TenantId = "t",
            ClientId = "c",
            ClientSecret = "s",
        };

        config.Validate(); // should not throw
    }

    [Fact]
    public void Validate_passes_for_device_code_without_credentials()
    {
        new DaxterConfig { Workspace = "WS", AuthMode = AuthMode.DeviceCode }.Validate();
    }

    [Fact]
    public void IsProductionTarget_true_when_env_is_prod()
        => Assert.True(new DaxterConfig { Workspace = "Some WS", Environment = "prod" }.IsProductionTarget());

    [Fact]
    public void IsProductionTarget_true_when_name_contains_prod()
        => Assert.True(new DaxterConfig { Workspace = "Sales - Prod" }.IsProductionTarget());

    [Fact]
    public void IsProductionTarget_true_when_listed()
        => Assert.True(new DaxterConfig
        {
            Workspace = "Sales Analytics",
            ProdWorkspaces = ["Sales Analytics", "Marketing"],
        }.IsProductionTarget());

    [Fact]
    public void IsProductionTarget_false_for_dev_even_when_prod_listed()
        => Assert.False(new DaxterConfig
        {
            Workspace = "Sales Analytics - Dev",   // exact match only — not flagged by "Sales Analytics" in the list
            Environment = "dev",
            ProdWorkspaces = ["Sales Analytics"],
        }.IsProductionTarget());

    [Fact]
    public void FromEnvironment_parses_prod_workspaces()
    {
        WithEnv(new Dictionary<string, string?>
        {
            [DaxterConfig.EnvWorkspace] = "WS",
            [DaxterConfig.EnvProdWorkspaces] = "Sales Analytics, Marketing",
        }, () =>
        {
            var config = DaxterConfig.FromEnvironment();
            Assert.Contains("Sales Analytics", config.ProdWorkspaces);
            Assert.Contains("Marketing", config.ProdWorkspaces);
        });
    }

    [Fact]
    public void Active_environment_selects_per_env_workspace_and_dataset()
    {
        WithEnv(new Dictionary<string, string?>
        {
            [DaxterConfig.EnvWorkspace] = "Fallback WS",
            ["DAXTER_WORKSPACE_DEV"] = "Sales Analytics - Dev",
            ["DAXTER_DATASET_DEV"] = "Retail Model",
            [DaxterConfig.EnvEnvironment] = "dev",
        }, () =>
        {
            var config = DaxterConfig.FromEnvironment();
            Assert.Equal("Sales Analytics - Dev", config.Workspace);
            Assert.Equal("Retail Model", config.Dataset);
            Assert.Equal("dev", config.Environment);
        });
    }

    [Fact]
    public void Per_env_falls_back_to_base_workspace_when_env_var_absent()
    {
        WithEnv(new Dictionary<string, string?>
        {
            [DaxterConfig.EnvWorkspace] = "Base WS",
            [DaxterConfig.EnvEnvironment] = "qa", // no DAXTER_WORKSPACE_QA defined
        }, () => Assert.Equal("Base WS", DaxterConfig.FromEnvironment().Workspace));
    }

    [Fact]
    public void Explicit_environment_argument_overrides_env_var()
    {
        WithEnv(new Dictionary<string, string?>
        {
            ["DAXTER_WORKSPACE_DEV"] = "Dev WS",
            [DaxterConfig.EnvEnvironment] = "qa",
        }, () => Assert.Equal("Dev WS", DaxterConfig.FromEnvironment(environment: "dev").Workspace));
    }

    [Fact]
    public void ListEnvironments_discovers_configured_profiles()
    {
        WithEnv(new Dictionary<string, string?>
        {
            ["DAXTER_WORKSPACE_DEV"] = "d",
            ["DAXTER_WORKSPACE_QA"] = "q",
        }, () =>
        {
            var envs = DaxterConfig.ListEnvironments();
            Assert.Contains("dev", envs);
            Assert.Contains("qa", envs);
        });
    }
}
