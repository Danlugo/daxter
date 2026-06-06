# DAXter

[![CI](https://github.com/Danlugo/daxter/actions/workflows/ci.yml/badge.svg)](https://github.com/Danlugo/daxter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8.0%20LTS-512BD4)
![Docker](https://img.shields.io/badge/Docker-multi--stage-2496ED)

**Ask Claude about your Power BI models — or query them from your terminal.** DAXter is a
Docker-only **CLI and MCP server** for the Power BI Service: run DAX/MDX/DMV, inspect model
metadata, refresh, and browse workspaces/reports — over XMLA + REST, from any machine with
Docker (macOS, Linux, Windows). No Windows-only tooling, no .NET install. Think *psql for a
Power BI model*, in a single container image.

## Use it in Claude Desktop

Run DAXter as an **MCP server**, then just **ask Claude** about your Power BI in plain language.
**You need:** Docker Desktop running and a Power BI account (you sign in **as yourself** — no
service principal required to start).

### 1. One-click extension (easiest)

1. Download **[`daxter.mcpb`](https://github.com/Danlugo/daxter/releases/latest/download/daxter.mcpb)** — this link always serves the latest.
2. In Claude Desktop open **Settings → Extensions → Advanced settings → Install Extension…**, pick `daxter.mcpb` (or **drag the file onto the Extensions page**), and **Install**. *(On macOS double-clicking the file also opens the installer; on Windows use this Settings flow — double-click may not be wired up.)*
3. Ask Claude **"sign me in to Power BI"** → it returns a device-code link; sign in as yourself. Then ask away — *"What measures are in the Sales model? Run `EVALUATE TOPN(10, Sales)`. When was it last refreshed? Who has access?"*

The extension just **launches the same Docker image** — the .NET/XMLA engine runs in the
container, no JSON and no env file. To set a default workspace or enable writes, use the **web
console** (below) once; otherwise name the workspace in your request.

### 2. Let Claude Code set it up (manual config)

Point **Claude Code** (which can run commands; a plain Claude Desktop *chat* can't) at the guide —
it pulls the image and merges the `daxter` server into your config:

> *"Using **Claude Code**, set up the DAXter MCP server for my Claude Desktop by following https://github.com/Danlugo/daxter/blob/main/SETUP.md — **fetch the raw file (`raw.githubusercontent.com/Danlugo/daxter/main/SETUP.md`) so you get the exact commands, not a summary**, then run all the Docker/config commands and **start the web console**. I'll do the browser sign-in at localhost and the final Claude Desktop restart."*

Then **fully quit & reopen Claude Desktop** and sign in as above. Full walkthrough (Windows notes,
multi-client, service principal) → **[`SETUP.md`](SETUP.md)**; a prompt per tool →
[`examples/mcp.md`](examples/mcp.md).

## What it does

| Module | Commands |
|--------|----------|
| **Query** | `query` (DAX/MDX), `dmv`, `ls` — table / CSV / JSON |
| **Model** | `model measures` · `measure` · `mcode` · `parameters` · `partitions` · `rls` · `export` (.bim) · `diff` |
| **Edit** ⚠ | `model edit measure/parameter/role/column/edit-column/source/calc-table/import-table/relationship` (+ `delete-*`, raw `tmsl`) — **dry-run by default**, gated, `.bim` backup before apply. `edit-column` changes an existing column's **format / data type / sort-by / summarize-by / folder / hidden** (via TOM — raw TMSL can't edit a standalone column). Available in **CLI · MCP · web (Model Edit page)** — one shared engine |
| **Ops** | `refresh model/table/partitions` (queued through the shared worker) · `refresh status` · `refresh trigger` · `refresh history` · `cache clear` (with `--dry-run`/`--yes`/`--force`) |
| **Workspace** | `ws ls/datasets/reports/lineage/report-inventory/export-report/permissions/gateways/connections/datasources` (REST). `report-inventory` classifies reports **thin/thick + downloadable**; `export-report` pulls a report's **definition** (PBIR/legacy field references) and optionally its `.pbix` |
| **Connections** ⚠ | See a model's **current "maps to" mapping** (`ws item-connections`), list **shareable cloud connections** (`ws connections`), **take over** + **bind** each source to any connection — cloud or gateway — via the Fabric `bindConnection` API: `ws bind-connection` (per-source, incl. cloud "Maps to") and `ws bind-gateway` (whole-model). **Dry-run by default**, gated, needs ownership. In **CLI · MCP (`daxter_item_connections`/`daxter_connections`/`daxter_take_over`/`daxter_bind_connection`/`daxter_bind_to_gateway`) · web (Connections page — both gateway and cloud sections writable)** |
| **Test** | `test-rls --role/--user` (XMLA impersonation) |
| **Pipeline** | `pipeline ls/stages/operations` · `pipeline rules` (deployment rules, inferred from per-stage parameter differences) · `pipeline audit` (models without rules, or `--mode check` to find matching models) · saved rule sets |
| **Foundations** | environment profiles (`--env`), device-code + service-principal auth |

The MCP server exposes these at **full parity** as **49 tools** (`daxter_login` + 31 read + 17 gated
write/edit). Refreshes from any interface (CLI, MCP, UI) are **queued through one shared worker** that
runs them **one per model at a time** — see [Refresh scheduler](#refresh-scheduler). See
[`docs/PRODUCT.md`](docs/PRODUCT.md) for the product plan,
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the design.

## Refresh scheduler

DAXter is one integrated system — the CLI, MCP server and web console are thin interfaces over a single
engine. **All refreshes route through one shared queue** (file-backed on the `daxter-tokens` volume),
drained by a single **worker hosted by the web container**. The worker runs **one refresh per model at a
time** (different models in parallel), honours partition order + `--retries`, and records job status.

- `daxter refresh model|table|partitions … --yes` (CLI) and `daxter_refresh` (MCP) **enqueue** a job and
  return a **job id** — they don't execute inline. Two refreshes can never collide on the same model.
- Track jobs with `daxter refresh status` (CLI), `daxter_refresh_jobs` (MCP), or the web **Jobs** page —
  all read the same queue, so a CLI- or MCP-launched refresh shows up everywhere.
- **Run the web container** (below) so its worker drains the queue — it does so whether or not you open
  the UI. Without a worker, jobs simply wait (a heartbeat warning tells you none is running).

## Web console

`daxter web` serves a local Blazor console — the simplest way to **sign in**, set defaults, browse
models, run DAX (with autocomplete + formatting), **edit the model** (parameters/roles/relationships/
tables, on the gated **Model Edit** page), audit deployment rules, and read logs:

```bash
docker run -d -p 8080:8080 --name daxter-web \
  -v daxter-tokens:/home/daxter/.daxter \
  ghcr.io/danlugo/daxter:latest web          # → http://localhost:8080
```

Open it → **Status → Sign in** (device-code) → **⚙ Configure** to set defaults / *Allow writes*. The
`daxter-tokens` volume holds your **token and settings** — the single config source the CLI and MCP
server read too. This container also **hosts the refresh worker** that drains the shared queue (see
[Refresh scheduler](#refresh-scheduler)), so keep it running to execute refreshes launched from any
interface. Full page-by-page tour in [`docs/PRODUCT.md`](docs/PRODUCT.md).

## CLI

Pull the prebuilt **multi-arch** image (`linux/amd64` + `linux/arm64`, native on Apple Silicon and
x86 — published to GHCR on every release) and run:

```bash
docker pull ghcr.io/danlugo/daxter:latest
./bin/daxter query 'EVALUATE TOPN(10, Sales)'     # DAX → table
./bin/daxter model measures --with-expr -o csv    # metadata → CSV
./bin/daxter ws gateways                          # REST inventory
```

`bin/daxter` runs the image with the token volume, mounts the current dir at `/work`, loads `./.env`,
and attaches a TTY when interactive. Output is `-o table` (default), `csv`, or `json`; results to
**stdout**, status to **stderr**. Configure via the web console or env vars
(`DAXTER_WORKSPACE`, `DAXTER_DATASET`, auth — see [`SETUP.md`](SETUP.md#advanced-service-principal--headless)).
Build from source with `make image`; ship offline with `make save` / `make load`. Every command has a
runnable example in **[`examples/cli.md`](examples/cli.md)**.

> XMLA addresses use **names**, not the GUIDs in portal URLs. List model names with
> `./bin/daxter dmv 'SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS'`, then set
> `DAXTER_WORKSPACE` / `--dataset` accordingly.

## MCP server

The same image is a **Model Context Protocol** server (stdio) via the `mcp` subcommand — add it with
the [extension](#1-one-click-extension-easiest) or the config block in [`SETUP.md`](SETUP.md). Call
**`daxter_capabilities`** to discover every tool/feature in one shot (auto-generated from the registered
tools, so it's always current). Tools
accept optional `workspace`/`dataset` (name **or** id); results are JSON, capped to 1,000 rows. The
write tools (`daxter_refresh`, `daxter_clear_cache`) are **dry-run by default** and only act when
`execute=true` **and** writes are enabled — via the web console (*Allow writes*) or
`DAXTER_MCP_ALLOW_WRITES=true`. `daxter_refresh` **queues** the job (returns a job id) for the shared
worker to run — track it with `daxter_refresh_jobs`, **resume** an interrupted/failed one with
`daxter_resume_refresh` (re-runs only the **not-yet-done partitions** by default). Refreshes execute via
the **server-managed Enhanced Refresh API** (no long-lived client connection → can't hang/drop;
`maxParallelism` via `DAXTER_REFRESH_MAX_PARALLELISM`, default 4; `DAXTER_REFRESH_ENGINE=xmla` forces the
legacy client path for non-Premium models). The single worker
drains the queue **up to 4 models concurrently** (per-model serialized; queue depth is unbounded) —
raise/lower it with `DAXTER_REFRESH_MAX_CONCURRENT_MODELS` (clamped to 1–16; higher consumes more
capacity + XMLA sessions).
Once on, **PROD is allowed by default**; set
`DAXTER_MCP_BLOCK_PROD_WRITES=true` to re-block it. The **model-edit** tools (`daxter_edit_*`,
`daxter_delete_*`, `daxter_set_*`, `daxter_create_*`, raw `daxter_edit_tmsl`) sit behind a **separate,
stricter gate** — `DAXTER_MCP_ALLOW_MODEL_EDIT=true` or the web console *Allow model edits* — and take
a `.bim` backup before applying (XMLA edits are irreversible for PBIX download). Prompts per tool →
[`examples/mcp.md`](examples/mcp.md).

## Authentication

- **Device code (default, interactive)** — sign in as yourself; the token caches in the
  `daxter-tokens` volume, so later runs are silent. Best for operations that need *your* permissions
  (e.g. gateway names). Requires an app registration with public client flows (or the built-in default).
- **Service principal (automation)** — set `DAXTER_AUTH_MODE=service-principal` +
  `DAXTER_TENANT_ID`/`DAXTER_CLIENT_ID`/`DAXTER_CLIENT_SECRET`. The tenant admin must enable *"Allow
  service principals to use Power BI APIs"* and the SP must be a workspace member.

See the [`examples/cli.md`](examples/cli.md#authenticate) auth walkthrough.

## Requirements

- Docker (Desktop or Engine).
- A workspace on **Premium / PPU / Fabric** with the **XMLA endpoint enabled** (Admin portal →
  Capacity settings → XMLA Endpoint = Read or Read/Write).
- An Entra ID identity with access to the workspace.

## How it works

| Layer | Choice |
|-------|--------|
| Runtime | .NET 8 (LTS), Linux container |
| XMLA client | `Microsoft.AnalysisServices.AdomdClient` 19.114 (managed, cross-platform) |
| Auth | `Microsoft.Identity.Client` (MSAL) → injected via `AccessToken` |
| CLI | `System.CommandLine` 2.0 |
| Output | Spectre.Console (table), CSV (RFC 4180), JSON |

Multi-stage build — the `sdk:8.0` stage restores + **tests** + publishes; the slim `runtime:8.0`
stage ships only the app as a non-root user. Full design in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

**Image security.** A scanner (e.g. `docker scout`) will flag CVEs on the image — these are
**inherited from the .NET runtime's Debian base** (`perl`, `gnutls28`, …), **not** DAXter's code, and
sit in paths a .NET app never executes. They clear when the base image is refreshed; a future
[chiseled base](https://learn.microsoft.com/dotnet/core/docker/container-images) would remove them.
DAXter's own layers add no critical/high vulnerabilities.

## Limitations

- **Model editing** (measures, parameters, RLS/OLS roles, calculated columns, **column properties**
  — format / data type / sort-by / summarize-by / folder / hidden — partition M sources,
  calculated tables, + raw TMSL) is supported — **dry-run by default**, behind a dedicated gate, with
  a `.bim` backup before every apply. ⚠ Editing a Power BI Desktop–authored model over XMLA is
  **irreversible for PBIX download** (keep your original .pbix) and requires the workspace XMLA
  endpoint set to **Read/Write**.
- The workspace must expose an XMLA endpoint (Premium/PPU/Fabric). Pro datasets don't (REST commands
  like `ws` / `refresh history` still work on Pro).
- TMSL refresh / `cache clear` need XMLA set to **Read/Write**; `refresh trigger` (REST) works with
  read-only XMLA.
- `test-rls` impersonation requires the connecting identity to be a workspace/model admin.
- Tenant-wide audit (Scanner/Admin API) requires a Fabric admin identity — not included.

## Contributing & development

Docker-only — no local .NET SDK required. Build/test commands, repo layout, and conventions live in
**[`CLAUDE.md`](CLAUDE.md)**; design in [`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md); roadmap in
[`docs/ROADMAP.md`](docs/ROADMAP.md).
