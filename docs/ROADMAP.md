# DAXter Roadmap

> DAXter is evolving from an XMLA query client into a **Power BI Service CLI** for
> querying, maintenance, and metadata across XMLA, the Power BI REST API, and the
> Admin/Scanner API. One service-principal token covers XMLA + REST; Admin needs
> elevated rights.

## API legend

| Tag | Source | Notes |
|-----|--------|-------|
| **X** | XMLA (ADOMD) | Inside a model: query, DMV, TMSL refresh |
| **T** | TOM (AMO) | Model object model: export/diff/edit |
| **R** | REST API | Workspace/dataset/report inventory, refresh, gateways |
| **A** | Admin/Scanner API | Tenant-wide; needs admin + security-group membership |
| **L** | Local | Config/profiles, no API call |

Effort: S = hours · M = half-day · L = day+ · Write = mutates/compute (needs `--yes`/`--dry-run`).

## Capability matrix

| # | Capability | API | Effort | Status / notes |
|---|-----------|-----|--------|---------------------|
| 1 | Switch environment (dev/qa/prod) | L | S | ✅ `--env` / `DAXTER_ENV` + `DAXTER_WORKSPACE_<ENV>`; `env ls` |
| 2 | Switch workspace | L+R | S | ✅ `--workspace`; REST `ws ls` pending (Phase 3) |
| 3 | Get Group (workspace) Id & Database (dataset) Id | R/X | S | ◑ dataset DATABASE_ID via DMV done; group id → REST (Phase 3) |
| 4 | List tables of a model | X | ✅ | `ls` |
| 5 | Execute DAX query | X | ✅ | `query` |
| 6 | Measure definitions (name + DAX) | X | S | ✅ `model measures [--with-expr]`, `model measure <n>` |
| 7 | M code for a table | X | S | ✅ `model mcode --table` |
| 8 | Parameters in model | X/T | M | ✅ `model parameters` (shared M expressions) |
| 9 | RLS settings (roles, filters, members) | X | M | ✅ `model rls [--role]` |
| 10 | Refresh info / last refreshed | X+R | M | ◑ XMLA `model partitions` (RefreshedTime) done; REST history (Phase 2) |
| 11 | Refresh all tables (full model) | R/X | M·Write | REST trigger (simple) or TMSL |
| 12 | Refresh partitions newest→oldest | X | M·Write | Enumerate partitions, TMSL process in order |
| 13 | Clear cache | X | S·Write | TMSL `ClearCache` |
| 14 | Download model metadata (save-as .bim) | T | M | TOM serialize → BIM/TMSL/TMDL |
| 15 | Compare two models (measure/object diff) | T | L | Build on #14 export + structural diff |
| 16 | Test RLS (impersonate user, check filter) | X | M | `Roles` + `EffectiveUserName` conn props; needs admin on model |
| 17 | Permissions for a model (who has access) | R | M | REST dataset/workspace users; needs workspace admin |
| 18 | Gateway settings / datasources | R | M | REST gateways + bound datasources |
| 19 | Deployment-pipeline rules per environment | R | M | **NEEDS CONFIRM** — Pipelines API stage rules |
| 20 | Tenant inventory / audit / orphans | A | L | Scanner + Activity API; admin only |
| 21 | Model editing (create/alter/delete) | T | L·Write | Deferred — mini Tabular Editor; high risk |

## Proposed command surface

```
daxter env use <dev|qa|prod> | env ls            # 1  profiles
daxter query / dmv / ls                          # 4,5 (done)
daxter model measures [--with-expr]              # 6
daxter model mcode --table <T>                   # 7
daxter model parameters                          # 8
daxter model rls [--role <R>]                    # 9
daxter model partitions --table <T>              # 10 (last refreshed)
daxter model export [--format bim|tmdl|tmsl]     # 14
daxter model diff <modelA> <modelB>              # 15
daxter model ids [--workspace N --dataset M]     # 3
daxter refresh model [--yes --dry-run]           # 11
daxter refresh table --table <T> [--yes]         # 11
daxter refresh partitions --table <T> --order newest-first  # 12
daxter refresh history [--top N]                 # 10
daxter cache clear [--yes]                        # 13
daxter test-rls --role <R> --user <upn> [-q DAX]  # 16
daxter ws ls | ws datasets | ws reports | ws lineage   # 2, inventory
daxter ws permissions [--dataset <D>]            # 17
daxter ws gateways                               # 18
daxter pipeline rules [--stage <s>]              # 19 (pending confirm)
daxter admin inventory | scan | activity | orphans  # 20 (optional)
```

## Phasing (value- and dependency-ordered)

- **Phase 0 — Foundations:** ✅ DONE — environment profiles (#1), workspace switch (#2), dataset-id resolution (#3 XMLA half).
- **Phase 1 — Model metadata (read, XMLA):** ✅ DONE — measures+expr (#6), M code (#7), parameters (#8), RLS settings (#9), partitions/last-refresh (#10 XMLA half). Validated live against a Premium workspace.
- **Phase 2 — Maintenance:** ✅ DONE — refresh model/table (#11), partition newest-first (#12), clear cache (#13), refresh trigger + history via REST (#10 REST half). `--dry-run`/`--yes`/`--force` rails. Validated live (history + dry-run).
- **Phase 3 — REST inventory:** ✅ DONE — `ws ls/datasets/reports/lineage/permissions/gateways/datasources`. Validated live (lineage + reports). Resolves group id #3 (REST half).
- **Phase 4 — Export & diff:** ✅ DONE — `model export` (.bim via TOM, validated live on Linux), `model diff` (measure differences via DMV). Added Microsoft.AnalysisServices (AMO/TOM).
- **Phase 5 — Test RLS:** ✅ DONE — `test-rls` via XMLA `Roles`/`EffectiveUserName`. Path validated live (reached engine under a test role); full use requires the connecting identity to be a workspace/model admin.
- **Phase 6 — Pipelines:** ✅ DONE — `pipeline ls/stages/operations` (#19, the env→workspace mapping). Fine-grained parameter rules and Admin/Scanner audit (#20) deferred (need a Fabric admin identity; the public API doesn't expose per-artifact rules cleanly).
- **Deferred — Model editing (#21)** (write, opt-in; intentionally out of scope for now).

**Status: Phases 0–6 complete and shipped in v1.0.0.**

## Cross-cutting

- **Auth:** existing MSAL token works for XMLA + REST (scope `…/powerbi/api/.default`). Admin API needs the SP in the Fabric admin API security group + tenant setting enabled.
- **Write safety (rules D3/E4):** refresh/clear-cache/edit require confirmation; on PROD require explicit approval. Default `--dry-run` shows the TMSL/REST call before executing.
- **XMLA read vs read-write:** TMSL refresh/edit need the capacity's XMLA endpoint set to **Read/Write**. REST refresh works with read-only XMLA.

## Decisions (resolved)

1. **#19 "Rules for an environment"** → **deployment-pipeline rules** (REST Pipelines API), Phase 6.
2. **Profiles** → **same service principal, workspace per environment** (`DAXTER_WORKSPACE_<ENV>`). Implemented in Phase 0.
3. **Admin scope** → SP is **member/contributor** on workspaces, **not** a tenant admin. Phase 6 tenant-wide audit (#20) stays deferred; everything workspace-scoped is fine.
4. **Refresh default** → REST trigger for whole-model refresh (no XMLA write needed); TMSL for partition-level newest→oldest control. To build in Phase 2.
