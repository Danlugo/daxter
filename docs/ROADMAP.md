# DAXter Roadmap

> DAXter started as an XMLA query client and has grown into a **Power BI + Fabric operator
> tool** with three surfaces (CLI · MCP server · Blazor web console) over one shared engine.
> One token covers Power BI XMLA + REST and Fabric REST; a second silent token covers the
> Fabric SQL endpoint audience. No Windows-only tooling, no .NET install.

## API legend

| Tag | Source | Notes |
|-----|--------|-------|
| **X** | XMLA (ADOMD) | Inside a model: query, DMV, TMSL refresh |
| **T** | TOM (AMO) | Model object model: export/diff/edit |
| **R** | Power BI REST | Workspace/dataset/report inventory, refresh, gateways |
| **F** | Fabric REST | sqlEndpoints, copyJobs, notebooks, items/jobs, bindConnection |
| **S** | TDS (SQL) | Fabric Warehouse / Lakehouse SQL endpoint (Microsoft.Data.SqlClient + AAD bearer) |
| **A** | Admin/Scanner | Tenant-wide; needs admin + security-group membership |
| **L** | Local | Config/profiles, no API call |

## Status — what's shipped

**All phases below are complete and in `ghcr.io/danlugo/daxter:latest`.** Numbers in parens
are the releases that landed them.

### Foundations (v0.1 → v1.0)

| # | Capability | API | Status |
|---|---|---|---|
| 0 | Three surfaces (CLI · MCP · Web) sharing one Core engine | L | ✅ |
| 1 | Environment profiles (`--env`, `DAXTER_*_<ENV>`) | L | ✅ |
| 2 | Device-code + service-principal auth · cached MSAL accounts | L | ✅ |
| 3 | Output formatters (table / CSV / JSON) | L | ✅ |
| 4 | Multi-arch Docker image (amd64 + arm64) published on tag | L | ✅ |

### Query + Metadata (v0.2 → v1.4)

| # | Capability | API | Status |
|---|---|---|---|
| 5 | DAX / MDX / DMV (`query`, `dmv`, `ls`) | X | ✅ |
| 6 | Measure definitions + expressions | X | ✅ |
| 7 | M code per table | X | ✅ |
| 8 | Parameters (shared M expressions) | X | ✅ |
| 9 | RLS settings (roles, filters, members) | X | ✅ |
| 10 | Partitions + last-refresh time (XMLA half) | X | ✅ |
| 11 | Model export (`.bim`) | T | ✅ |
| 12 | Model diff (measure-level) | T | ✅ |
| 13 | Test-RLS (impersonation) | X | ✅ |

### Operations (v0.3 → v1.8)

| # | Capability | API | Status |
|---|---|---|---|
| 14 | Refresh model / table / partitions (newest-first) | X+R | ✅ |
| 15 | Enhanced Refresh (default; server-side, survives client drop) | R | ✅ |
| 16 | Refresh history + status | R | ✅ |
| 17 | Resume-from-failed-partition | L+X | ✅ |
| 18 | Cache clear (TMSL `ClearCache`) | X | ✅ |
| 19 | Refresh scheduler — file-backed queue + one shared worker · concurrency tunable via `DAXTER_REFRESH_MAX_CONCURRENT_MODELS` | L | ✅ |

### Workspace / Inventory (v0.4 → v1.5)

| # | Capability | API | Status |
|---|---|---|---|
| 20 | Workspace ls / datasets / reports / lineage / permissions | R | ✅ |
| 21 | Gateways + datasources + connections | R | ✅ |
| 22 | Report inventory (thin / thick / paginated · downloadable .pbix) | R | ✅ |
| 23 | Export report (PBIR definition + .pbix) | R | ✅ |
| 24 | Take over + bind-to-gateway · per-source `bindConnection` (Fabric GA) | R+F | ✅ |

### Pipelines (v0.6 → v1.4)

| # | Capability | API | Status |
|---|---|---|---|
| 25 | Pipeline ls / stages / operations | R | ✅ |
| 26 | Deployment rules — inferred from per-stage parameter differences | R+L | ✅ |
| 27 | Pipeline audit — models without rules, parameter sanity checks · saved checks | R+L | ✅ |

### Model editing (v1.10 → v1.15)

| # | Capability | API | Status |
|---|---|---|---|
| 28 | Typed edits — measure / parameter / role / column / source / calc-table / import-table / relationship | T | ✅ |
| 29 | `edit-column` — format / data type / sort-by / summarize-by / folder / hidden | T | ✅ |
| 30 | Raw TMSL escape hatch | X | ✅ |
| 31 | Dry-run by default · `.bim` backup before apply · Allow-model-edit gate | T+L | ✅ |

### Connections — Cloud "Maps to" (v1.20 → v1.24)

| # | Capability | API | Status |
|---|---|---|---|
| 32 | Per-source "Maps to" via Fabric `bindConnection` (GA, all connectivity types incl. ShareableCloud) | F | ✅ |
| 33 | Web Connections page — two sections (Gateway / Cloud), both writable | F | ✅ |

### RLS viewer (v1.29)

| # | Capability | API | Status |
|---|---|---|---|
| 34 | Per-(role, table) DAX filter expression viewer · syntax-highlighted · copy-DAX | X | ✅ |
| 35 | MCP `daxter_role_filters` + `daxter_role_members` | X | ✅ |

### Fabric SQL endpoint (v1.25 → v1.28)

| # | Capability | API | Status |
|---|---|---|---|
| 36 | Endpoint discovery (Warehouses + Lakehouse SQL endpoints) | F | ✅ |
| 37 | T-SQL execution over TDS · AAD bearer · separate pre-authorized client id (AADSTS65002 workaround) | S | ✅ |
| 38 | Object explorer (schemas → tables / views / functions / stored procedures) | S | ✅ |
| 39 | Streaming Export All CSV — `SqlDataReader → TextWriter`, no in-memory materialization (verified 802k rows / 9.4s) | S | ✅ |
| 40 | CSV style options — quote-all + CRLF (matches Power BI "Export data" byte-for-byte; verified MD5-identical sorted body for a 14k-row export) | S | ✅ |

### Fabric Copy Jobs + Notebooks (v1.30)

| # | Capability | API | Status |
|---|---|---|---|
| 41 | List Copy Jobs + Notebooks per workspace | F | ✅ |
| 42 | View definition (`copyjob-content.json` / `artifact.content.ipynb`) | F | ✅ |
| 43 | Run on demand (writes-gated) — `jobType=Execute` / `RunNotebook` | F | ✅ |
| 44 | Recent runs + status + duration + failure reason | F | ✅ |
| 45 | Cancel a running instance | F | ✅ |

### Writes-gate model (v1.30.1)

| # | Capability | API | Status |
|---|---|---|---|
| 46 | Two-list workspace patterns (`ReadOnlyWorkspaces` deny + `WriteWorkspaces` allow) with `*` globs | L | ✅ |
| 47 | Refuse messages name the matched pattern | L | ✅ |
| 48 | Configure-page live preview (writable / READ-ONLY + reason) | L | ✅ |
| 49 | MCP auto-enforces when user has explicit lists; legacy env-var opt-in retained | L | ✅ |

## Future / deferred

| # | Capability | API | Notes |
|---|---|---|---|
| 50 | **Edit Copy Job / Notebook definitions** | F·Write | Fabric `PATCH` / `updateDefinition` is available; needs a JSON / `.ipynb` editor UI (bigger build) |
| 51 | **Create + Delete** Copy Job / Notebook items | F·Write | After the edit UI lands |
| 52 | **Parameterized notebook runs from the Web button** | F·Write | CLI/MCP already accept `--execution-data` / `executionData`; UI for cell-parameter overrides is a follow-up |
| 53 | **Tenant inventory / audit / orphans** | A | Scanner + Activity API; admin only. Deferred — requires a Fabric admin identity + tenant-setting toggle |
| 54 | **Edit RLS DAX from the viewer** | T·Write | Today the viewer is read-only; edits go through Model Edit page |
| 55 | **Pipelines + Spark Job Definitions as Fabric items** | F | Same `<FabricItemViewer>` shape as Copy Jobs / Notebooks — additive |
| 56 | **Schedule management** (recurring runs) | F·Write | Pipeline Scheduler API exists; would slot into `/refresh` + `/copy-jobs` + `/notebooks` |

## Cross-cutting

- **Auth.** The XMLA + Power BI REST + Fabric REST surfaces share one MSAL token (Power BI
  client id). The Fabric SQL endpoint surface uses a SECOND silent token from the same
  account but a different client id (`DefaultFabricSqlClientId` — Azure CLI's, which IS
  pre-authorized for `database.windows.net`). One-time second device-code, then silent.
  Override either with `DAXTER_PUBLIC_CLIENT_ID` / `DAXTER_SQL_CLIENT_ID` if you have a
  tenant app pre-authorized for both audiences (one sign-in for everything).
- **Write safety.** Three layers: **Allow writes** toggle · **workspace patterns** with `*`
  globs (deny-list + allow-list; deny wins) · per-op dry-run. Model edits add a `.bim`
  backup. Refuse messages name the matched pattern.
- **XMLA read vs read-write.** TMSL refresh / edit need the capacity's XMLA endpoint set to
  **Read/Write**. REST refresh works with read-only XMLA.
- **MCP tool discovery.** `daxter_capabilities` reflects over `[McpServerTool]` attributes
  and returns the live list with read/write classification — agents discover features
  without out-of-band docs. A regression test pins the classification so a `ReadOnly = true`
  tool can't drift into the destructive bucket (the bug fixed in v1.29).
