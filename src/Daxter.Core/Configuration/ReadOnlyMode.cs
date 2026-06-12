namespace Daxter.Core.Configuration;

/// <summary>
/// v1.46.0 — thin compatibility shim over the permission model. <see cref="IsEnabled"/> now means
/// "the effective permission level is <see cref="PermissionLevel.Read"/>" (resolved from
/// <see cref="PermissionPolicy"/> — i.e. <c>DAXTER_LEVEL=read</c> or a console level of read). The old
/// standalone <c>DAXTER_READONLY</c> env var is gone; use <c>DAXTER_LEVEL=read</c> instead.
/// Kept so existing call sites compile; new gates should use <see cref="PermissionPolicy.Allows"/>
/// with an explicit <see cref="PermissionLevel"/>.
/// </summary>
public static class ReadOnlyMode
{
    /// <summary>True when the effective permission level (no specific workspace) is read-only.</summary>
    public static bool IsEnabled => PermissionPolicy.Load().Effective(null) == PermissionLevel.Read;

    /// <summary>Refusal text for a blocked mutation under read level.</summary>
    public static string Message =>
        "DAXter is at the READ permission level (DAXTER_LEVEL=read) — every mutation is disabled. " +
        "Reads, queries, exports, and audits remain available. Raise DAXTER_LEVEL or the console " +
        "Permission level to read+execute (refresh), modify, or full.";
}
