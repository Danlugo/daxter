using Daxter.Core.Configuration;

namespace Daxter.Core.Tests;

/// <summary>The v1.44.0 master read-only switch (<c>DAXTER_READONLY</c>). Pins the env-var parsing and
/// the ConfigState masking that every Web write consumer relies on. The MCP gate predicates
/// (WritesAllowed/RefreshAllowed/ModelEditAllowed) are internal to Daxter.Cli and exercised live in the
/// smoke test; here we lock the two public choke points: the switch itself and the console state.</summary>
[Collection("env-mutating")]
public sealed class ReadOnlyModeTests : IDisposable
{
    private readonly string? _original;

    public ReadOnlyModeTests() => _original = Environment.GetEnvironmentVariable(ReadOnlyMode.EnvVar);

    public void Dispose() => Environment.SetEnvironmentVariable(ReadOnlyMode.EnvVar, _original);

    [Theory]
    [InlineData("true", true)]
    [InlineData("1", true)]
    [InlineData("yes", true)]
    [InlineData("on", true)]
    [InlineData("TRUE", true)]
    [InlineData(" true ", true)]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsEnabled_parses_truthy_values(string? value, bool expected)
    {
        Environment.SetEnvironmentVariable(ReadOnlyMode.EnvVar, value);
        Assert.Equal(expected, ReadOnlyMode.IsEnabled);
    }

    [Fact]
    public void ConfigState_masks_AllowWrites_and_AllowModelEdit_under_readonly()
    {
        Environment.SetEnvironmentVariable(ReadOnlyMode.EnvVar, null);
        var state = new Web.Services.ConfigState { AllowWrites = true, AllowModelEdit = true };
        Assert.True(state.AllowWrites);
        Assert.True(state.AllowModelEdit);
        Assert.False(state.ReadOnly);

        Environment.SetEnvironmentVariable(ReadOnlyMode.EnvVar, "true");
        // Same saved toggles, but read-only mode now forces the public view to false.
        Assert.True(state.ReadOnly);
        Assert.False(state.AllowWrites);
        Assert.False(state.AllowModelEdit);
    }
}
