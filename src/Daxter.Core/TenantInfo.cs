namespace Daxter.Core;

// ──────────────────────────────────────────────────────────────────────────────────────────────
// Tenant identity for DAXter — the small set of fields a fleet orchestrator (Semantics, the
// per-client DAXter-spinner) wants visible across every response shape that identifies the
// running instance.
//
// CONTEXT. DAXter today is one-DAXter-per-machine (or per-laptop). The artifact store and
// context plane are naturally tenant-scoped because they live in the container's volume.
// Semantics runs many DAXter containers, one per client; each container needs an identity
// stamp so the fleet UI / fleet alarms / fleet inventory can pin which response came from
// which tenant without docker-exec scraping.
//
// CONTRACT.
//   DAXTER_TENANT_ID — opaque short string the orchestrator picks (e.g. "inspire", "acme").
//                      No semantic meaning to DAXter; just a sticker.
//   DAXTER_LABEL     — free-form human-readable label (e.g. "Inspire Brands — Prod").
//                      Surfaced on the Web home page banner and in `daxter_version`.
//
// Both are OPTIONAL — local laptop users won't set either and should see the existing
// behaviour (responses omit the fields entirely; the home page hides the banner).
// ──────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>The Semantics-friendly identity card: opaque tenant id + free-form label. Both
/// optional; both surfaced on every "what / who am I" tool so fleet orchestration can pin
/// responses without exec'ing into the container.</summary>
public static class TenantInfo
{
    /// <summary>Opaque tenant id from <c>DAXTER_TENANT_ID</c>; null when unset (single-tenant
    /// local use). No semantic meaning — Semantics picks the convention, DAXter just echoes it.</summary>
    public static string? TenantId
        => Trim(Environment.GetEnvironmentVariable("DAXTER_TENANT_ID"));

    /// <summary>Free-form human label from <c>DAXTER_LABEL</c> (e.g. "Inspire Brands — Prod").
    /// Shown on the Web home page banner when set; included in daxter_version JSON. Null when
    /// unset so the local-laptop UX doesn't change.</summary>
    public static string? Label
        => Trim(Environment.GetEnvironmentVariable("DAXTER_LABEL"));

    /// <summary>True when EITHER tenant_id OR label is set — i.e. the container is running
    /// under a fleet orchestrator. The Home page uses this to decide whether to show the
    /// label banner.</summary>
    public static bool IsConfigured => TenantId is not null || Label is not null;

    /// <summary>Embed the tenant fields into a response envelope, omitting unset ones. Returned
    /// as <see cref="IDictionary{TKey,TValue}"/> rather than a record so JSON serialisation
    /// flattens the keys naturally without an extra nested object — and so callers can merge
    /// into their existing dictionary-shaped responses (daxter_version, daxter_capabilities,
    /// /api/health) without a refactor.</summary>
    public static void MergeInto(IDictionary<string, object?> target)
    {
        if (TenantId is { } id) target["tenant_id"] = id;
        if (Label is { } label) target["label"] = label;
    }

    private static string? Trim(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
