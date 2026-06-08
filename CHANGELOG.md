# Changelog

All notable changes to DAXter are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

## [1.28.0] - 2026-06-08

### Added
- **CSV style options for streaming SQL export** — `quoteAll` and `crlf`, exposed across all surfaces
  so a DAXter export can match the exact byte format of Power BI / Excel "Export data" output.
  Notably distinguishes NULL (bare empty, `,,`) from empty-string (`""`) under `quoteAll`, matching
  Power BI's convention — verified live: a 14,272-row export hand-pulled from Power BI and a DAXter
  `--quote-all --crlf` export of the same table have identical sorted MD5 hashes.
  - **Web `/sql`** — two checkboxes next to **⬇ Export All CSV**: *Quote-all* (wraps every field) and
    *CRLF* (Windows line endings).
  - **`POST /api/sql/export`** — reads `quoteAll` and `crlf` form fields (`on`/`true`/`1` = yes).
  - **CLI** — `daxter sql query --out file.csv --quote-all --crlf`.
  - **MCP** — `daxter_sql_export(workspace, endpoint, sql, quoteAll=false, crlf=false)`.
  Defaults stay RFC 4180 + LF (clean, what Pandas / DuckDB / sqlmgr emit). New `CsvStyle` record in
  `Daxter.Core.Formatting` is the single source of truth — surfaces just plumb the flags through.

## [1.27.1] - 2026-06-08

### Fixed
- **`Globalization Invariant Mode is not supported` on every SQL query.** Caught by live-testing
  v1.27.0's Export All against an 802k-row Fabric lakehouse table. Two compounding causes:
  1. `Daxter.Cli.csproj` had `<InvariantGlobalization>true</InvariantGlobalization>` (image-size
     tweak from the XMLA/REST era). Microsoft.Data.SqlClient calls into ICU during
     `SqlConnection.OpenAsync` and throws under invariant mode. Flipped to `false`.
  2. Even with the csproj fix, the `dotnet/aspnet:8.0` runtime base image ships without ICU data,
     and the runtime still forces invariant mode when ICU isn't present. Added `libicu72` +
     `tzdata` via `apt-get` and set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false` in the runtime
     stage. ~30 MB image growth to ship a SQL endpoint that actually opens connections.
  Verified live: `SELECT * FROM 802k-row table` streams to a 62 MB CSV in ~9.4s with bounded memory.

## [1.27.0] - 2026-06-08

### Added
- **SQL "Export All CSV" — streaming, no memory cap.** A new button on `/sql` streams the FULL
  result set straight to a browser download. The Run-SQL path materializes everything into memory
  for the in-page grid (good for sampling); Export All goes a different route — `POST /api/sql/export`
  opens a `SqlDataReader`, writes RFC-4180 CSV row-by-row directly into the HTTP response, and
  flushes every 1000 rows. Bypasses the Blazor SignalR circuit so a `SELECT *` on a multi-million-row
  warehouse table won't OOM the container OR the browser. Same writes-gate as the live-query path
  (and a confirm modal if the SQL isn't read-only).
- **`FabricSqlClient.StreamCsvAsync(server, database, sql, allowWrite, TextWriter, ct)`** — the
  shared streaming primitive every surface uses (Web endpoint, CLI `--out`, MCP `daxter_sql_export`).
  `CommandBehavior.SequentialAccess` so wide rows don't buffer; returns the row count written.
- **CLI: `daxter sql query --out file.csv`** — stream the full result set to a file path. No
  in-memory materialization regardless of row count.
- **MCP: `daxter_sql_export(workspace, endpoint, sql)`** — streams to
  `~/.daxter/exports/sql/<timestamp>-<endpoint>.csv` on the persistent volume; returns the path
  with a `docker cp …` hint so the user can pull the file off the container.

### Changed
- `CsvResultFormatter.Escape` / `.Render` are now `public static` so the streaming exporter shares
  the exact same RFC-4180 escape + culture-invariant value rendering the in-memory CSV uses.

## [1.26.2] - 2026-06-07

### Fixed
- **`/sql` editor: typed SQL was invisible.** The textarea reused `.dax-edit .dax-editor`, which is
  intentionally `color:transparent` on the DAX page (a colored `<pre>` paints highlighted text
  underneath). The SQL page has no highlight overlay, so the transparent text rule was rendering
  white-on-white. New `.sql-edit` wrapper class keeps the size/font of the DAX editor but flips
  `color` / `-webkit-text-fill-color` back to `var(--text)` so the SQL you type is visible.

## [1.26.1] - 2026-06-07

### Fixed
- **`AADSTS65002` on Fabric SQL token acquisition.** Power BI's first-party public client id is NOT
  pre-authorized by Microsoft for the `database.windows.net` audience, so reusing the existing MSAL
  app for the SQL scope failed at AAD ("Consent between first party application X and first party
  resource Y must be configured via preauthorization"). The SQL surface now uses a separate
  pre-authorized client id — Azure CLI's public client id (`04b07795-…`) by default — which the
  Microsoft identity service accepts for the SQL audience the same way `az login` does. Override via
  the new env var `DAXTER_SQL_CLIENT_ID` (point it at a tenant app pre-authorized for BOTH Power BI
  and Azure SQL to skip the second sign-in).

### Added
- **Separate one-time Fabric SQL sign-in.** Because MSAL refresh tokens are bound to client id, the
  SQL-scope sign-in is a distinct device-code flow from Power BI's. Surfaced everywhere:
  - **Web** — when a SQL call returns the "Not signed in" / `AADSTS65002` message, the `/sql` page
    shows a yellow card with a **"🔐 Sign in for Fabric SQL"** button that pops a device-code modal
    with URL + code. When the background sign-in completes the modal auto-closes and the page retries.
  - **CLI** — `daxter login --target sql` (default target = `powerbi`, unchanged).
  - **MCP** — `daxter_login` now accepts `target="sql"`.
- **`DAXTER_SQL_CLIENT_ID`** environment variable on `DaxterConfig` for the override path.

## [1.26.0] - 2026-06-07

### Added
- **SQL Objects explorer.** The `/sql` page now has a left-side **Objects** panel — schemas as
  expandable folders containing **Tables / Views / Functions / Stored Procedures** groups, each
  showing every object on the SQL endpoint. Same shape as Fabric's own SQL editor. Type-to-filter
  search box on top; click an object to insert `[schema].[name]` into the SQL editor at the
  caret. Auto-loads when the endpoint is picked (or arrived via a Frequent / deep-link click) and
  auto-expands the first schema (usually `dbo`). One INFORMATION_SCHEMA UNION round-trip — Tables +
  Views + Functions + Procedures all in one query — so the tree is alphabetical and fully populated
  the moment it appears.
- **`daxter sql objects --workspace W --endpoint NAME`** — same listing from the CLI; output as
  table / CSV / JSON. `--server` / `--database` override the endpoint lookup.
- **`daxter_sql_objects(workspace, endpoint)` MCP tool** — read-only. Returns `(schema, kind, name)`
  rows. Pair it with `daxter_sql_endpoints` (workspace discovery) → `daxter_sql_objects` (what's in
  here) → `daxter_sql_query` (run T-SQL) and an agent can navigate a warehouse end-to-end without
  ever knowing the GUID hostname.

## [1.25.0] - 2026-06-07

### Added
- **T-SQL against Fabric SQL endpoints (Warehouses + Lakehouse SQL analytics endpoints).** New
  capability across all three surfaces — same MSAL account as the rest of DAXter, so signing in once
  via `daxter_login` silently entitles SQL too (no second device-code prompt, no service principal).
  Authenticates via `SqlConnection.AccessToken` with a token acquired for the
  `https://database.windows.net/.default` scope.
  - **Web** — new **/sql** page: workspace picker → SQL-endpoint picker (`SearchableSelect` Options
    mode showing `name (Warehouse|Lakehouse)`, binding the endpoint) → SQL editor (Ctrl/Cmd-Enter
    runs) → `ResultGrid` with Export CSV. Own `SqlContext` Frequent sidebar group (page-scoped
    history per UI contract), deep-link `?ws=&ep=`, busy overlay, confirm-modal on writes.
  - **CLI** — `daxter sql endpoints --workspace W` lists Warehouses + Lakehouse SQL endpoints in a
    workspace; `daxter sql query --workspace W --endpoint NAME --query "…"` (or `--file`) runs the
    statement. `--allow-writes` lets non-SELECT through; otherwise read-only. `--server` /
    `--database` override the discovery lookup.
  - **MCP** — `daxter_sql_endpoints` (read-only, list endpoints) and `daxter_sql_query` (run T-SQL).
    Read-only T-SQL runs unconditionally; non-read T-SQL is REFUSED unless the writes gate is on AND
    the workspace doesn't look like PROD — same rule as every other writing tool. Surfaced in
    `daxter_capabilities` automatically.
- **`Daxter.Core.Sql.FabricSqlClient`** — thin T-SQL client (Microsoft.Data.SqlClient) that takes a
  `(server, database, sql)` and materializes the first result set as the same `QueryResult` every
  DAXter surface already renders. `MsalTokenProvider.GetFabricSqlTokenAsync` shares the cached MSAL
  account so the token comes silently.
- **`Daxter.Core.Sql.SqlWriteGate`** — light classifier deciding read-only vs write so the same gate
  applies on the Web, the CLI, and the MCP tool (catches CTE-feeding-INSERT and mixed batches).

### Changed
- `MsalTokenProvider` now implements `IFabricSqlTokenProvider` and exposes `FabricSqlScopes`. The
  existing XMLA/REST token flow is unchanged — the SQL scope is a separate cache entry on the same
  MSAL account.

## [1.24.1] - 2026-06-06

### Added
- **`SearchableSelect` — label/value `Options` mode for id-valued pickers.** The component now accepts
  `Options` as `IReadOnlyList<(string Value, string Label)>`: the box shows the **label**, binds the
  **value** (e.g. an id). String-list mode (`Items`) is unchanged. Applied to **every remaining
  id-valued picker** that wasn't already searchable:
  - **Audit** — Pipeline (was a plain `<select>` of pipeline id/name).
  - **Connections** — Gateway (admin gateway picker) **and** the per-row "Maps to" dropdowns in the
    Gateway-connections and Cloud-connections sections.
  Type to filter instead of scrolling.

## [1.24.0] - 2026-06-06

### Added
- **Searchable pickers everywhere.** A new shared **`SearchableSelect`** component (native
  `<datalist>`-backed type-to-filter combo) replaces the plain `<select>` on every **long / dynamic-list**
  picker — workspace, dataset/model, table, and column dropdowns on **Model Edit, Connections, and
  Refresh** (Explore already had it). Type to filter instead of scrolling a long list (the model dropdown
  was hundreds of entries). Short **fixed-enum** selects (refresh type, data type, granularity,
  summarize-by, cross-filter, log level, status filters…) stay plain `<select>`. The UI contract now
  mandates `SearchableSelect` for long/dynamic pickers so new pages inherit it.

### Changed
- **Refresh now runs via the server-managed Enhanced Refresh REST API by default** — the worker submits
  the refresh (with `objects` for table/partition targeting, `maxParallelism`, `retryCount`, and
  `commitMode: partialBatch` for partition jobs) and **polls** per-partition status, instead of driving
  XMLA partition-by-partition over a long-lived client connection. Because **no client connection is
  held**, refreshes can't hang or drop the way the XMLA path did (the *"connection is not open"* / stuck
  failures). Per-partition progress is unchanged; cancellation maps to the API's `DELETE`.
  - `commitMode: partialBatch` commits each partition as it finishes, so a mid-run failure keeps the
    completed work — and **resume re-runs exactly the not-completed partitions** (tracked precisely, so
    it's correct even when partitions finish out of order under parallelism).
  - Tunables: `DAXTER_REFRESH_MAX_PARALLELISM` (default 4, clamped 1–10). Set `DAXTER_REFRESH_ENGINE=xmla`
    to force the legacy client-driven serial path (e.g. for non-Premium models, which the enhanced API
    requires Premium/PPU/Fabric + `Dataset.ReadWrite.All` to use).
  - The XMLA serial path (with its token-keepalive + re-open-on-retry) remains as the fallback engine.

### Added
- **Per-source connection binding — including cloud "Maps to" — via the Fabric `bindConnection` API.**
  Binds a single data source of a semantic model to a connection of **any** connectivity type:
  `ShareableCloud` (the cloud "Maps to"), `OnPremisesGateway`, `VirtualNetworkGateway`, `PersonalCloud`,
  `Automatic` (default SSO), or `None` (unbind). This **supersedes** the model-level `BindToGateway` —
  it's per-source and does the cloud mappings the gateway API can't.
- Across surfaces (parity): **CLI** `ws bind-connection`, **MCP** `daxter_bind_connection`, **web**
  (the Connections page **Cloud connections** section is now writable — pick a shareable cloud connection
  per source and *Set*). Gated like other writes; requires model ownership (take over first).

### Fixed / corrected
- **Cloud "Maps to" is NOT Service-only after all.** An earlier release described it as having no public
  write API (based on older UI docs). Microsoft's GA `bindConnection` API does it — now adopted.
  (`daxter_bind_to_gateway` remains for whole-model gateway binds but points to `daxter_bind_connection`.)

### Fixed
- **Long partition refreshes now survive a dropped connection** (`"The connection is not open"`). This is
  distinct from token expiry — the connection drops mid-command, typically under concurrent-refresh
  capacity/XMLA pressure. The per-partition retry previously re-ran on the **same dead session**, so it
  couldn't recover; it now **re-opens a fresh session** (fresh connection + token) on each attempt. And
  partition refreshes **retry by default** (`SerialPartitionRefresh.MinRetries` = 2, each a cheap
  re-open) so a single transient drop no longer fails the whole job — even when the caller passed
  `retries=0`. The token-keepalive (fresh session per partition) was already working for expiry; this
  closes the connection-drop gap.

### Added
- **`daxter_capabilities` — agents discover every feature in one call.** Lists every registered MCP tool
  (name, title, read/write kind, description) plus the running version. It's generated by **reflecting the
  registered tools at runtime**, so it is always complete and never drifts — every release automatically
  surfaces its new features. An agent unsure what DAXter can do calls this first.
- **Release gate for feature discovery.** `verify-release.sh` now calls `daxter_capabilities` over the MCP
  on the *published* image and fails the release if it doesn't return the tool catalogue — so "MCP agents
  can always learn all available features" is enforced every release, not just hoped for.

### Added
- **Resume a refresh from where it stopped.** A partition job now records its ordered partition list at
  start, so resuming an interrupted/failed job re-runs **only the not-yet-done partitions** (the
  in-flight/failed one included) instead of the whole table — a 68-partition job stopped at 24 resumes
  just the remaining 44. Pass full-rerun to re-do everything.
- Across surfaces (parity): **MCP** `daxter_resume_refresh` (job id; `remaining_only=true` by default),
  **CLI** `refresh resume <job-id>` (`--full` for a full re-run), **web** (Jobs page Resume button shows
  *"↻ Resume N left"* for partial jobs, with a *full* option). Same write gate as a normal enqueue.
- **Self-recovery for agents:** `daxter_refresh_jobs` now returns a `resume_hint` on every failed/
  interrupted job — the exact `daxter_resume_refresh` call to pick up where it left off — and the
  `daxter_refresh`/`daxter_refresh_jobs` descriptions tell the agent to use it instead of starting over.

### Note
- Resume-remaining requires the job to have recorded its partition order — i.e. jobs run on **this
  version or later**. Jobs from before always fall back to a full re-run.

### Added
- **Refresh concurrency is now configurable** via `DAXTER_REFRESH_MAX_CONCURRENT_MODELS` (default **4**,
  clamped to **1–16**). The shared worker drains the queue up to this many *models* at once (still
  serialized one refresh per model; queue depth stays unbounded). Raise it when the capacity can take the
  extra concurrent XMLA refreshes; the ceiling guards against runaway values that would exhaust capacity.
  The worker logs the effective cap at startup.

### Added
- **Export a report's definition** (PBIR / PBIR-Legacy) via the Fabric *Get Report Definition* API —
  the per-visual JSON that carries every field reference (`Table[Column]`, measures), the substrate for
  column-usage analysis. Handles the long-running-operation path; base64 parts are decoded to text.
  Optional **`.pbix`** download too (Power BI *Export Report In Group*), which surfaces a clear error for
  service-authored / XMLA-edited reports that can't be exported.
- Across surfaces (parity): **CLI** `ws export-report --report <name> [--out <dir>] [--pbix]` (writes the
  definition files / `.pbix`, or prints a manifest), **MCP** `daxter_export_report` (**downloads both the
  definition and the `.pbix` by default** to `~/.daxter/exports/` in the token volume — `pbix=false` /
  `definition=false` to pick one, `output_dir` to write elsewhere, `part` to also return a file inline),
  **web** (Explore → workspace → **Report types** → *Download JSON*).

### Added
- **Report classifier — thin vs thick + downloadable.** Classifies every report in a workspace as
  **thin** (decoupled from a shared model), **thick** (embeds its own model — XMLA-editing that model
  permanently blocks future `.pbix` download), or **paginated**, and whether it's downloadable as a
  `.pbix` (`isFromPbix` — service-authored reports can't be exported). Signals: `datasetWorkspaceId`,
  `isFromPbix`, `reportType`, and the reports-per-dataset fan-out. Use it to pick which reports are safe
  to pull for analysis (the thin, `.pbix`-backed ones) and which to leave alone before a model edit.
- Across all surfaces (parity): **CLI** `ws report-inventory`, **MCP** `daxter_report_inventory`,
  **web** (Explore → workspace → **Report types**).

### Added
- **Edit an existing column's properties** — format string, data type, sort-by column, summarize-by,
  display folder, description, hidden, data category — on **any** column (data or calculated), without
  needing a DAX expression. This closes a real gap: raw TMSL `createOrReplace.column` is rejected by the
  engine (*"Unrecognized JSON property: column"*), so a column's format/sort-by previously had to be
  changed in Tabular Editor. DAXter now uses **TOM + `SaveChanges()`** — the same path Tabular Editor /
  Power BI Desktop use — sending only the fields you change (so a sourced column's data type is never
  re-stamped unless you pick a new one). Gated behind the model-edit write gate; `.bim` backup before apply.
- Across all three surfaces (parity): **CLI** `model edit edit-column`, **MCP** `daxter_edit_column`,
  **web** (a new **Columns** tab on the Model Edit page — pick a table, click a column, edit its
  properties, Preview/Apply).

## [1.14.0] - 2026-06-05

### Added
- **Connections page rebuilt to mirror the Power BI *Gateway and cloud connections* screen** — two
  sections, each listing every data source in the model with a per-source **"Maps to:"** dropdown:
  - **Gateway connections** — pick a gateway you administer; each source shows a ✓/✗ match indicator and
    a dropdown of that gateway's matching connections (pre-selected to the current binding). **Apply is a
    real write** via the public `BindToGateway` API.
  - **Cloud connections** — each source shows its matching shareable cloud connections (pre-selected to
    the current mapping). **Read-only** with a *Manage in Power BI Service ↗* deep link, because setting
    a cloud "Maps to" has **no public write API** (confirmed against Microsoft Learn — it's Service-only).
- New readable capability across all three surfaces (parity): **CLI** `ws connections`, **MCP**
  `daxter_connections`, **web**. Lists every connection you can access (cloud + gateway) via the Fabric
  *List Connections* API (paginated); the web Cloud section filters to shareable cloud per source.

## [1.13.1] - 2026-06-04

### Fixed
- **Connections re-bind dropdown was always empty.** It matched the gateway's connections to a model
  source by server+database, but the `/gateways/{id}/datasources` endpoint frequently returns
  **null server/database** (e.g. on VNet/Resource gateways) — so nothing ever matched, even when the
  gateway clearly hosts the connection. The matcher now keys primarily on the connection **name**
  (gateway `datasourceName` == the source's display name from the Fabric read), falling back to
  type + server + database only when both sides carry them. The re-bind list is now built from the
  named Fabric connections (falling back to raw data sources when Fabric is unavailable), so a source
  whose connection exists on the selected gateway is now selectable.

## [1.13.0] - 2026-06-04

### Added
- **Connections page now reads the real "maps to" mapping** — connection **name** + **connectivity
  type** (Cloud / On-prem gateway / VNet gateway) per source — from the Fabric *List Item Connections*
  API. This needs only model read/write, so it names bindings even to gateways the signed-in account
  can't administer (e.g. a VNet gateway connection for a Snowflake source), matching the Service's
  *Gateway and cloud connections* view. Falls back to the raw data-sources list if the Fabric API is
  unreachable.
- New readable capability across all three surfaces (parity): **CLI** `ws item-connections`, **MCP**
  `daxter_item_connections`, **web** (the Connections current-mapping table).

### Changed
- **Connections re-bind is now an explicit single-gateway action.** The current mapping is read-only
  (the public API can't write per-source-different gateways or cloud "Maps to"); to *change* a binding
  you pick **one** gateway you administer and re-bind matching sources to it (`BindToGateway` is one
  gateway per model). Per-source connection dropdowns now use **exact, kind-aware matching** (type +
  server + database) with **no fallback** — a source with no connection on the chosen gateway says so
  plainly instead of listing every connection.

### Fixed
- **`daxter_gateway_datasources` (MCP) no longer errors "No workspace configured."** It used the
  workspace-scoped path; it now runs tenant-scoped like the CLI's `ws gateway-datasources` (which was
  already correct), since a gateway id needs no workspace.

## [1.12.3] - 2026-06-04

### Fixed
- **Connections gateway picker now lists every gateway visible to the account.** It used
  `DiscoverGateways`, which only returns on-premises gateways with *pre-matching* data sources (and
  effectively needs ownership) — so it showed "no bindable gateways" even when valid ones existed. The
  picker now uses `GatewaysAsync` (all gateways the account can administer); the per-row connection
  dropdown still filters to connections matching that source. (`daxter_discover_gateways` remains
  available as a tool for the narrower "already-bindable" view.)

## [1.12.2] - 2026-06-04

### Fixed
- **Connections layout no longer shoves the pickers off the card.** The 2-column table pushed the
  Gateway/connection dropdowns far right (and overflowed) when a data source's server string was long.
  Each connection is now a **stacked block** — source on top, `maps to: Gateway [▾] connection [▾]`
  left-aligned beneath it — inside a bordered list.
- **Connection dropdown shows only applicable connections.** Instead of every connection on the
  gateway, each source's dropdown now lists only the gateway connections whose **server matches that
  source** (with a safe fallback to all if none match, and a "showing N of M" hint when filtered).

## [1.12.1] - 2026-06-04

### Changed
- **Connections: per-source gateway + connection pickers.** Each data-source row now has its own
  **Gateway** dropdown *and* a **connection** dropdown driven by that gateway (so when more than one
  gateway is available you choose gateway → then connection, per source). Apply still enforces the
  API's one-gateway-per-model rule — if rows pick different gateways it asks you to use the same one.

## [1.12.0] - 2026-06-04

### Changed
- **Connections page redesigned to mirror Power BI's "Gateway and cloud connections" screen.** Now:
  **Take over** + the **current owner** (`configuredBy`) at the top; a **"Gateway and cloud connections"**
  header; a **gateway picker**; and a **per-data-source row** — `Connection: <source> — maps to: [▾]` —
  where each source maps to one of the chosen gateway's connections, then **Apply** binds. Current
  binding (gateway id, or "cloud / SSO") is shown per source. The shareable cloud-connection "Maps to"
  remains read-only (no public API), called out inline. Same write gate + confirm modal + busy overlay.

## [1.11.2] - 2026-06-04

### Fixed
- **Connections page picker now sorts on its own** (defensive) — every per-page list-builder
  (`Names`/`ColumnValues`) sorts A→Z, so a dropdown is alphabetised even if its source isn't (the
  Connections `Names` was the one that relied solely on the bridge sort). All dynamic dropdowns across
  the console are confirmed sorted.

### Docs
- Folded two recurring rules into `.claude/ui-contract.md` + `daxter-new-feature`: **every page gets
  its OWN Frequent context** (separate per page — never reuse another's), and **all dropdowns are
  sorted + type-scoped**.

## [1.11.1] - 2026-06-04

### Fixed
- **Workspace/dataset pickers are now sorted and model-only.** Dropdowns showed datasets in raw API
  order and included Fabric internal **dataflow-staging** artifacts (`StagingLakehouseForDataflows`,
  `StagingWarehouseForDataflows`, etc.) that `/datasets` surfaces but aren't user semantic models. The
  `DaxterUi` bridge now **alphabetically sorts** workspaces + datasets and **filters those staging
  artifacts** from the dataset picker — once, so every page (Connections, Model Edit, Query…) inherits
  it. (Lakehouse/warehouse *default* semantic models share their item's name and have no public flag,
  so a few may still appear — Microsoft is decoupling/sunsetting them.) Folded the "sorted, scoped
  dropdowns" rule into the UI contract.

## [1.11.0] - 2026-06-04

### Added
- **Take control of a model + bind its connections to a gateway** — a new service-level capability
  (ownership + gateway binding live in the Power BI service, not in the model, so this is REST, not XMLA).
  Across all surfaces:
  - **Web** — a new **Connections** page: pick a model (Frequent + deep-link + Back), see its current
    data sources and bindings, **Take over** ownership, **Discover** bindable gateways, pick one and
    optionally map specific gateway connections, then **Bind** — each write behind a confirm modal and
    the **Allow writes** gate (prod-blocked), with the global busy overlay.
  - **MCP** — `daxter_discover_gateways`, `daxter_gateway_datasources`, `daxter_take_over`,
    `daxter_bind_to_gateway` (dry-run by default; `execute=true` + writes enabled to apply).
  - **CLI** — `daxter ws discover-gateways`, `ws gateway-datasources --gateway`, `ws takeover`,
    `ws bind-gateway --gateway [--datasources]` (dry-run unless `--yes`).
  - Core: `PowerBiRestClient.TakeOverAsync` / `DiscoverGatewaysAsync` / `GatewayDatasourcesAsync` /
    `BindToGatewayAsync` (`Default.TakeOver` / `Default.DiscoverGateways` / `Default.BindToGateway`).
  - **Scope note:** supports on-premises and VNet **gateway** bindings. Per-source **shareable
    cloud-connection** "Maps to" has no public REST API (UI-only in the Service), so it's shown
    read-only with guidance. (`PowerBiRestClientTests` cover the bind-body + response mapping.)

## [1.10.4] - 2026-06-03

### Fixed
- **Busy spinner now shows on model/XMLA loads** (clicking a table in Model Edit, loading roles/
  relationships/columns, the DAX-query field tree, and model-edit dry-runs). These handlers were
  already wrapped in the global `UiBusy` overlay, but the ADOMD/TOM work behind them
  (`connection.Open`, `Execute`, `ReadTable`, `SaveChanges`) is **synchronous** and ran on the Blazor
  circuit thread — so the "show overlay" re-render couldn't paint until the blocking read had already
  finished. The `DaxterUi` bridge now **offloads the synchronous XMLA/TOM work to a thread-pool thread**
  (`Task.Run`) at the `XmlaAsync` choke point and the `ModelEditService` read/edit methods, freeing the
  circuit thread to render the spinner immediately. (REST loads already yielded, so they were unaffected.)

## [1.10.3] - 2026-06-03

### Fixed
- **Long partition refreshes no longer fail mid-run on token expiry.** A serial multi-partition
  refresh (e.g. `daxter_refresh scope=partitions`/`AllPartitions`) reused one XMLA session — and the
  one access token captured when it connected — for the whole job. On big tables the job outran the
  token's lifetime and died with **`Refreshing the expired access-token has failed`** (observed at
  partition 41 of 67, ~21 min in). The worker now opens a **fresh session per partition** (a cheap,
  silent token-cache acquire via the new `SerialPartitionRefresh` Core helper), so a refresh of any
  length keeps a valid token and runs to completion without manual batching. Cancellation still aborts
  the in-flight partition. Covered by `SerialPartitionRefreshTests`.

## [1.10.2] - 2026-06-03

### Fixed
- **Delete on Model Edit now shows a confirmation you can't miss.** Clicking a row's **Delete** appeared
  to do nothing: the per-row `@onclick:stopPropagation` was on the `<td>` (no handler) so it never
  fired — the click also selected the row — and the confirmation rendered as a card at the *bottom* of
  the page, off-screen. Delete now routes through the shared **confirm flow shown as a centered modal
  popup** (dry-run preview → **Confirm** / **Cancel**), with `stopPropagation` on the button so it no
  longer also selects the row. On Confirm it deletes and refreshes the list. Applies to Parameters,
  Roles, Relationships, and Tables.
- **Frequent-sidebar clicks now load on the Model Edit page.** Deep-link recall (`?ws=&ds=`) only ran
  in `OnInitializedAsync`, which doesn't re-fire when you click a Frequent shortcut while already on the
  page (same component → only the query changes). Model Edit now applies it in `OnParametersSetAsync`
  (guarded against re-applying the same value) and shows the loading spinner while it loads.
- **Loading spinner on Pipelines + Audit.** Their Frequent-triggered loads (and index loads) now drive
  the global busy overlay too, so every Frequent page shows a spinner while loading (matching Explore /
  Refresh / Model Edit).

### Changed
- **Confirmations are now centered modal popups** (overlay) instead of inline cards, so an Apply/Delete
  confirm is never lost below the fold.
- **Blocking busy overlay on every seconds-long click, across all pages.** Query (Run DAX, load
  workspaces/datasets/objects), Home (health check, check-for-updates), and Gateways now drive the
  global `UiBusy` overlay — which covers the screen and intercepts clicks — so the user can't fire a
  second action into a half-loaded page (matching Explore / Refresh / Pipelines / Audit / Model Edit).
- **Descriptive, self-identifying container names.** Containers no longer show as Docker's random
  names (`determined_hamilton`). The `.mcpb` extension (v1.10.2) now launches the MCP server as
  **`daxter-mcp-<epoch>-<pid>`** (shell-wrapped `docker run … --name …`, with darwin/win32 variants;
  `exec` preserves the stdio pipe), and the `bin/daxter` CLI wrapper names runs **`daxter-cli-<epoch>-<pid>`**
  (the web console is already `daxter-web`). The suffix is a timestamp+pid — unique per launch so
  concurrent clients don't collide; there is no per-Claude-session label because no session identity
  is passed into `docker run`. Requires reinstalling the `.mcpb` to take effect.

## [1.10.0] - 2026-06-03

### Fixed
- **M source now shows for incremental-refresh tables.** When a table has an incremental refresh
  policy, its M query template lives on the policy (`BasicRefreshPolicy.SourceExpression` /
  `TMSCHEMA_REFRESH_POLICIES.[SourceExpression]`), not on the auto-generated partitions — so the
  Explore **M code** view and the Model Edit **Tables** click-load came up blank. Both now read the
  policy source. The Model Edit Tables editor also **flags policy tables and blocks Apply** (saving
  would replace the table and drop the refresh policy — edit those in Tabular Editor / raw TMSL).

### Added
- **Model editing in the web console — `Model Edit` page.** Model editing is now writable in **all three
  surfaces** (CLI, MCP, **UI**), all calling the same `Daxter.Core` **`ModelEditService`** — one engine,
  no duplicated logic. The new `/model-edit` page lets you add / edit / remove:
  - **Parameters** (shared M expressions), **RLS roles** (permission + members + table filters),
    **relationships** (from/to columns, cross-filter, active), and **tables** (calculated, or import with
    an M source + typed columns).
  - **Click a row to load it** into the editor (shows its current definition); **Preview** dry-runs the
    change; **Apply** writes it. Gated behind *Allow model edits* (a `.bim` backup is taken before every
    apply — XMLA edits are irreversible for PBIX download). Workspace/dataset picker with Frequent recall.
  - New Core read `ModelMetadataService.Relationships()` (resolves from/to `table[column]`, active,
    cross-filter) + `DaxterUi` bridge reads for relationships and per-role members/filters.
  - **Relationship From/To pickers**: table + column **dropdowns** (no typing/guessing column names).
  - **Incremental refresh policy editing**: selecting a policy-managed table opens a **refresh-policy editor**
    (source M + rolling-window & incremental granularity/periods/offset + polling expression) — updates the
    policy in place via `ModelEditService.UpdateRefreshPolicy` without disturbing the table's partitions.
  - **Confirm-on-Apply**: every **Apply** (parameter/role/relationship/table/refresh-policy) first dry-runs the
    change and shows a **Confirm apply / Cancel** card with the exact preview — no write happens until you confirm.
- **Global loading overlay.** A circuit-scoped `UiBusy` service drives one blocking spinner overlay
  in `MainLayout`: any slow async load — clicking a row to edit, picking a workspace/dataset, or
  **arriving on a page from a Frequent/deep-link click** — shows the spinner and blocks clicks until
  it finishes, so you can't queue overlapping loads or click into a half-loaded page. Wired into the
  Model Edit, Explore, and Refresh pages; the pattern is now mandatory in `daxter-ui` + the UI contract.

## [1.9.0] - 2026-06-03

### Added
- **Refresh scheduler engine — one shared queue, all interfaces.** DAXter is one integrated system:
  the CLI, MCP server and web console are thin interfaces over a single `Daxter.Core` engine. Refreshes
  now route through a shared **`RefreshQueueStore`** (file-backed on the `~/.daxter` volume) drained by a
  single **`RefreshScheduler`** worker **hosted by the web container**. The worker serializes **one
  refresh per model at a time** (different models run in parallel), honours ordered per-partition
  processing and `--retries`, and writes a heartbeat so any interface can tell a worker is live.
  - **New tool / commands:** `daxter refresh status` (CLI) and `daxter_refresh_jobs` (MCP) list the
    shared queue (queued/running/finished, across **all** interfaces) and show worker liveness — bringing
    the tool count to **49**.
  - The web **Jobs** page now shows refreshes enqueued by the CLI and MCP server too (same shared queue),
    with a **Source** badge per job (Web/CLI/MCP) and a **worker-liveness banner** so you can see at a
    glance whether queued jobs will actually run. Cancel / Resume / remove operate on the shared queue.
  - **Verified end-to-end:** the CLI enqueued two jobs for different models; the web container's worker
    started, drained both in parallel, and reported status — with graceful per-job failure handling.

### Changed
- **CLI/MCP refreshes now QUEUE instead of executing inline.** `daxter refresh model|table|partition|partitions`
  and `daxter_refresh` enqueue a job (returning a **job id**) and the worker runs it — guaranteeing two
  refreshes can never run against the same model at once, no matter which interface launched them. Dry-run
  prints the **plan** (connection-free); enqueueing needs no live XMLA connection. Run the DAXter web
  container to host the worker that drains the queue (it drains the queue whether or not you use the UI).
  `refresh trigger` (REST, async/server-managed) and `cache clear` are unchanged (not queued).

### Note
- The web Jobs list moved from `jobs.json` to the shared `queue.json` — existing job *history* resets once
  (estimates in the separate history store are preserved). In-flight jobs are not auto-resumed across this
  upgrade.

## [1.8.4] - 2026-06-03

### Added
- **Model editing now covers relationships and structured (import) tables** — completing the
  "create a table + relationships + measures" story.
  - **Relationships:** `model edit relationship` / `delete-relationship` (CLI) and
    `daxter_edit_relationship` / `daxter_delete_relationship` (MCP) — create/alter/delete a
    single-column relationship `fromTable[fromColumn] → toTable[toColumn]` (many-to-one), with
    cross-filter (`single|both|automatic`) and active/inactive.
  - **Import tables:** `model edit import-table` (CLI) / `daxter_create_import_table` (MCP) —
    create/replace a table with an **M (Power Query) source** partition + typed sourced columns
    (`Name:dataType:sourceColumn`). Complements the existing calculated-table support.
  - Same model-edit gate + `.bim` backup + dry-run as the other edits. **Verified live**: created
    two import tables + a relationship, read them back, then deleted them (model left clean).

## [1.8.3] - 2026-06-03

### Fixed
- **Partition refresh order is now actually honored.** v1.8.2 changed the TMSL shape but Analysis
  Services *still* ignored it — live testing showed partitions refreshing oldest-first regardless of
  `--order`. DAXter now executes **one single-partition refresh at a time, sequentially**, so the
  **client** drives the order (the engine can't be made to order partitions within a single TMSL
  request). `refresh partitions --order newest-first|oldest-first`, and a `--partitions` subset in
  list order, now process in the exact order requested, with per-partition progress; each partition
  honors `--retries`. **Verified live:** a newest-first subset refreshed `2026Q206 → 2026Q205 →
  2026Q204` in that order (newest committed first). `MaintenanceService.BuildPartitionsRefresh` is
  replaced by `BuildOrderedPartitionCommands`.

## [1.8.2] - 2026-06-03

### Fixed
- **Partition refresh `--order` now actually orders.** `refresh partitions --order newest-first|oldest-first`
  previously put all partitions into a *single* TMSL `refresh` operation, and the engine reordered them
  (observed: oldest-first regardless of the flag). Each partition now gets its **own** `refresh` operation
  inside the `maxParallelism: 1` sequence, so they process strictly in the requested order.

### Added
- **Retry on transient failure for refreshes.** New `--retries N` on the CLI (`refresh model/table/
  partition/partitions/trigger`, `cache clear`) and a `retries` argument on the `daxter_refresh` /
  `daxter_clear_cache` MCP tools. On a transient error (connection drop, timeout, throttling) the
  operation is retried up to N more times with linear backoff (5 s, 10 s, … capped at 30 s); each retry
  is logged. Default `0` (single attempt). *Note: this retries the operation within the process — it
  cannot recover from the process being killed externally.* Backed by `Daxter.Core.RetryPolicy`.

## [1.8.1] - 2026-06-03

### Fixed
- **Model editing now applies correctly via TOM `SaveChanges()`.** v1.8.0 built surgical TMSL
  (`createOrReplace` of a `measure`/`column`/`expression`) by hand — which the Power BI engine
  **rejects** ("Unrecognized JSON property: measure"). `ModelEditService` was rewritten to use the
  Tabular Object Model: it connects, mutates the in-memory model, and applies with `SaveChanges()`
  (the engine-sanctioned path). Dry-run now shows a concise **change preview** (raw TMSL still shows
  the TMSL). The safety model is unchanged: dedicated gate, `.bim` backup before every apply, PROD
  guard, irreversible-for-PBIX warning.
- **Verified live** against a real model — create/alter/delete of measures, parameters, RLS roles,
  calculated columns, calculated tables, and the raw-TMSL hatch all apply and read back; deletes
  remove cleanly. Notes: RLS **role members must be real Entra identities**, and a **calculated
  partition can't be converted to an M source** (engine rules, surfaced as clear errors).

## [1.8.0] - 2026-06-02

### Added
- **Model editing** — DAXter can now create/alter/delete model objects over XMLA (TMSL), reversing the
  previous "editing is out of scope" stance. Covered objects: **measures, parameters / shared M
  expressions, RLS/OLS roles, calculated columns, partition (M) sources, calculated tables**, plus
  delete of each and a **raw TMSL** escape hatch. The new `Daxter.Core/Editing/ModelEditService` builds
  the exact TMSL that executes (so the dry-run preview == what's applied), reusing the existing XMLA
  command path.
  - **CLI:** `daxter model edit measure|parameter|role|column|source|calc-table|delete-*|tmsl`
    (`--dry-run` default; `--yes` to apply; `--force` for prod).
  - **MCP:** 12 new tools (`daxter_edit_measure`, `daxter_delete_measure`, `daxter_set_parameter`,
    `daxter_edit_role`, `daxter_edit_calculated_column`, `daxter_set_partition_source`,
    `daxter_create_calculated_table`, `daxter_delete_*`, `daxter_edit_tmsl`), all annotated
    `Destructive` and **dry-run by default** — 45 tools total.
  - **Web:** *Allow model edits* toggle on the Configure page.
- **Safety, by design:**
  - **Dry-run by default** everywhere — you preview the TMSL before anything runs.
  - **Dedicated, stricter gate** separate from refresh writes: `DAXTER_MCP_ALLOW_MODEL_EDIT=true` or
    the web console *Allow model edits* (refresh's *Allow writes* alone is not enough).
  - **Automatic `.bim` backup** to `~/.daxter/backups/` before every apply — your practical "undo".
  - **PROD guard** still applies (`--force` / `DAXTER_MCP_BLOCK_PROD_WRITES`).
  - ⚠ **Editing a Power BI Desktop model over XMLA is irreversible for PBIX download** — keep your
    original `.pbix`; requires the workspace XMLA endpoint set to **Read/Write**.

## [1.7.12] - 2026-06-01

### Added
- **MCP tools now carry behavior annotations.** The 30 read tools are marked `ReadOnly = true`, the
  two mutating tools (`daxter_refresh`, `daxter_clear_cache`) `Destructive = true`, and every tool
  gets a human-readable `Title`. Clients (e.g. Claude Desktop's per-tool permission list) can show a
  friendly name and distinguish reads from writes — and can auto-allow read-only tools where they
  support it. Writes remain gated regardless (dry-run unless writes are explicitly enabled).

### Docs
- **`SETUP.md`** — note how to grant tool permissions in one action via the Extensions **group
  dropdown** (set to *Allow*), clarifying that allowing a tool does **not** bypass the write gate.
- Ship the Claude Desktop extension bundle (`daxter.mcpb`) — install via **Settings → Extensions →
  Advanced settings → Install Extension** (the reliable path on Windows, incl. Microsoft Store
  builds where the file association isn't registered).

## [1.7.11] - 2026-06-01

### Fixed
- **Dataset/workspace names with an apostrophe now work everywhere** (e.g. `Reseller's Margin`).
  The name was written into the XMLA `Initial Catalog` **unquoted**, so a `'` corrupted the
  OLE-DB connection-string parse and `AdomdConnection` threw in its constructor — breaking
  `daxter_list_tables`, `daxter_query`, and `daxter_refresh` on those models. Connection-string
  values are now quoted per the ADO.NET rule (enclosed in the quote character the value doesn't
  contain; embedded quotes doubled). **Behavior change:** pass the name *raw* — any previous
  workaround of doubling the apostrophe (`Reseller''s Margin`) must be removed.
- **Real errors now surface from every MCP tool.** The runtime guard only relayed
  `DaxterException`, so any other failure (like the connection-string parse error above) showed
  the opaque *"An error occurred invoking …"*. It now returns the underlying message, and the
  `AdomdConnection` constructor is inside the connection try/catch so its parse errors are wrapped.

### Added
- **`dataset` and `workspace` arguments accept a name *or* an id (GUID).** A GUID is resolved to
  its canonical name (the XMLA endpoint addresses by name) and used raw in the TMSL refresh target;
  a plain name still resolves with no extra REST round-trip. Fixes GUID inputs that previously
  failed with `PowerBIEntityNotFound` / "not found".

## [1.7.10] - 2026-05-31

### Changed
- **Audit "Models" tab is now a filterable, exportable table** (was a bulleted "models without rules"
  list). It renders via the shared DAX-results grid — columns **Model · Status · Owner** — with a
  **Show** dropdown to filter by **Have rules / No rules / No readable parameters** (each with a live
  count) and **Export CSV** of the current view (leave it on *All* for the full list). The dataset
  **owner** (`configuredBy`) is now captured during the scan, and a new
  `PipelineRulesService.ClassifyModel` is the single, unit-tested place that buckets a model's status.

## [1.7.9] - 2026-05-31

### Changed
- **Published image is now multi-arch (`linux/amd64` + `linux/arm64`).** The GHCR publish job uses
  `docker buildx` with QEMU to build and push a multi-platform manifest, so **Apple Silicon (arm64)
  pulls a native image** instead of running the amd64 image under emulation. The Dockerfile's
  in-build test step is now conditional (`ARG RUN_TESTS`, default on); the multi-arch publish passes
  `RUN_TESTS=0` to skip re-running the suite under slow arm64 emulation — tests are still gated
  natively on amd64 by the `test` and Docker `image` CI jobs, and the code is platform-agnostic.

## [1.7.8] - 2026-05-31

### Docs
- **Install guide is now env-file-free.** Building on the v1.7.7 config unification, `SETUP.md` no
  longer has you create a `daxter.env` or pass `--env-file`: the Claude Desktop MCP entry is just
  the image + the `daxter-tokens` volume, and you sign in / set defaults (tenant, workspace,
  allow-writes) in the **web console**, which saves them to the volume the CLI/MCP/console all read.
  Install drops from 5 steps to 4. Env vars move to an **Advanced: service principal / headless**
  section (still a supported fallback for injecting an SP secret without writing it to the volume).
- **README, CLAUDE.md, `examples/mcp.md`** updated to match — the web-console runs and the MCP
  config block drop `--env-file`; "Allow writes" is enabled via ⚙ Configure (or the env fallback).

## [1.7.7] - 2026-05-31

### Changed
- **One config source across CLI, MCP, and the web console.** `DaxterConfig.FromEnvironment` — used
  by all three — now reads the settings the web console saves to the shared volume
  (`~/.daxter/console-config.json`) as the primary source, with env vars as a fallback. Precedence:
  explicit `--flag`/tool-arg **>** web-console config **>** env var **>** default. So workspace,
  dataset, tenant, client id/secret, prod-workspaces and allow-writes set in the UI are now honored
  by the CLI and MCP server too — no more "the console shows it but the MCP doesn't." A new
  `PersistedSettings` type is the single shape Core and the web both read/write; v1.7.6's allow-writes
  special-case folds into it.
- **The env file is now optional.** With config living on the shared volume, a `daxter.env` /
  `--env-file` is no longer required for normal use — it stays a supported fallback for
  headless/CI / service-principal injection. (Install-guide simplification to follow.)

## [1.7.6] - 2026-05-31

### Changed
- **MCP refresh/cache writes now honor the web console's "Allow writes" toggle.** The gate read
  only the `DAXTER_MCP_ALLOW_WRITES` env var, so flipping *Allow writes* in the web console
  (Configure) had no effect on the MCP server — it kept refusing even though the console showed
  writes enabled. The gate now also reads the saved toggle from the shared
  `~/.daxter/console-config.json`, so the web checkbox enables MCP refresh too (after a Claude
  Desktop restart). The env var still works.
- **PROD refresh/cache is now allowed by default (once writes are enabled).** The old "PROD-looking
  targets are always blocked over MCP" rule is now **opt-in** — set `DAXTER_MCP_BLOCK_PROD_WRITES=true`
  to re-block prod. Writes themselves remain **off by default**, so a prod refresh still requires an
  explicit opt-in to writes first.
  (`DaxterToolRuntime.WritesAllowed` = env OR console-config toggle; `ProdWritesBlocked` is the new
  opt-out; regression-tested.)

## [1.7.5] - 2026-05-31

### Fixed
- **"Run all rules" now matches the single Parameter-value-check (finder semantics).** The rule-set
  view inverted a "≠ X" check: it treated the saved check as a compliance assertion ("every model
  *should* be ≠ X") and listed the models that **were** X as "violations" — the opposite of what the
  single check (and the rule name) means. It now lists, per rule, the models whose value **satisfies**
  the condition — e.g. `DB_NAME ≠ DB_PROD` returns the models that are ≠ DB_PROD (the anomalies),
  consistent with the single check. Applied across Web, CLI (`--run-all-saved`), and MCP
  (`daxter_audit_run_all_saved`); the column is now **Matches** (matched / checked).
  (`RuleResult.Violations`→`Matches`, `Compliant`→`Matched`; `EvaluateRule` is finder-based and
  regression-tested.)

## [1.7.4] - 2026-05-31

### Fixed
- **Windows config merge no longer writes a UTF-8 BOM.** The PowerShell merge in `SETUP.md` used
  `Set-Content -Encoding UTF8`, which on Windows PowerShell 5.1 prepends a BOM — Claude Desktop
  then rejects the file with *"Could not load app settings… not valid JSON"* and **no** MCP servers
  load. It now writes BOM-free via `[System.IO.File]::WriteAllText(…, UTF8Encoding($false))`. Added
  a Troubleshooting row with a one-line repair for a config that already got a BOM.

## [1.7.3] - 2026-05-31

### Fixed
- **`daxter ws ls` and `ws gateways` no longer require a workspace.** Listing workspaces/gateways
  is a tenant-level operation, but the CLI resolved config with `requireWorkspace: true` and failed
  with `No workspace configured` even when the token was valid. They now resolve with
  `requireWorkspace: false`, matching the MCP `daxter_workspaces` / `daxter_gateways` tools.
  This also un-breaks the `ws ls` sign-in self-check the install agent runs in `SETUP.md`.
  (`ConnectionOptions.Resolve` gained a `requireWorkspace` parameter; regression-tested.)

## [1.7.2] - 2026-05-30

### Docs
- **Install-guide overhaul (`SETUP.md`)** for an agent-driven, non-technical-user experience:
  - **Sign in (step 4) before the final restart (step 5)** — Claude Desktop loads the `daxter`
    config on its first restart and the cached token is already there, so the tools work first try.
  - **The agent self-verifies** the whole path with the CLI (`ws ls`) instead of asking the user
    to run a test, then reassures them it's working and shows their workspaces.
  - **"Keep it simple" guidance** — narrate high-level ("step 2 of 5…"), hide raw command output
    unless something errors; reduce it to the two human actions (sign in, restart).
  - **`docker info` daemon-running gate** before install; **web console** is the primary,
    single sign-in path (separate process, absolute `--env-file`, configurable port, persistence);
    **Windows PowerShell** config-merge; **`DAXTER_TENANT_ID`** recommended with a placeholder-trap
    opt-out; clarified that **no `…mcp` container in `docker ps` is normal** (stdio).
- **New "Remove / uninstall DAXter" section** — unhook from Claude Desktop, stop the web console,
  and optional cleanup (token volume, image, env file).
- Web console container is now named **`daxter-web`** for an easy stop/remove.
- **Recommended setup prompt** points the agent at the **raw** `SETUP.md` for exact commands and
  names "Claude Code" + the two user hand-offs explicitly.

## [1.7.1] - 2026-05-30

### Changed
- **Simpler in-chat sign-in.** `daxter_login` now returns a clean, click-first message —
  *"🔐 Signing you in to Power BI: 1) Open <link> 2) Enter code: XXXX 3) Sign in"* plus the
  next step — built from the structured device-code URL/code instead of the raw MSAL string.
  When a cached token is still valid it just says you're already signed in. Matches the
  streamlined `SETUP.md` step 5 (`DaxterToolRuntime.FormatLoginPrompt`, unit-tested).

### Docs
- **`SETUP.md`** rewritten for a faster first run: a `docker info` "is the daemon running?"
  gate before install; the web console called out as a separate process (with an absolute
  `--env-file` path, configurable port, and persistence note); `DAXTER_TENANT_ID` promoted to
  recommended with a placeholder-trap warning; a **Windows PowerShell** config-merge alongside
  the macOS/Linux one; and a dead-simple, click-the-link sign-in step.
- **`README.md` / `CLAUDE.md` / `examples/mcp.md`** synced to the current surface (33 MCP tools,
  audit rule sets, the web Audit page).

## [1.7.0] - 2026-05-30

### Added
- **Three XMLA/REST capabilities that were CLI/MCP-only are now in the web console:**
  - **Test RLS** (Explore → a dataset's **Test RLS** command). Run a DAX query while
    **impersonating a role and/or a user (UPN)** to see exactly what they'd see. The role
    dropdown is populated from the model's roles; the effective-user box takes any UPN. Read-only,
    but the signed-in identity must be an **admin** of the workspace/model to impersonate
    (`Roles` + `EffectiveUserName` on the XMLA connection).
  - **Gateways** (new **Gateways** nav page). Lists the on-premises data gateways visible to the
    signed-in identity (REST `/gateways`) — only gateways the caller administers return a name.
  - **Deployment Pipelines** (new **Pipelines** nav page). Lists deployment pipelines; click one
    to drill into its **stages** (Dev → Test → Prod workspaces) and recent **operations**
    (REST `/pipelines`, `/pipelines/{id}/stages`, `/pipelines/{id}/operations`).

### Added
- **Deployment-rule check on the Pipelines page (model-first).** Pick a **model** and DAXter finds
  every accessible pipeline that contains it, then lays out **one card per stage** (Dev → QA → Prod).
  Each card shows two sections: **Model Parameters** (every parameter + its value in that stage, read
  over XMLA) and **Deployment Rules** (the parameters whose value differs from the source/Dev stage =
  the inferred rule overriding them there). Rule values are tinted for quick scanning. The public Power
  BI REST API has no operation to read rule objects directly, so these are inferred from per-stage value
  differences; stages where the model can't be read are noted. Raw stages + operations remain under a
  collapsible "Browse pipelines" section. (Requires the signed-in identity to have pipeline access —
  works in the web console, not the MCP service principal.) The Pipelines page has its own
  **Frequent · Pipelines** sidebar with a **Models** group — click a recent model to rebuild its
  stage board instantly.
- **Same check on the CLI + MCP.** The rule-inference logic lives in `Daxter.Core` and is shared by
  all three layers:
  - **CLI**: `daxter pipeline rules --pipeline <id> --model <name>` — prints a `Parameter | <stage 1> | <stage 2> | … | RuleApplied` table (table/csv/json via `--output`) and a stderr summary `(N stages, N parameters, N differ → N deployment rules inferred)`.
  - **MCP**: `daxter_pipeline_rules(pipelineId, model)` — same matrix as JSON, with stage-read failures listed as notes.
- **Pipeline scan results persist to disk** — `PipelineScanStore` now saves completed scans to
  `~/.daxter/pipeline-scans.json` (last 20) on the mounted volume, so container restarts don't lose them.
- **Recent audits sidebar** — new `AuditHistoryStore` tracks the last 10 ran audits in
  `~/.daxter/audit-history.json`. The Audit page sidebar shows them under **Recent audits** with
  one-click recall (clicking re-loads the persisted scan immediately), plus per-entry remove and clear.
- **New `/audit` page** — cross-cutting checks across pipelines/workspaces/models, with an
  **Audit type** picker so new audits drop in without rearranging the layout. Today: Pipeline
  deployment-rule audits (pick a pipeline → run "models without rules" / "parameter value check");
  more audit types (refresh status, measure dependencies, etc.) slot in next to it. Has its own
  **Frequent · Audit** sidebar tracking the most-used pipelines (each item links to
  `audit?pipeline=<id>`). Replaces the audit tabs that previously sat on the Pipelines page; the
  Pipelines page now links across to /audit and keeps the per-model deployment-rule board.
- **Pipeline-wide audits.** Below each model board, a new **Audits** card with two tabs:
  - **Models without rules** — scans every model in the pipeline and lists the ones whose parameter values are identical across every stage (no deployment rule or manual override in effect anywhere).
  - **Parameter value check** — pick a stage workspace, a parameter name, and an expected value (equals / does-not-equal); lists every model whose parameter matches in that stage. Useful for verifying a rule is consistently applied (e.g. all models in *Prod* have `WAREHOUSE_URL = <prod-endpoint>`).
  Both tabs share a single scan that streams progress (`N/Total`) and runs once per pipeline.
  Mirrored on the CLI (`daxter pipeline audit --pipeline X` with `--mode no-rules|check --stage Y --param Z --value V [--not-equals]`) and on the MCP (`daxter_pipeline_models_without_rules`, `daxter_pipeline_param_check`).

### Added
- **Saved param-checks (Audit page).** After running a Parameter-value check (e.g. `WAREHOUSE_URL = …` in
  a stage), a **★ Save this check** button stores it — with an editable, auto-suggested name
  (`WAREHOUSE_URL = <prod-endpoint> · Prod`) — to a new **Saved checks** group in the Frequent · Audit
  sidebar. Clicking a saved check deep-links back (`audit?pipeline=…&type=check&stage=&param=&value=&ne=`),
  selects the pipeline, and prefills the check. Saved via a **shared `Daxter.Core` store**
  (`~/.daxter/audit-saved.json`) so they survive restarts **and are usable from CLI + MCP**:
  - CLI: `daxter pipeline audit --list-saved` and `daxter pipeline audit --saved "<name>"`.
  - MCP: `daxter_audit_list_saved` and `daxter_audit_run_saved(name)`.
  Each has a remove × in the sidebar; legacy saved entries are migrated out of `audit-history.json`
  on first read.
- **Rule sets — ➕ Add rule & Run all.** The check's save button is now **➕ Add rule**, and after
  saving it clears the param/value (keeps the stage) so you can stack several rules quickly (like
  Power BI's "+ Add rule"). A **▶ Run all rules** button evaluates every saved rule for the pipeline
  against the current scan in one table — **Rule · Param · Stage · Expected · Compliant/Checked · ⚠ Violations**
  (expand a row to see the offending models). Same across surfaces: CLI
  `daxter pipeline audit --run-all-saved --pipeline X` and MCP `daxter_audit_run_all_saved(pipelineId)`,
  all sharing the one `Daxter.Core.PipelineRulesService.EvaluateRule`.
- **Audits scope to a single model (or all).** Every audit can target one model instead of the whole
  pipeline — much cheaper (one model's stages, read in parallel). `Daxter.Core.ScanModelAsync` produces
  a one-model scan that the same evaluators run on. Available everywhere:
  - **MCP:** `daxter_pipeline_param_check(…, model: "Sales")` (reports the model's actual value + pass/fail)
    and `daxter_audit_run_all_saved(pipelineId, model: "Sales")` ("run all audits for Sales").
  - **CLI:** `daxter pipeline audit --model Sales …` scopes `--mode check` / `--run-all-saved` to that model.
  - **Web:** a **Model** box on the Audit page narrows the param-check + Run-all to one model.

### Fixed
- **DAX Query Fields pane didn't populate on first load** — it only worked after closing and
  re-opening it. `LoadObjects` now calls `StateHasChanged()` on completion (Blazor doesn't
  auto-render after an `OnAfterRenderAsync` await), so the tree paints the first time. The Fields
  pane now also starts **hidden by default** (the model still loads in the background so editor
  autocomplete works whether or not the pane is open).
- **"Run →" / "Top N rows" / Recent-query hand-off to the DAX Query page.** Prerendering rendered
  the target page twice in two DI scopes; the throwaway prerender pass consumed the handed-off
  query, so the DAX Query page opened empty (showing its placeholder). Prerendering is now disabled,
  so pages initialize once on the live circuit and the query arrives correctly.

### Changed
- **New "Slate & Teal" theme** — re-skinned to an enterprise-analytics look: navy chrome topbar
  (`--chrome:#1b2533`) with muted→bright nav states, a teal interactive accent (`--accent:#0f766e`),
  cool slate neutrals, lighter borders, and a bit more depth. Because the stylesheet was already
  fully tokenized, the whole re-theme was ~20 `:root` token-value edits + one topbar rule — every
  page, card, grid, and badge picked it up automatically. Frozen into `.claude/ui-contract.md` as
  the project's design contract; `ui-consistency` enforces it. (Established via the `ui-consistency`
  ESTABLISH flow.)
- **UI professional pass + theme tokenization.** Promoted the palette to a full CSS-variable token
  set (text / surface / line / link / status / brand, plus `--radius*`, `--shadow*`, `--ring`) and
  replaced ~60 hardcoded hex values with `var(--…)` — re-theming is now editing a handful of tokens,
  not hunting the stylesheet. Added subtle depth (card/topbar/grid shadows), button hover states, and
  **keyboard focus rings** (`:focus-visible`) for accessibility — no layout change. Added the standard
  **← Back** button to the **Refresh** and **Audit** pages (both are reached via deep-link from other
  pages, per the UI contract). Verified with the `ui-consistency` skill: theme-drift and the two
  missing back-buttons were the only gaps; both now closed.
- **Measure "Run →" now loads the definition.** Instead of a bare `EVALUATE ROW("M", [M])`
  reference, it opens the DAX Query page with a runnable `DEFINE MEASURE 'Table'[M] = <definition>
  EVALUATE ROW("M", [M])` block — so the measure's actual logic is visible and editable. The home
  table is resolved via a TableID→table join.
- **Measures now appear in the Frequent · Explore sidebar.** Opening or running a measure records
  it; the most-used show under a new **Measures** group (click to open its definition).
- **Measures view is now a searchable grid** (Explore → a dataset → **Measures**): a search box
  (matches name, display folder, or definition text) over a **Measure | Definition | Actions**
  table. The **Definition** shows the DAX inline, collapsed with a fade and **click-to-expand**;
  **Run →** runs the measure on the DAX Query page; **Export CSV** downloads the listed measures
  + definitions (respects the current search filter). (Was a click-through list of names.)
- **Explore split into two pages.** The DAX editor moved out of the Explore tabs into its own
  **DAX Query** page (`/query`); **Explore** (`/explore`) is now purely the drill-down browser.
  "Run DAX →", "Top N rows", "Run measure", and clicking a **Recent query** in the sidebar all
  open the DAX Query page **pre-filled** (and run, where applicable) — carried across via the
  shared `ExploreActions` bus. The Recent-queries sidebar + Frequent shortcuts show on both pages.
  The DAX Query page has a **← Back** button that returns to the previous page (e.g. the Explore view you came from).
- **Configure moved to a ⚙ gear icon** in the top-right corner (out of the main nav), so the
  app's own settings aren't confused with a workspace/dataset's configuration. The gear **toggles**
  — a second click returns to the previous view.

## [1.6.8] - 2026-05-30

### Added
- **DAX autocomplete + signature help.** The query editor now suggests as you type (≥2 chars):
  the model's **tables, columns, and measures** (from the loaded Fields tree) plus ~130 **DAX
  functions** and keywords. Arrow keys to navigate, Enter/Tab to accept, Esc to dismiss; the
  chosen reference is inserted correctly (`'Table'[Column]`, `[Measure]`, `FUNCTION(`).
  Inside a function call, a **signature hint** shows its arguments with the **current argument
  highlighted** (e.g. `CALCULATE(Expression, **Filter1**, Filter2, …)`), and field suggestions
  are **biased to what that argument expects** (a `Table` arg surfaces tables first, a `Column`
  arg surfaces columns, an `Expression`/`Value` arg surfaces measures + columns). The signature
  hint also shows a **one-line description** of the function and a **↗ docs** link to Microsoft
  Learn (and each function suggestion in the dropdown has its own ↗ docs link). Self-contained —
  no external editor/CDN dependency (a small `dax-complete.js` over the textarea).
- The **Fields tree loads automatically** when you open the DAX tab / pick a dataset — no more
  "Load fields" button.
- **Taller DAX editor**, and **Recent queries moved to the left sidebar** (under
  *Frequent · Explore*) — freeing the workbench width. Clicking a recent query still runs it on
  the DAX tab (via a small `ExploreActions` bus); remove/clear are in the sidebar.
- **DAX syntax highlighting.** The query editor now color-codes **functions**, **keywords**,
  **strings**, **'table'** and **[column/measure]** references, **numbers**, and **comments** — via
  a highlighted overlay behind a transparent textarea, painted client-side as you type (no server
  round-trip), kept in sync after Format / field insertion. Self-contained (no editor library).
- **Format button + live validation** in the DAX editor. **⌗ Format** pretty-prints the query
  (indents by call depth, one argument per line, keeps simple calls inline, uppercases
  functions/keywords) — done **locally**, the query is never sent anywhere. A validation strip
  flags **unbalanced parentheses / brackets / quotes** and a missing `EVALUATE`/`DEFINE` as you
  type (the exact class of error that an unclosed `[` causes).

### Fixed
- **Interrupted jobs can be resumed or dismissed.** A job that was queued/running when the
  container was bounced comes back as **Interrupted** — but there was no way to clear or re-run
  it. Now each finished job (interrupted/failed/canceled/succeeded) has **↻ Resume** (re-runs the
  same spec, re-checking the write gates) and **×** (remove from the list), on both the Jobs page
  and the Refresh-page tracker. **Clear finished** now also removes interrupted jobs.
- **Frequent sidebar disambiguates same-named tables/datasets.** Standardized models often reuse
  table names across datasets, so the Frequent list couldn't tell two `Sales` facts apart. Each
  table now shows its **dataset** as a subtitle (and each dataset its **workspace**), plus a hover
  **tooltip** with the full context (e.g. *"Sales — in dataset 'Retail Model' (workspace
  'Sales Analytics - Dev')"*).

## [1.6.7] - 2026-05-30

### Added
- **CLI parity with the web Refresh page + Fields tree** (matching the MCP additions in 1.6.6):
  - `daxter refresh partition --table T --partition P` — refresh **one** partition.
  - `daxter refresh partitions --table T [--partitions A,B,C] [--order …]` — refresh **all** or a
    **subset** of a table's partitions, now processed **in order** (TMSL `Sequence`,
    `maxParallelism: 1`). `--type` covers full/automatic/**calculate**/dataOnly/clearValues.
  - `daxter model columns [--table T]` — list a model's columns (name, hidden).

## [1.6.6] - 2026-05-30

### Added
- **MCP parity with the web Refresh page + Fields tree.**
  - `daxter_refresh` now supports **`scope=partition`** (refresh one named partition) and a
    **`partitions`** argument (comma-separated subset) for `scope=partitions`; all partition
    refreshes are wrapped in a TMSL `Sequence` with `maxParallelism: 1` so they process **in
    order** (a plain multi-partition refresh runs in parallel). `type` already covered
    full/automatic/**calculate**/dataOnly/clearValues.
  - New **`daxter_columns`** tool — list a model's columns (name, hidden), optionally for one
    table (excludes the internal RowNumber column). Backed by `ModelMetadataService.Columns`.
  - **27 MCP tools** total (`daxter_login` + 24 read + 2 gated write).

## [1.6.5] - 2026-05-30

### Added
- **DAX tab is now a Power BI Desktop-style workbench.** Three panes: a **Fields** tree on the
  **left** (every non-hidden table → its columns and measures, searchable; click a field to
  **insert its reference** — `'Table'[Column]` or `[Measure]` — into the query), the **query +
  results** in the center, and **Recent queries** on the **right**. The Fields and Recent panes
  are **collapsible** (×, with one-click reopen) and the query editor is full-width monospace.
  The fields load when you open the DAX tab (or pick a dataset). Backed by a new
  `DaxterUi.ModelTreeAsync` (TMSCHEMA DMVs).
- **Console uses the full window width.** The content area no longer caps at 1000 px and centers
  (which wasted most of a wide screen and squeezed the DAX results); it now fills the space next
  to the sidebar, so grids and the DAX workbench are actually usable. Reading-width text still
  caps for legibility.
- **One-click "run it" actions in Explore.** Drill into a **table** → **Top 10 rows →** /
  **Top 100 rows →** buttons jump to the DAX tab pre-filled with `EVALUATE TOPN(n, 'Table')`
  and run it. **Measures** are now a **clickable list** that drills into a **measure detail**
  showing its **DAX definition** (expression + display folder) with a **Run measure →** button
  (`EVALUATE ROW("Measure", [Measure])`) — so you can both **read** and **run** it. Table/measure
  names are DAX-escaped; the measure detail joins the Browse back-stack.
- **Per-partition progress for "all / pick partitions" refreshes.** These jobs now refresh each
  partition as its own step, so the Jobs page shows **"Partition X / N: <name>"**, a progress bar
  that advances per completed partition, and an **activity log with the time each partition took**
  (e.g. `[2/7] ✓ 2026Q1 — 18s`). Cancel stops cleanly between partitions. (Trade-off: each
  partition is processed with the chosen type, so a `Full` of N partitions recalculates N times —
  in exchange for the live visibility.)

## [1.6.4] - 2026-05-29

### Fixed
- **Workspace/Dataset pickers couldn't show other values once selected.** A native `<datalist>`
  filters its suggestions to substrings of the current input, so a full value hid every other
  option. Each picker now has a **clear (×) button** (one click empties it and shows the full
  list again — clearing the workspace also resets the dataset) and **selects its text on focus**
  so you can immediately type a new search.

### Added
- **Dedicated Refresh page** (`/refresh`). Pick a workspace + dataset, then choose **what to
  refresh** — the **full model**, a **table**, or **partitions** (all **in order**
  newest→oldest / oldest→newest, or a **single** partition) — a **processing type** (Full /
  Automatic / **Calculate** / Data only / Clear values) — and **confirm** before it runs. The
  partition options are **table-aware**: a table with one partition just refreshes it (no
  "in order" choice); the order options appear only when a table has multiple partitions.
  Partitions can be refreshed three ways: **all** (in order), or **Pick partitions…** to
  check **one or more** specific partitions (select-all / clear helpers). Whichever you choose,
  the page shows the **exact ordered list** of partitions to process (the confirmation repeats
  it), and the refresh **truly processes them in that order** — the TMSL is wrapped in a
  `Sequence` with `maxParallelism: 1` (a plain refresh runs partitions in parallel).
  `MaintenanceService.BuildPartitionsRefresh` gained an optional `maxParallelism` and an
  explicit-partition-set overload.
- **Explore → ↻ Refresh** now carries only the **workspace + dataset** to the Refresh page (you
  pick the scope there); it no longer preselects a specific table.
- **Jobs survive a redeploy.** The job list is persisted to `~/.daxter/jobs.json` on the mounted
  volume, so restarting/upgrading the container keeps the refresh history. Jobs that were
  queued or running when the container stopped are reconciled to **Interrupted** on load (a
  write is never silently auto-resumed). Duration history (for ETAs) was already persisted.
  The page is **tabbed** — **New refresh** and **History**: picking a workspace+dataset loads
  the dataset's **refresh history** (read-only — works even for PROD or with writes off).
  The Jobs page now offers a **← Back to Refresh** link (keeping the workspace/dataset) when you
  arrive from a per-dataset "All jobs →" link, so you don't have to re-pick them.
  A **"Refreshing…"** banner shows while a refresh is in progress for the dataset, and a
  per-dataset job tracker (plus the **Jobs** page) lets you **cancel** and **see details**
  (status, duration, target, error). Refreshes deep-link from Explore (a dataset or table →
  **↻ Refresh…**).
- **Refreshes run as background jobs.** A single-worker `JobService` runs them **one at a time
  in launch order** (so partitions process in sequence), reusing Core's `MaintenanceService`
  (TMSL). Gated exactly like the MCP/CLI: only with **Allow writes** on, and **always refused
  for PROD** targets. **Cancellation** skips queued jobs and aborts a running refresh (closes
  its connection).
- **Jobs page shows progress, an activity log, and an ETA.** Each job has a live **Step**
  (Queued → Started → Connecting → Processing → Completed/Failed), a click-to-expand
  **activity-log timeline**, and — once a similar refresh has run before — an **estimated
  duration** from history (`~Ns est.`) with a progress bar (elapsed vs estimate, updated every
  second). Durations are remembered per kind+dataset+table in a persisted `JobHistoryStore`
  (`~/.daxter/job-history.json`). The **nav shows `Jobs (N)`** while N are active.
- **Browse is now the default Explore tab** (and listed first); the workspace list loads on
  arrival so you land straight in the explorer.
- **Back button in Browse.** The drill-down explorer now has a **← Back** that returns to the
  exact previous view — from a table's M code back to the full **list of tables**, from a command
  result back to the dataset's commands, and so on up the chain (browser-style history, not just
  the breadcrumb's level jumps). Disabled at the top; reset when you deep-link in from the
  Frequent sidebar.
- **DAX query history.** Successful queries are remembered and listed as **Recent queries**
  under the query box on the Explore → DAX query tab. Click one to **re-run it** (re-applies
  its workspace/dataset + DAX); each row shows a single-line preview, its dataset, and how many
  times you've run it. Remove individual entries (×) or **clear all**. Persisted to
  `~/.daxter/query-history.json` on the mounted volume (de-duped, newest-first, last 100).
- **"Frequent" sidebar — per page.** A left rail lists your **most-used workspaces, datasets,
  and tables** (by open count). It's **scoped to the current page**: Explore has its own
  Frequent set and Refresh has its own, and clicking an item deep-links into *that* page at the
  right level (a table even opens to its M code on Explore, or preselected on Refresh). Usage is
  tracked per page-context in a persisted `UsageStore` (`~/.daxter/usage.json` on the mounted
  volume) and only records targets that actually resolved, so typos don't pollute the list.
- **Version awareness + update check.** The image is stamped with its version at build time
  (`DAXTER_VERSION`, from the git tag); the web console shows it in the header, and the Status
  page has a **Version & updates** card that checks GitHub `releases/latest` on demand. If a
  newer release exists it shows the release link and **volume-preserving upgrade commands**
  (your sign-in + Configure settings are kept). Outbound-only, on click — no telemetry, no
  background polling. Fork-friendly via `DAXTER_REPO`.

### Changed
- **Docs lead with the web console for sign-in.** README and `SETUP.md` now present
  `daxter web` → **Sign in** as the easiest first-run path (the device-code dance moves to a
  one-click page), and document an **Upgrade** flow that reuses the `daxter-tokens` volume so
  config and tokens survive. Clarified that the volume — not the image — holds all state.

## [1.6.3] - 2026-05-29

### Security
- **Logs are redacted.** Every line captured by the in-app log passes through a tested
  `SecretRedactor` that masks JWT/OAuth access tokens, `Bearer` headers, and keyed secrets
  (`password=`, `client_secret=`, `access_token=`, `api_key=`, …). DAXter already injects tokens
  out-of-band (never in connection strings) and never logs DAX text, cell values, or the client
  secret — the redactor is a defense-in-depth net so a third-party exception message can't leak
  one either. (14 redactor tests.)

### Added
- **Logs page** in the web console — recent console activity in-app: every operation with its
  row count and timing (e.g. `datasets [Sales Analytics] → 94 rows in 887 ms`), sign-in, health checks,
  and errors. Level filter (All / Information+ / Warning+ / Error), auto-refresh (2 s), and
  Clear. Backed by a bounded in-memory `LogSink` (last 1000 entries, `DAXTER_LOG_BUFFER` to
  change) fed by an `ILogger` provider, so the same lines also go to the container console
  (`docker logs`). `DaxterUi` now logs each operation's start/result/failure.

### Fixed
- **Device-code sign-in no longer fails with `AADSTS7000218 / invalid_client`.** When an env had a
  service-principal `DAXTER_CLIENT_ID` (+secret) *and* used device-code auth (the default), the
  device-code flow reused that **confidential** app id as a public client — so Entra demanded a
  client secret and sign-in failed. Device-code now always uses a **public** client:
  `DefaultPublicClientId`, or a new optional `DAXTER_PUBLIC_CLIENT_ID` for your own native app.
  `DAXTER_CLIENT_ID` is now strictly the service-principal (confidential) id.
- **Sign-in page is now click-to-open.** The device-code prompt renders the verification URL as a
  **clickable link** (opens in a new tab) and the user code in a large, **one-click-copy** field —
  no more selecting/copying out of a text blob. (`DeviceLogin` now carries the structured
  `VerificationUrl`/`UserCode` from MSAL, not just the message.)
- **Confirmed: saving the configuration does not sign you out.** `Save()` only writes
  `~/.daxter/console-config.json`; the MSAL token cache (`~/.daxter/msal_cache.bin`) is a separate
  file that nothing deletes. (Changing identity fields — auth mode, tenant, client id — can make
  the *silent* token lookup miss because MSAL keys its cache by client+tenant, but the cached
  token is still on disk; the sign-in link above recovers it.)
- **Web sign-in now completes reliably.** The device-code flow returned the code but swallowed
  the background result, so after you entered the code on the page **nothing happened** (and a
  failed sign-in was invisible). The Status page now **awaits sign-in completion**, auto
  re-checks health when you finish authenticating, and **shows the reason** if sign-in fails.
  `MsalTokenProvider.StartDeviceLoginAsync` returns a `DeviceLogin(Message, Completion)`.
- **Auth errors are now actionable.** When an operation fails because the session ended (e.g.
  after changing the configuration), the error renders a friendly banner with a **link to the
  Status page** to sign in again — instead of the raw MCP-flavored "use the daxter_login tool"
  text. New shared `ErrorBanner` component, used by the result grid and the Browse explorer.

### Added
- Explore page: the **Workspace and Dataset pickers are now searchable** (type-to-filter via
  an input + `<datalist>`), which matters with ~dozens of workspaces.
- Explore is now **tabbed** — **DAX query** and **Browse**. Browse is a **drill-down explorer**:
  start from zero, click **Workspaces** → a workspace → **Datasets** → a dataset → run any model
  command (tables, measures, parameters, partitions, RLS, datasources, permissions, refresh
  history) or workspace command (reports, lineage), with **breadcrumb** navigation back up. A
  **Run DAX →** button jumps to the query tab with the drilled-into workspace/dataset filled in.
- Browse drill-down now extends to **tables**: **Tables** lists the model's tables as a clickable
  list; clicking a table shows its **Power Query (M) code** (rendered as a formatted code block)
  and its **partitions**, with a fourth breadcrumb level (`Workspaces › ws › ds › table`).
- A **"Load default"** link appears when the server has a default workspace/dataset
  (`DAXTER_WORKSPACE`/`DAXTER_DATASET`) — one click fills them in.
- **Editable Configure page** — change auth mode, tenant, default workspace/dataset, prod
  workspaces, and allow-writes; edits apply to the console immediately and **Save** persists
  them to the mounted `~/.daxter` volume (survives restarts). Backed by a `ConfigState`
  service the console uses for all operations; the env file / Claude Desktop snippet preview
  reflects the edits.

## [1.6.2] - 2026-05-29

### Added
- Result grid now has **pagination** (50 rows/page, First/Prev/Next/Last) and an
  **Export CSV** button.

### Fixed
- Explore page always showed "error" and never rendered results — the `ResultGrid`'s string
  `Error` parameter was bound as a literal (`Error="error"`) instead of the field
  (`Error="@error"`), so it was never null. Now successful queries render the grid and real
  errors show their message.

## [1.6.1] - 2026-05-29

### Fixed
- Web console pages are now **interactive** — the `--empty` Blazor template didn't apply a
  render mode, so buttons/dropdowns did nothing (Explore appeared broken). Applied global
  `InteractiveServer` render mode. Also cleaned up nullability warnings in the Explore page.

## [1.6.0] - 2026-05-29

### Added
- **Web console** — `daxter web` serves a local Blazor UI
  (`docker run -p 8080:8080 --env-file daxter.env daxter:latest web` → http://localhost:8080):
  - **Status / health** — config summary, sign-in/token + connectivity checks, with a
    device-code **Sign in** button.
  - **Explore** — workspaces → datasets → tables / measures, a DAX query box → results grid,
    refresh history.
  - **Configure** — view the current config and generate an env file + the Claude Desktop MCP
    entry to copy.
  Reuses `Daxter.Core` (same logic as CLI/MCP); the runtime image moved to `aspnet:8.0` to
  host it.

## [1.5.1] - 2026-05-29

### Fixed
- MCP tools now surface user-actionable messages (e.g. *"Not signed in — use the daxter_login
  tool"*) instead of the SDK's generic *"An error occurred invoking …"* — `DaxterException`
  text was being hidden, which would have stalled the device-code onboarding. `daxter_login`
  under a service principal returns a clear "no interactive sign-in needed" message.

## [1.5.0] - 2026-05-29

### Added
- **Sign-in-as-yourself onboarding (MCP).** New **`daxter_login`** tool returns the
  device-code URL + code as the tool result (shown in chat) and finishes auth in the
  background — no terminal needed. Normal tools no longer block on an interactive prompt:
  if not signed in they return a clear "use the daxter_login tool" message.
- Tenant-level tools (`daxter_workspaces`, `daxter_gateways`, `daxter_pipelines`,
  `daxter_pipeline_stages`/`_operations`) now run with **no workspace configured**, so you
  can sign in and then pick a workspace (`DaxterConfig.FromEnvironment(requireWorkspace: false)`).
- **26 MCP tools**. `SETUP.md` now defaults to device-code (your own login); service
  principal is documented as the automation alternative.

## [1.4.0] - 2026-05-29

### Added
- **Datasource connection details** — `ws datasources` / `daxter_datasources` now return
  `server`, `database`, `path`, `url` (e.g. the Snowflake account + warehouse), not just the
  gateway id.
- **`DAXTER_PROD_WORKSPACES`** — comma-separated workspace names treated as production by
  the write guard, for tenants whose prod workspaces are unsuffixed (e.g. `Sales Analytics`).
  Prod detection is now centralized in `DaxterConfig.IsProductionTarget()` (env=prod, name
  contains "prod", or listed) and shared by the CLI and MCP write paths.

## [1.3.0] - 2026-05-29

### Added
- **MCP ↔ CLI parity** — 8 more MCP tools so the server exposes the full client surface:
  `daxter_export`, `daxter_permissions`, `daxter_gateways`, `daxter_datasources`,
  `daxter_test_rls`, `daxter_pipelines`, `daxter_pipeline_stages`,
  `daxter_pipeline_operations`. 25 tools total (23 read + 2 gated write).

## [1.2.0] - 2026-05-29

### Added
- **Gated MCP write tools** — `daxter_refresh` (model/table/partitions) and
  `daxter_clear_cache`, **dry-run by default**. They execute only when `execute=true`
  **and** the server sets `DAXTER_MCP_ALLOW_WRITES=true`; PROD-looking targets are always
  refused. Safe for autonomous use by default.
- MCP tool-registration tests (reflection over `DaxterTools`).

## [1.1.0] - 2026-05-29

### Added
- **MCP server** — the image doubles as a Model Context Protocol server (`daxter mcp`,
  stdio). 15 read-only tools (`daxter_query`, `daxter_dmv`, `daxter_list_tables`,
  `daxter_measures`, `daxter_measure`, `daxter_mcode`, `daxter_parameters`,
  `daxter_partitions`, `daxter_rls`, `daxter_diff_measures`, `daxter_refresh_history`,
  `daxter_workspaces`, `daxter_datasets`, `daxter_reports`, `daxter_lineage`) with
  optional `workspace`/`dataset` args, JSON output capped to 1,000 rows. Reuses 100% of
  `Daxter.Core`. Logging routed to stderr to keep stdout a clean JSON-RPC channel.

## [1.0.0] - 2026-05-29

First public release — a cross-platform Power BI Service CLI covering query, model
metadata, maintenance, inventory, RLS testing, and deployment pipelines.

### Added
- **Query module** — `query` (DAX/MDX), `dmv` ($SYSTEM), `ls` (tables); output as
  table, CSV, or JSON; query text inline or from a file.
- **Model module** — `model measures` / `measure` / `mcode` / `parameters` /
  `partitions` (with last-refresh times) / `rls` (roles, filters, members) /
  `export` (.bim via TOM) / `diff` (measure differences between two models).
- **Ops module** — `refresh model` / `table` / `partitions` (newest-first, TMSL),
  `refresh trigger` (REST), `refresh history` (REST), `cache clear` (XMLA). Mutating
  ops require `--yes`, support `--dry-run`, and refuse PROD-looking targets without
  `--force`.
- **Workspace module** (REST) — `ws ls` (workspaces + group ids), `ws datasets`,
  `ws reports`, `ws lineage` (report → dataset), `ws permissions` (workspace or
  `--dataset`), `ws gateways`, `ws datasources`.
- **Test module** — `test-rls --role <r> --user <upn> [-q DAX]` runs a query under an
  RLS role / impersonated identity (XMLA `Roles` + `EffectiveUserName`). Requires the
  connecting identity to be a workspace/model admin.
- **Pipeline module** (REST) — `pipeline ls` / `stages` (env → workspace mapping) /
  `operations` (deployment history).
- **Foundations** — environment profiles (`--env` / `DAXTER_ENV` →
  `DAXTER_WORKSPACE_<ENV>`), `env ls`; service-principal and device-code auth via MSAL
  with a persisted token cache.
- **Distribution** — multi-stage Docker image (tests gate the build), non-root runtime,
  `bin/daxter` wrapper, `Makefile` (image/test/save/load).
- **Docs** — README, architecture, product plan, roadmap.

### Security
- OAuth token injection (no passwords in connection strings); secrets via `.env`
  (gitignored), never in the image; non-root container.

## Planned (post-1.0)

- TMDL folder export (in addition to `.bim`).
- Deployment-pipeline parameter/datasource *rules* detail (beyond stages).
- Tenant-wide inventory & audit (Scanner/Admin API) — needs a Fabric admin identity.
- Model editing (create/alter/delete measures, tables, roles) — write, opt-in.
