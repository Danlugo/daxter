namespace Daxter.Core.Configuration;

/// <summary>
/// The single authorization gate for every DAXter surface (CLI / MCP / Web). Resolves the
/// <see cref="PermissionLevel"/> in effect for a given target workspace and answers
/// <see cref="Allows"/> for an operation's required level.
///
/// <para><b>Two inputs, composed by <c>min</c>:</b></para>
/// <list type="number">
/// <item><b>Env ceiling</b> — <c>DAXTER_LEVEL</c> sets the instance ceiling (unset ⇒ <see cref="PermissionLevel.Full"/>,
/// i.e. no cap, for a local owner). <c>DAXTER_WORKSPACE_LEVELS</c> (<c>pattern=level;…</c>) caps specific
/// workspaces lower (or higher, up to its own value); when several patterns match, the
/// <b>most-restrictive</b> wins. The env ceiling can NOT be exceeded from inside the container.</item>
/// <item><b>Active level</b> — what the operator has turned on (the Configure-page toggle, persisted to
/// <c>console-config.json</c>). Defaults to <see cref="PermissionLevel.Read"/> (safe first-run) and can be
/// raised up to — never past — the env ceiling.</item>
/// </list>
/// <c>Effective(ws) = min(active, envCeiling(ws))</c>.
///
/// <para>The DAXter-local artifact/context scratch store is governed by the separate
/// <see cref="LocalLevel"/> (<c>DAXTER_LOCAL</c>, default <see cref="PermissionLevel.Full"/>) — it's not
/// part of the estate scale.</para>
/// </summary>
public sealed class PermissionPolicy
{
    public const string EnvLevel = "DAXTER_LEVEL";
    public const string EnvWorkspaceLevels = "DAXTER_WORKSPACE_LEVELS";
    public const string EnvLocal = "DAXTER_LOCAL";

    private readonly PermissionLevel _globalCeiling;
    private readonly IReadOnlyList<(string Pattern, PermissionLevel Level)> _workspaceCeilings;
    private readonly PermissionLevel _active;

    /// <summary>The level allowed for the DAXter-local scratch store (artifacts/context). Separate from
    /// the estate scale; default <see cref="PermissionLevel.Full"/>.</summary>
    public PermissionLevel LocalLevel { get; }

    public PermissionPolicy(
        PermissionLevel globalCeiling,
        IReadOnlyList<(string Pattern, PermissionLevel Level)> workspaceCeilings,
        PermissionLevel active,
        PermissionLevel localLevel)
    {
        _globalCeiling = globalCeiling;
        _workspaceCeilings = workspaceCeilings;
        _active = active;
        LocalLevel = localLevel;
    }

    /// <summary>Builds the policy from environment + the console's persisted active level.
    /// <paramref name="activeOverride"/> lets the Web layer pass the live <c>ConfigState</c> value;
    /// when null, the persisted <c>console-config.json</c> level is used.</summary>
    public static PermissionPolicy Load(PermissionLevel? activeOverride = null)
    {
        var levelEnv = Environment.GetEnvironmentVariable(EnvLevel);
        var globalCeiling = PermissionLevels.ParseOr(levelEnv, PermissionLevel.Full);
        var local = PermissionLevels.ParseOr(
            Environment.GetEnvironmentVariable(EnvLocal), PermissionLevel.Full);
        var wsCeilings = ParseWorkspaceLevels(Environment.GetEnvironmentVariable(EnvWorkspaceLevels));

        // Active level precedence: explicit override (Web ConfigState) ▸ persisted console level ▸
        // the env ceiling when DAXTER_LEVEL was set (headless intent) ▸ the safe floor (read).
        PermissionLevel active;
        if (activeOverride is { } o)
        {
            active = o;
        }
        else if (PermissionLevels.TryParse(PersistedSettings.Load().Level, out var persisted))
        {
            active = persisted;
        }
        else
        {
            active = !string.IsNullOrWhiteSpace(levelEnv) ? globalCeiling : PermissionLevel.Read;
        }

        return new PermissionPolicy(globalCeiling, wsCeilings, active, local);
    }

    /// <summary>The hard ceiling the env imposes on a workspace (most-restrictive matching pattern, else
    /// the global ceiling). Independent of the operator's active level.</summary>
    public PermissionLevel EnvCeiling(string? workspace)
    {
        var ceiling = _globalCeiling;
        var matched = false;
        if (!string.IsNullOrWhiteSpace(workspace))
        {
            foreach (var (pattern, level) in _workspaceCeilings)
            {
                if (!WorkspaceMatcher.Matches(workspace!, pattern)) continue;
                ceiling = matched ? Min(ceiling, level) : level;   // most-restrictive among matches
                matched = true;
            }
        }
        return ceiling;
    }

    /// <summary>The level actually in effect for a workspace: <c>min(active, envCeiling)</c>.</summary>
    public PermissionLevel Effective(string? workspace) => Min(_active, EnvCeiling(workspace));

    /// <summary>True when an operation needing <paramref name="required"/> is permitted against
    /// <paramref name="workspace"/>.</summary>
    public bool Allows(PermissionLevel required, string? workspace) => Effective(workspace) >= required;

    /// <summary>True when a DAXter-local scratch operation needing <paramref name="required"/> is permitted.</summary>
    public bool AllowsLocal(PermissionLevel required) => LocalLevel >= required;

    /// <summary>An actionable refusal string for a blocked estate operation.</summary>
    public string Refusal(PermissionLevel required, string? workspace)
    {
        var eff = Effective(workspace);
        var where = string.IsNullOrWhiteSpace(workspace) ? "this instance" : $"'{workspace}'";
        var cap = EnvCeiling(workspace) < _active
            ? $" (env ceiling {EnvCeiling(workspace).Token()})"
            : "";
        return $"REFUSED — this needs the '{required.Token()}' permission level; {where} is at " +
               $"'{eff.Token()}'{cap}. Raise the level in the web console (Configure → Permission level) " +
               $"or set {EnvLevel}, and target a workspace allowed to {required.Token()}.";
    }

    /// <summary>Parses <c>DAXTER_WORKSPACE_LEVELS</c>: <c>pattern=level;pattern=level</c> (also accepts
    /// commas). Unparseable entries are skipped.</summary>
    public static List<(string Pattern, PermissionLevel Level)> ParseWorkspaceLevels(string? raw)
    {
        var result = new List<(string, PermissionLevel)>();
        if (string.IsNullOrWhiteSpace(raw)) return result;

        foreach (var entry in raw.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = entry.LastIndexOf('=');
            if (eq <= 0 || eq >= entry.Length - 1) continue;
            var pattern = entry[..eq].Trim();
            if (pattern.Length > 0 && PermissionLevels.TryParse(entry[(eq + 1)..], out var level))
            {
                result.Add((pattern, level));
            }
        }
        return result;
    }

    private static PermissionLevel Min(PermissionLevel a, PermissionLevel b) => a < b ? a : b;
}
