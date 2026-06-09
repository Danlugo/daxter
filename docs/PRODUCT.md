# DAXter — Product Plan

> **DAXter** is a Docker-only **Power BI & Fabric** client that runs the same engine across
> **three surfaces** — a CLI, an **MCP server** (so Claude / any MCP client can use it in
> plain language), and a **web console** — from macOS, Linux, or Windows. No local .NET, no
> Windows-only tooling, one container image.

## 1. Positioning

**The problem.** Power BI's pro tooling — Tabular Editor, DAX Studio, SSMS, Profiler — is
Windows-only, and ADOMD.NET's interactive sign-in only works on Windows. Mac/Linux users,
agentic workflows, and CI/CD pipelines are locked out. Adding Fabric Warehouses, Lakehouses,
Copy Jobs, and Notebooks to the picture multiplies the gap: more APIs, more auth audiences,
no unified operator-friendly tool.

**The solution.** DAXter acquires Entra ID tokens itself (MSAL — including a separate
pre-authorized client id for the Fabric SQL endpoint to dodge AADSTS65002), then layers
**every Power BI / Fabric surface** on top of that token: XMLA (ADOMD/TOM), the Power BI
REST API, the Fabric REST API (Items / Job Scheduler / Copy Jobs / Notebooks /
bindConnection / Pipelines), and TDS to the Fabric Warehouse / Lakehouse SQL endpoint. All
packaged as a single multi-stage Docker image you can run anywhere, hand to a teammate,
or expose to Claude as an MCP server.

**One-liner today:** *"Tabular Editor + DAX Studio + the Fabric admin UI, scriptable,
cross-platform, and Claude-able."*

## 2. Three surfaces, one engine

```
                            Daxter.Core (the engine)
                            ─────────────────────────
                            Auth · XMLA · TOM · REST · TDS
                            Refresh queue · Editing service
                            Audit · Fabric SQL · Job runner
                                       ▲
                ┌──────────────────────┼──────────────────────┐
                │                      │                      │
        ┌───────┴──────┐       ┌───────┴───────┐      ┌──────┴──────┐
        │ Daxter.Cli   │       │ Daxter.Cli/Mcp │      │ Daxter.Web  │
        │  shell       │       │  MCP server    │      │  Blazor     │
        │  (System.    │       │  (76 tools)    │      │  console    │
        │  CommandLine)│       │                │      │ (12 pages)  │
        └──────────────┘       └────────────────┘      └─────────────┘
            terminal               Claude / any           browser
                                   MCP client
```

The CLI, the MCP tool layer, and the Web's `DaxterUi` bridge all call into **the same Core
service methods**. Adding a capability once means it shows up everywhere — that's the
`daxter-capability` skill in action. Three surfaces sharing one queue, one token cache, and
one set of tests.

## 3. Capability map (current — v1.30.x)

| Module | What it does | Surfaces |
|---|---|---|
| **Query** | DAX / MDX / DMV over XMLA · field autocomplete on the Web | CLI · MCP · `/query`, `/explore` |
| **Model metadata** | measures, M code, parameters, columns, partitions, relationships, RLS — TMSCHEMA DMVs with id→name joins | CLI · MCP · `/explore`, `/model-edit` |
| **Model export + diff** | TOM serialize → `.bim`; measure-level diff between two models | CLI · MCP |
| **Model editing** | Typed edits for measure / parameter / role / column / `edit-column` (format, data type, sort-by, summarize-by, folder, hidden) / source / calc-table / import-table / relationship + raw TMSL escape hatch. **Dry-run by default**, `.bim` backup before apply, gated by **Allow model edits** | CLI · MCP · `/model-edit` |
| **RLS viewer** | Roles tree + per-(role, table) DAX filter expression, syntax-highlighted; members list; copy-DAX. Read-only; edits go through Model Edit | CLI · MCP (`daxter_rls`, `daxter_role_filters`, `daxter_role_members`) · `/rls` |
| **Refresh** | model / table / partitions (newest-first or `--partitions` subset), Enhanced Refresh by default, server-side; `--engine xmla` falls back to TMSL. Resume-from-where-it-failed. Run history with status + duration | CLI · MCP · `/refresh`, `/jobs` |
| **Refresh scheduler** | One file-backed queue on the shared volume; one worker (in the Web container) drains it; CLI/MCP/UI enqueue and the queue is identical for all. `DAXTER_REFRESH_MAX_CONCURRENT_MODELS` tunes parallelism | shared by all surfaces |
| **Cache clear** | TMSL `ClearCache`, dry-run by default | CLI · MCP |
| **Workspace inventory** | groups, datasets, reports, lineage, **report inventory** (thin/thick + downloadable .pbix), **export report** (PBIR definition + .pbix), permissions, gateways, datasources | CLI · MCP · `/explore` |
| **Connections** | Per-model "Maps to" view; list shareable cloud connections; **take over** + **bind each source** (cloud or gateway) via the Fabric `bindConnection` API — supersedes the old gateway-only path | CLI · MCP · `/connections` |
| **Pipelines** | List · stages · operations · **deployment rules** inferred from per-stage parameter diffs (the public API doesn't expose them directly — we reconstruct from observed differences) · audit (models without rules, parameter sanity checks) | CLI · MCP · `/pipelines`, `/audit` |
| **Fabric SQL endpoints** | Discovery (Warehouses + Lakehouse SQL endpoints), **object explorer** (schemas → tables/views/functions/stored procedures), T-SQL execution (read-only by default; writes-gated), **streaming Export All CSV** that doesn't materialize in memory (verified: 802k rows in 9.4s, 14k-row export byte-equivalent to Power BI "Export data" output with `--quote-all --crlf`) | CLI · MCP (`daxter_sql_endpoints`, `daxter_sql_objects`, `daxter_sql_query`, `daxter_sql_export`) · `/sql` |
| **Fabric Copy Jobs + Notebooks** | List per workspace, view full definition (`copyjob-content.json` / `.ipynb`), run on demand (writes-gated, confirm modal), watch recent runs (status / duration / failure reason), cancel | CLI · MCP (9 tools) · `/copy-jobs`, `/notebooks` |
| **Test-RLS** | Run a query under a role / impersonated UPN (`Roles` + `EffectiveUserName` connection props) | CLI · MCP · `/rls` |
| **Writes gate** | Two-list model: read-only patterns (deny-list) + write-allowed patterns (allow-list) with `*` glob wildcards. Refuse messages tell the user WHICH rule matched. Legacy `DAXTER_PROD_WORKSPACES` still honored | shared by all surfaces |
| **Auth** | Device-code (cached) and service principal · separate one-time sign-in for the Fabric SQL scope (different pre-authorized client id) · tokens persist in the `daxter-tokens` volume · `DAXTER_PUBLIC_CLIENT_ID` / `DAXTER_SQL_CLIENT_ID` for BYO app | shared |
| **Foundations** | Environment profiles · output formats (table/CSV/JSON) · Frequent-sidebar per page · deep-link recall · busy overlay · Logs page · Jobs page · Configure live preview | CLI + Web |

## 4. Personas & journeys

- **Analyst (ad-hoc):** "On my Mac, pull numbers / check a measure / open a warehouse table."
  → `/query` · `daxter query` · `daxter_query` · `/sql` for T-SQL · `/explore` for fields.
- **BI Developer (document / promote):** "Document a model, diff DEV vs PROD before promotion,
  inspect RLS, edit a measure / format / sort-by, test-RLS as a user."
  → `/explore` · `model export/diff` · `/rls` · `/model-edit` · `test-rls`.
- **BI Ops (maintenance):** "After the nightly load, refresh partitions newest-first, watch the
  queue, kick off a Copy Job, monitor a Notebook run, clear cache, audit pipeline rules."
  → `/refresh` · `/jobs` · `/copy-jobs` · `/notebooks` · `/audit` · `daxter refresh*` + cron.
- **Agentic operator (Claude as a copilot):** "Ask Claude in plain language to do any of the
  above — sign in, list models, draft a measure, run a SQL export, run a notebook, monitor it."
  → MCP server, 76 tools, `daxter_capabilities` lists them all with read/write classification.
- **Platform / Governance:** "What exists, who has access, which gateway, which workspaces are
  writable, what pipeline rules per stage."
  → `/explore` · `/connections` · `/configure` (writes gate) · `pipeline rules` · `pipeline audit`.

## 5. Architecture overview

```
            ┌──────────────┐                ┌──────────────┐
   CLI  →   │ Daxter.Cli   │     MCP   →    │ DaxterTools  │
            └──────┬───────┘                └──────┬───────┘
                   │                               │
            ┌──────▼───────────────────────────────▼───────┐
            │ Daxter.Core                                   │
            │   Auth (MSAL, two client ids)                 │
            │   XMLA (ADOMD/TOM)                            │
            │   PowerBI REST  ──► groups/datasets/reports   │
            │   Fabric REST   ──► sqlEndpoints/copyJobs/    │
            │                     notebooks/items/jobs/     │
            │                     bindConnection            │
            │   FabricSqlClient (Microsoft.Data.SqlClient,  │
            │                    AAD token via SqlConnection│
            │                    .AccessToken, streaming    │
            │                    CSV export)                │
            │   Refresh scheduler (file-backed queue)       │
            │   ModelEditService (TOM staging + .bim backup)│
            │   PipelineRulesService                        │
            │   ModelMetadata / ModelDiff / Export          │
            └────────────────▲──────────────────────────────┘
                             │
            ┌────────────────┴───────────┐
            │ Daxter.Web                 │
            │   Blazor Server pages      │  → /status, /explore, /query, /refresh,
            │   DaxterUi (the bridge)    │     /jobs, /gateways, /pipelines, /audit,
            │   RefreshWorkerHostedSrv   │     /model-edit, /connections, /sql, /rls,
            │   Endpoints/SqlExport      │     /copy-jobs, /notebooks, /configure, /logs
            └────────────────────────────┘
```

See [`ARCHITECTURE.md`](ARCHITECTURE.md) for the topology details (containers, volume,
shared queue).

## 6. Write safety

Every mutation goes through three layers:

1. **`Allow writes` toggle** (global on/off, persisted on the Configure page).
2. **Workspace patterns** — `ReadOnlyWorkspaces` (deny-list) and `WriteWorkspaces` (allow-list)
   with `*` glob wildcards. Deny wins; allow-list restricts further. Refuse messages tell the
   user *which* pattern matched.
3. **Per-op dry-run** — CLI defaults to dry-run unless `--yes`; MCP defaults to dry-run unless
   `execute=true`; Web shows a centered confirm-modal with the operation preview.

Model edits add a fourth layer: a **`.bim` backup** is taken before any apply.

## 7. Distribution

- **Multi-arch Docker image** on GHCR — `ghcr.io/danlugo/daxter:<tag>` and `:latest`
  (amd64 + arm64).
- **Claude Desktop extension** (`daxter.mcpb`) — drop-in install that launches the same image
  as the MCP server.
- **Web console** — `docker run … web` → `http://localhost:8080`.
- **CLI** — `docker run … <command>`. The `bin/daxter` helper wraps it for convenience.

## 8. Skills demonstrated

.NET 8 / C# · layered architecture · DI · xUnit (270 tests) · Docker multi-stage,
multi-arch, non-root · OAuth2 / MSAL / Entra ID auth with audience-scoped client-id routing
(AADSTS65002 fix) · Power BI XMLA (ADOMD/TOM) + Power BI REST + Fabric REST integration ·
Microsoft.Data.SqlClient over AAD bearer · CLI UX (System.CommandLine) · MCP server (Model
Context Protocol) · Blazor Server (12 pages, shared components, syntax highlighting, busy
overlay, Frequent-sidebar pattern) · CI/CD via GitHub Actions (multi-arch publish) ·
cross-platform engineering · security-by-design (token cache volume, non-root container,
no secrets in image, writes-gate model).
