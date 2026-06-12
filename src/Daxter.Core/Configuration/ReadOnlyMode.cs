namespace Daxter.Core.Configuration;

/// <summary>
/// Master read-only kill-switch. When <c>DAXTER_READONLY</c> is truthy, every <b>structural</b>
/// mutation — model edits, schedule changes, cache clear, SQL writes, gateway binds, takeover — is
/// refused across all surfaces (CLI, MCP, Web). It overrides the Allow-writes / Allow-model-edits
/// gates and can't be re-enabled from inside the container.
/// <para>
/// <b>Refresh is the deliberate exception.</b> A read-only instance can still trigger refreshes
/// (model / table / partition, REST trigger, resume, apply-refresh-policy) so it can keep data
/// current — but refreshes continue to honour the Production / read-only-workspace guardrail
/// (<c>DAXTER_READONLY_WORKSPACES</c> / <c>DAXTER_WRITE_WORKSPACES</c> / prod-block), exactly as in
/// normal mode. Reads, queries, exports, and audits are always unaffected.
/// </para>
/// The DAXter-local artifact + context scratch stores are not Power BI / Fabric mutations and are
/// intentionally out of scope of this switch.
/// </summary>
public static class ReadOnlyMode
{
    /// <summary>Environment variable that enables read-only mode.</summary>
    public const string EnvVar = "DAXTER_READONLY";

    /// <summary>Refusal message surfaced when a blocked mutation is attempted.</summary>
    public const string Message =
        "DAXter is in READ-ONLY mode (DAXTER_READONLY) — model edits, schedule changes, cache clear, " +
        "SQL writes, and gateway/ownership changes are disabled. Reads, queries, exports, audits, and " +
        "refreshes (which still respect the Production-workspace rules) remain available.";

    /// <summary>True when <c>DAXTER_READONLY</c> is set to a truthy value (true/1/yes/on).</summary>
    public static bool IsEnabled
    {
        get
        {
            var v = Environment.GetEnvironmentVariable(EnvVar)?.Trim().ToLowerInvariant();
            return v is "true" or "1" or "yes" or "on";
        }
    }
}
