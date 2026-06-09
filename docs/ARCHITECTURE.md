# DAXter Architecture

## Overview

DAXter is a Docker-only Power BI + Fabric client with **three surfaces** — a CLI, an MCP
server, and a Blazor Server web console — all sharing **one engine** (`Daxter.Core`). The
engine is UI-free, so each surface is a thin shell over the same service methods. Adding a
capability once exposes it everywhere; this is the `daxter-capability` skill in practice.

```
                 ┌─────────────────────────────────────────────────┐
   user / CI →  │ Daxter.Cli            System.CommandLine          │
                │   `daxter query/refresh/model/ws/pipeline/sql/    │
                │    fabric/test-rls/login/web/mcp`                  │
                └───────────────────────┬────────────────────────────┘
                                        │ same Core methods
                 ┌─────────────────────────────────────────────────┐
   Claude /  →  │ Daxter.Cli.Mcp        MCP server (76 tools)       │
   MCP client   │   discovered via [McpServerTool] reflection       │
                │   `daxter_capabilities` is self-introspecting     │
                └───────────────────────┬────────────────────────────┘
                                        │ same Core methods
                 ┌─────────────────────────────────────────────────┐
   browser   →  │ Daxter.Web            Blazor Server (12 pages)   │
                │   `DaxterUi` bridge   wraps Core for Razor pages  │
                │   RefreshWorkerHostedService drains the queue     │
                │   Endpoints/SqlExportEndpoint (streaming CSV)     │
                └───────────────────────┬────────────────────────────┘
                                        │
                 ┌──────────────────────▼──────────────────────────┐
                 │ Daxter.Core (engine)                             │
                 │                                                  │
                 │  Auth ──► MSAL ─┬─► Power BI XMLA/REST scope     │
                 │                 └─► Fabric SQL scope             │
                 │                     (separate pre-auth client id)│
                 │  Connection ──► ADOMD / TOM                      │
                 │  Rest ──► Power BI REST + Fabric REST            │
                 │       (sqlEndpoints / copyJobs / notebooks /     │
                 │        items/jobs/instances / bindConnection)    │
                 │  Sql  ──► Microsoft.Data.SqlClient over AAD      │
                 │       bearer (FabricSqlClient + streaming CSV)   │
                 │  Editing ──► ModelEditService (.bim backup +     │
                 │       TOM staging + apply)                       │
                 │  Scheduling ──► RefreshQueueStore (file-backed)  │
                 │  Audit ──► PipelineRulesService + saved checks   │
                 │  Metadata · Query · Diff · Export · Formatting   │
                 └──────────────────────────────────────────────────┘
```

## Runtime topology — one image, many containers, one shared volume

One DAXter **instance per machine** does **not** mean one container. It means **one shared
volume** (`daxter-tokens`) + **exactly one long-running web container** (the worker + UI).
Every surface is the *same image* run in a different **mode** (`mcp` / `cli` / `web`); the
modes coordinate **only** through the file-backed queue on the shared volume — never through
shared process memory.

```
  CLIENTS (separate Claude programs / a browser)
  ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌──────────┐
  │ Claude MCP #1 │ │ Claude MCP #2 │ │  CLI command  │ │ Browser  │
  └───────┬───────┘ └───────┬───────┘ └───────┬───────┘ └────┬─────┘
          │ (1 per client)  │                 │ (1 per run)   │
          ▼                 ▼                 ▼               ▼
  CONTAINERS (each = one mode; all stamped from the Daxter Image)
  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌─────────────────┐
  │ MCP cont. A  │  │ MCP cont. B  │  │ CLI cont.    │  │ WEB cont.       │
  │ --rm,no port │  │ --rm,no port │  │ --rm, exits  │  │ persistent,8080 │
  │ ENQUEUE only │  │ ENQUEUE only │  │ ENQUEUE only │  │ ★ WORKER + UI   │
  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘  └────────┬────────┘
         └─────────────────┴─────────────────┴───────────────────┘
                                   │  all mount ▼
                 ┌─────────────────────────────────────────────┐
                 │  SHARED VOLUME  daxter-tokens (~/.daxter)    │  ◄── the "single instance"
                 │    • queue.json   the one job queue          │
                 │    • auth token   one sign-in for all        │
                 └─────────────────────────────────────────────┘
   (the Daxter Image is the template every container is stamped from —
    it sits "behind" the containers; it is not the coordination layer)
```

**How jobs stay consistent across containers** (`Daxter.Core/Scheduling/RefreshQueueStore.cs`):

- The queue is a **cross-process, file-backed** `queue.json` on the shared volume. Reads
  re-load from disk under an exclusive **lock file**; writes are temp-file + atomic replace.
- **MCP and CLI only `Enqueue`** (`JobOrigin.Mcp` / `Cli`) — they append a job and may exit.
  They hold **no** job state in memory.
- The **web container hosts the single worker** (`RefreshWorkerHostedService`) that drains the
  queue; its `JobService` is a *façade over the same `RefreshQueueStore`*, so the **Jobs UI
  shows every job** — CLI, MCP, and web alike. A `worker.heartbeat` file lets CLI/MCP warn
  when no web worker is alive to run what they enqueued.

**The two invariants that make "single instance per machine" hold:**

1. **One shared volume** — every container mounts `-v daxter-tokens:/home/daxter/.daxter`.
   (The `.mcpb` extension and the web `docker run` both do.)
2. **Exactly one web container** running the worker.

Multiple MCP/CLI containers are **harmless** — they are stateless front doors to the one
queue. The failure mode to avoid is a surface mounting a **different** volume (e.g. a stray
`*-tokens`): its `queue.json` would be invisible to the web worker. This is why **all
surfaces standardize on `daxter-tokens`**.

**Common misconceptions** (both are wrong):

| Wrong | Right |
|-------|-------|
| Several MCP clients share one MCP container | **One MCP container per client** (per-stdio process); N clients → N containers |
| The image is the shared/coordination layer | The image is just the **template**; the **shared volume** is the coordination layer |

## Why this shape

| Concern | Decision | Rationale |
|---------|----------|-----------|
| Auth on macOS/Linux | Acquire token via MSAL, inject `AccessToken` | ADOMD interactive login is Windows-only |
| Two AAD audiences | Different public client id per audience (`DefaultPublicClientId` for Power BI / XMLA / REST · `DefaultFabricSqlClientId` for `database.windows.net`) | Power BI's first-party client isn't pre-authorized for the SQL audience (AADSTS65002). Azure CLI's id is. |
| Testability | Interfaces + fakes (`ITokenProvider`, `IXmlaSession`, `IXmlaSessionFactory`); HTTP stub for REST tests; `IFabricSqlTokenProvider` for SQL | Unit tests (270+) run with no live endpoint |
| Three surfaces, one engine | Core has no UI; CLI, MCP, and Web are thin shells calling the same methods | Adding a capability once exposes it everywhere — `daxter-capability` skill |
| MCP tool discovery | Reflection over `[McpServerTool]` attributes — `daxter_capabilities` returns the live list | Self-documenting; agents discover features without out-of-band docs |
| Distribution | Multi-stage Docker, tests in build stage; multi-arch (amd64 + arm64) GHCR publish | One portable artifact; a failing test fails the image |
| Output | `IResultFormatter` (table/csv/json) + `CsvStyle` for SQL exports (RFC 4180 vs quote-all + CRLF) | stdout = data, stderr = status → clean piping; Excel-compat optional |
| Config | Env vars + `--flags` + persisted file (`~/.daxter/console-config.json`) + env profiles | One config source; Web console writes, CLI/MCP read |
| Refresh coordination | File-backed `queue.json` on the shared volume; ONE worker (Web container) drains | Cross-process consistency; the only "single instance" required is one Web container |

## Key abstractions

- **`ITokenProvider` + `IFabricSqlTokenProvider`** → `MsalTokenProvider` implements both.
  Two public client ids (`DefaultPublicClientId` for Power BI/REST/XMLA;
  `DefaultFabricSqlClientId` for the SQL endpoint — different audience, different
  pre-auth). Device-code (cached per client id) and service-principal flows. The token is
  the seam that makes cross-platform work.
- **`IXmlaSession`** → `AdomdXmlaSession`. Executes a query, materializes a `QueryResult`.
  `IXmlaSessionFactory` builds the connection string, injects `AccessToken`, opens it.
- **`ModelMetadataService`** → wraps a session and runs TMSCHEMA DMVs (measures, M code,
  partitions, RLS roles + filters + members), resolving id→name joins in code.
- **`ModelEditService`** → TOM-based; stages every edit (measure / parameter / role /
  column / source / calc-table / import-table / relationship + raw TMSL), takes a `.bim`
  backup, then `SaveChanges()`. Apply is gated by **Allow model edits** + a dry-run preview.
- **`PowerBiRestClient`** → thin client over both `api.powerbi.com/v1.0/myorg` (groups,
  datasets, reports, refreshes, gateways, pipelines) AND `api.fabric.microsoft.com/v1`
  (warehouses, lakehouses, copyJobs, notebooks, items/jobs/instances, semanticModels
  bindConnection, getDefinition). One auth token covers both.
- **`FabricSqlClient`** → `Microsoft.Data.SqlClient` over `SqlConnection.AccessToken` with a
  silently-acquired `database.windows.net` token. `ExecuteAsync` materializes;
  `StreamCsvAsync` writes RFC-4180 / quote-all + CRLF CSV row-by-row straight to a
  `TextWriter` (zero in-memory materialization — verified streaming export of 802k-row
  table in 9.4s on the Fabric backend).
- **`RefreshQueueStore`** → file-backed queue (`queue.json` + `queue.lock` +
  `worker.heartbeat`) on the shared volume. CLI/MCP/Web all enqueue; one worker drains.
- **`WorkspaceMatcher`** → pure glob matcher (`*` wildcard, case-insensitive, anchored)
  feeding `DaxterConfig.IsReadOnlyTarget()`. Two-list precedence: deny-list wins;
  allow-list (when non-empty) restricts further; legacy `*prod*` heuristic only kicks in
  when neither list is explicit.
- **`ResultFormatterFactory`** → `Table` (Spectre.Console), `Csv` (RFC 4180), `Json`,
  plus `CsvStyle` for streaming SQL exports (`QuoteAll` + `Crlf` flags).
- **`DaxterConfig`** → resolves config from `--flags` → `DAXTER_*_<ENV>` →
  `PersistedSettings` → `DAXTER_*` defaults.

## Request flows

**A DAX query** — `daxter query "EVALUATE …"` or `daxter_query` MCP tool or `/query`:
1. CLI / MCP / Web bridge calls `Core.IXmlaSession.Execute(dax)`.
2. `DaxterConfig.FromEnvironment` resolves workspace/dataset/auth (env profile aware).
3. `MsalTokenProvider.GetTokenAsync()` returns a cached Power BI token (silent if a
   previous sign-in succeeded; device-code if not, but only on the CLI — MCP/Web refuse
   interactive prompts and tell the user to `daxter_login`).
4. `AdomdXmlaSessionFactory` builds `Data Source=powerbi://api.powerbi.com/v1.0/myorg/<ws>`,
   injects `AccessToken`, opens.
5. `AdomdXmlaSession.Execute` runs the DAX/MDX/DMV, returns a `QueryResult`.
6. CLI: `IResultFormatter` renders to stdout · MCP: cap + JSON-serialize · Web: pass to
   `ResultGrid`.

**A SQL query** — `daxter sql query …` or `daxter_sql_query` MCP tool or `/sql`:
1. Bridge resolves workspace → groupId via REST, then looks up the endpoint (Warehouse or
   Lakehouse) → `(server, database)` from `SqlEndpointsAsync`.
2. `MsalTokenProvider.GetFabricSqlTokenAsync()` silently acquires a
   `https://database.windows.net/.default` token (using `DefaultFabricSqlClientId` — the
   separate pre-authorized client id; needs a one-time second sign-in).
3. `FabricSqlClient.ExecuteAsync(server, database, sql, allowWrite)` opens a `SqlConnection`
   with `AccessToken=<bearer>`, runs the query, materializes the first result set.
4. The `SqlWriteGate` (read-only by default) blocks non-SELECT unless `allowWrite` is on.
5. **Export All CSV** path on the Web takes a different route: a `POST /api/sql/export`
   minimal-API endpoint streams `FabricSqlClient.StreamCsvAsync` straight into
   `Response.Body` — bypasses the SignalR circuit so a `SELECT *` on a multi-million-row
   table won't OOM the browser.

**A refresh** — `daxter refresh …` or `daxter_refresh` MCP tool or `/refresh`:
1. The surface enqueues a `RefreshJob` into `~/.daxter/queue.json` (cross-process lock).
2. The Web container's `RefreshWorkerHostedService` notices via `worker.heartbeat`, picks
   the next job, opens an XMLA session OR posts to the Enhanced Refresh REST API
   (default: REST — server-side, survives client disconnect).
3. Progress + per-partition status are written back to the queue so any UI / `refresh
   status` poll sees them. Resume picks up exactly the not-completed partitions.

## Security

- OAuth 2.0 via MSAL; **no passwords in connection strings** — token injection only.
- Secrets come from env / `.env` (gitignored) / `~/.daxter/console-config.json`; **never**
  baked into the image.
- Container runs as a **non-root** user (`daxter`); token cache lives in a mounted volume
  (`daxter-tokens`).
- Errors are sanitized to `daxter: <message>` (no stack traces) for user-facing failures.
- **Writes gate** (three layers): Allow-writes toggle · workspace patterns (deny-list +
  allow-list with `*` glob) · per-op dry-run (`--yes` / `execute=true` / confirm modal).
- **Model edits**: `.bim` backup taken before every apply.

## Testing

- `Daxter.Core.Tests` (xUnit, 270+): connection-string building, formatters (incl. RFC-4180
  quoting + CRLF + culture-invariant render), config/env-profile resolution, metadata DMV
  construction + RLS join, error paths, **REST JSON parsing** (workspaces, datasets,
  reports, gateways, connections, copyJobs, notebooks, sqlEndpoints, item-job instances —
  HTTP stubbed via a fake handler), **WorkspaceMatcher** glob semantics, **read-only
  precedence** ladder, **capabilities classification** (so a tool annotated
  `ReadOnly = true` can't drift into the destructive bucket), **SQL write-gate**
  classifier, **CSV style** options, **scheduler resume**, **MCP login prompt
  formatting**. Env-mutating tests are serialized via a collection.
- The Docker build runs the full suite; the image is only produced if tests pass.
