# Changelog

All notable changes to DAXter are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
  newest→oldest / oldest→newest, or a **single** partition) — and **confirm** before it runs.
  A **"Refreshing…"** banner shows while a refresh is in progress for the dataset, and a
  per-dataset job tracker (plus the **Jobs** page) lets you **cancel** and **see details**
  (status, duration, target, error). Refreshes deep-link from Explore (a dataset or table →
  **↻ Refresh…**).
- **Refreshes run as background jobs.** A single-worker `JobService` runs them **one at a time
  in launch order** (so partitions process in sequence), reusing Core's `MaintenanceService`
  (TMSL). New **Jobs** page with a nav active-count badge. Gated exactly like the MCP/CLI:
  only with **Allow writes** on, and **always refused for PROD** targets. **Cancellation**
  skips queued jobs and aborts a running refresh (closes its connection).
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
- **"Frequent" sidebar.** A left rail across the console lists your **most-used workspaces,
  datasets, and tables** (by open count) so you don't have to search for the same ones every
  time. Click one to jump straight into Explore at that level (deep-linked via the query
  string — a table even opens to its M code). Usage is tracked in a persisted `UsageStore`
  (`~/.daxter/usage.json` on the mounted volume) and only records targets that actually
  resolved, so typos don't pollute the list.
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
  row count and timing (e.g. `datasets [Data Hub] → 94 rows in 887 ms`), sign-in, health checks,
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
