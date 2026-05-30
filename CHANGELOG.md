# Changelog

All notable changes to DAXter are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
