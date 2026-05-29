# DAXter — Product Plan

> **DAXter** is a cross-platform command-line tool for the **Power BI Service**.
> Query, document, and operate semantic models from macOS / Linux / CI — no Windows,
> no local .NET install, just a portable container.

## 1. Positioning

**The problem.** Power BI's pro tooling — Tabular Editor, DAX Studio, SSMS, Profiler —
is Windows-only, and ADOMD.NET's interactive sign-in is Windows-only too. Mac/Linux
users, and automated pipelines, are effectively locked out of programmatic model access.

**The solution.** DAXter acquires an Entra ID token itself (MSAL) and injects it into the
XMLA connection, then layers the Power BI REST API on top — all packaged as a single
multi-stage Docker image. One artifact you can run anywhere or hand to a teammate.

**One-liner:** *"Tabular Editor / DAX Studio operations, scriptable and cross-platform."*

## 2. Product modules (grouped by user intent)

| Module | Intent | Commands | API | Persona |
|--------|--------|----------|-----|---------|
| **Query** | Ad-hoc analytics & exploration | `query`, `dmv`, `ls` | XMLA | Analyst / Dev |
| **Model** | Understand, document & diff models | `model measures/measure/mcode/parameters/rls/partitions`, `model export`, `model diff` | XMLA + TOM | BI Developer |
| **Ops** | Maintenance & operations | `refresh model/table/partitions`, `refresh history`, `cache clear` | XMLA(TMSL) + REST | BI Ops / Platform |
| **Workspace** | Inventory & governance | `ws ls/datasets/reports/lineage/permissions/gateways`, `pipeline rules` | REST | Platform / Governance |
| **Test** | Validation | `test-rls` (impersonate a user) | XMLA | BI Dev / QA |
| **Admin** | Tenant audit (deferred) | `admin inventory/scan/activity/orphans` | Admin/Scanner | Fabric Admin |
| **Foundations** | Cross-cutting DevEx | env profiles, auth, output formats, config | Local | All |

## 3. Personas & journeys

- **Analyst (ad-hoc):** "On my Mac, pull numbers / check a measure." → `query`, `model measure`.
- **BI Developer (document / promote):** "Document a model, diff DEV vs PROD before
  promotion, test RLS for a user." → `model export/diff`, `test-rls`, `--env`.
- **BI Ops (maintenance):** "After the nightly load, refresh partitions newest-first,
  verify refresh history, clear cache." → `refresh*`, runs in CI/cron.
- **Platform / Governance:** "What exists, who has access, which gateway, what pipeline
  rules per stage." → `ws *`, `pipeline rules`.

## 4. Architecture (engineering story)

```
            ┌──────────────┐
   CLI  →   │ Daxter.Cli   │  System.CommandLine, output formatters
            └──────┬───────┘
                   │ depends on
            ┌──────▼───────┐   Auth (MSAL token)      ┌── XMLA  (ADOMD/TOM)
            │ Daxter.Core  │ ─ Connection (sessions) ─┤── REST  (HttpClient)
            │  (engine)    │   Metadata / Query        └── Admin (Scanner)
            └──────────────┘
```

- **Layered:** `Daxter.Core` is a UI-free engine; `Daxter.Cli` is a thin shell. The engine
  is reusable (could back a REST service or GUI later).
- **Testable:** interfaces (`ITokenProvider`, `IXmlaSession`, `IXmlaSessionFactory`) + fakes →
  unit tests with no live endpoint.
- **Multi-API:** one auth token spans XMLA + REST; Admin gated behind elevated rights.
- **Secure by design:** OAuth/MSAL, token injection (cross-platform), token cache volume,
  no secrets in the image, non-root container, `.env` gitignored.
- **Distribution:** multi-stage build runs the test suite; a failing test fails the image.

## 5. Release plan

| Version | Theme | Status |
|---------|-------|--------|
| v0.1 | Query module + Docker + auth + tests | ✅ done |
| v0.2 | Foundations (env profiles) + Model metadata (read) | ✅ done |
| v0.3 | **Ops:** refresh (model/table/partitions newest-first), cache clear, refresh history (REST) | next |
| v0.4 | **Workspace:** REST inventory, lineage, permissions, gateways | planned |
| v0.5 | **Model export** (.bim/tmdl/tmsl) + **model diff** (TOM) | planned |
| v0.6 | **Test-RLS**; deployment-pipeline rules | planned |
| v1.0 | Polish, docs, demo, CI/CD, **public release** | goal |
| v1.x+ | Admin/Scanner audit; model editing (write, Tier 3) | future |

**Write-safety (rules D3/E4):** every mutating op (`refresh`, `cache clear`, future edits)
ships with `--dry-run` (prints the exact TMSL/REST call) and `--yes` confirmation; PROD
requires explicit opt-in.

## 6. Portfolio / publication checklist (github.com/Danlugo/daxter)

**Repo presentation**
- [ ] Hero README: one-liner, problem, **demo GIF/asciinema**, quickstart, feature matrix
- [ ] `docs/ARCHITECTURE.md` with the diagram above
- [ ] `LICENSE` (MIT), `CHANGELOG.md` (Keep a Changelog), `CONTRIBUTING.md`
- [ ] Topics: `powerbi` `xmla` `dax` `dotnet` `cli` `fabric` `docker`
- [ ] Issue/PR templates; a project board mirroring this roadmap

**Engineering signal**
- [ ] GitHub Actions CI: build → test → docker build → (publish image to GHCR)
- [ ] Badges: build, tests/coverage, license, image
- [ ] Conventional commits + semantic-version tags + GitHub Releases
- [ ] Clean, narrated commit history

**Confidentiality scrub (MUST, before first push)**
- [ ] Remove all client specifics (workspace/model/tenant names) from docs & samples
- [ ] Confirm `.env` is gitignored and never committed; scrub history if needed
- [ ] `.env.example` uses generic placeholders only

## 7. Skills demonstrated (résumé talking points)

.NET 8 / C#, clean layered architecture, dependency injection, xUnit testing · Docker
multi-stage & non-root images · OAuth2 / MSAL / Entra ID auth · Power BI XMLA (ADOMD/TOM)
+ REST API integration · CLI UX design (System.CommandLine) · CI/CD · cross-platform
engineering · security-by-design.
