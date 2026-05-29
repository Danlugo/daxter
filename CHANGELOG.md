# Changelog

All notable changes to DAXter are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

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
