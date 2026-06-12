namespace Daxter.Core.Configuration;

/// <summary>
/// DAXter's single ordered capability scale. Each level <b>includes everything below it</b>, so a
/// numeric compare (<c>effective &gt;= required</c>) is the whole authorization check.
/// <list type="bullet">
/// <item><see cref="Read"/> — query / inspect / export / audit / sign-in. No changes of any kind.</item>
/// <item><see cref="Execute"/> — + run operations that don't change definitions: refresh, resume,
/// apply-refresh-policy, run notebook / copy-job, cancel a job, clear cache.</item>
/// <item><see cref="Modify"/> — + alter EXISTING objects/config: edit measures/columns/roles/params,
/// set the refresh schedule, SQL UPDATE/INSERT/MERGE, bind gateway, take over, update item definitions.</item>
/// <item><see cref="Full"/> — + create new objects and delete: create/delete measures/columns/tables/roles,
/// SQL DELETE/DROP/TRUNCATE.</item>
/// </list>
/// The DAXter-local artifact/context scratch store is governed separately (<c>DAXTER_LOCAL</c>), not by
/// this estate scale — it's DAXter's own working memory, not the customer's Power BI / Fabric assets.
/// </summary>
public enum PermissionLevel
{
    Read = 0,
    Execute = 1,
    Modify = 2,
    Full = 3,
}

/// <summary>Parsing + display helpers for <see cref="PermissionLevel"/>.</summary>
public static class PermissionLevels
{
    /// <summary>Canonical lowercase token (<c>read</c> / <c>execute</c> / <c>modify</c> / <c>full</c>).</summary>
    public static string Token(this PermissionLevel level) => level switch
    {
        PermissionLevel.Read => "read",
        PermissionLevel.Execute => "execute",
        PermissionLevel.Modify => "modify",
        PermissionLevel.Full => "full",
        _ => "read",
    };

    /// <summary>Human label for the Configure page / refusal messages.</summary>
    public static string Label(this PermissionLevel level) => level switch
    {
        PermissionLevel.Read => "read",
        PermissionLevel.Execute => "read + execute",
        PermissionLevel.Modify => "read + modify + execute",
        PermissionLevel.Full => "full (add / modify / delete / execute)",
        _ => "read",
    };

    /// <summary>Parses a level token. Accepts the canonical tokens plus a few friendly aliases and the
    /// cumulative spellings (<c>read+execute</c> etc.). Returns false for anything unrecognised.</summary>
    public static bool TryParse(string? value, out PermissionLevel level)
    {
        level = PermissionLevel.Read;
        switch ((value ?? "").Trim().ToLowerInvariant().Replace(" ", ""))
        {
            case "read" or "r" or "0":
                level = PermissionLevel.Read; return true;
            case "execute" or "read+execute" or "exec" or "x" or "operate" or "1":
                level = PermissionLevel.Execute; return true;
            case "modify" or "read+modify+execute" or "read+modify" or "write" or "edit" or "w" or "2":
                level = PermissionLevel.Modify; return true;
            case "full" or "admin" or "all" or "owner" or "3":
                level = PermissionLevel.Full; return true;
            default:
                return false;
        }
    }

    /// <summary>Parses a level token, falling back to <paramref name="fallback"/> when unset/invalid.</summary>
    public static PermissionLevel ParseOr(string? value, PermissionLevel fallback)
        => TryParse(value, out var l) ? l : fallback;
}
