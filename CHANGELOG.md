# Changelog

All notable changes to DAXter are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

Targeting **v1.0.0** (first public release). Implemented so far:

### Added
- **Query module** — `query` (DAX/MDX), `dmv` ($SYSTEM), `ls` (tables); output as
  table, CSV, or JSON; query text inline or from a file.
- **Model module** — `model measures` / `measure` / `mcode` / `parameters` /
  `partitions` (with last-refresh times) / `rls` (roles, filters, members).
- **Ops module** — `refresh model` / `table` / `partitions` (newest-first, TMSL),
  `refresh trigger` (REST), `refresh history` (REST), `cache clear` (XMLA). Mutating
  ops require `--yes`, support `--dry-run`, and refuse PROD-looking targets without
  `--force`.
- **Foundations** — environment profiles (`--env` / `DAXTER_ENV` →
  `DAXTER_WORKSPACE_<ENV>`), `env ls`; service-principal and device-code auth via MSAL
  with a persisted token cache.
- **Distribution** — multi-stage Docker image (tests gate the build), non-root runtime,
  `bin/daxter` wrapper, `Makefile` (image/test/save/load).
- **Docs** — README, architecture, product plan, roadmap.

### Security
- OAuth token injection (no passwords in connection strings); secrets via `.env`
  (gitignored), never in the image; non-root container.

## Planned

- **v1.1 — Ops:** `refresh` (model/table/partitions newest-first), `cache clear`,
  refresh history (with `--dry-run` / `--yes`).
- **v1.2 — Workspace:** REST inventory, lineage, permissions, gateways.
- **v1.3 — Export & diff:** model `.bim`/TMDL export, model diff (TOM).
- **Later:** test-RLS, deployment-pipeline rules, tenant audit, model editing.
