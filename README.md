# DAXter

[![CI](https://github.com/Danlugo/daxter/actions/workflows/ci.yml/badge.svg)](https://github.com/Danlugo/daxter/actions/workflows/ci.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
![.NET 8](https://img.shields.io/badge/.NET-8.0%20LTS-512BD4)
![Docker](https://img.shields.io/badge/Docker-multi--stage-2496ED)

A portable, **Docker-only** CLI for the **Power BI Service**. Query, document, and
operate semantic models on a Premium / PPU / Fabric XMLA endpoint from macOS, Linux, or
CI — no Windows, no local .NET install, no Analysis Services tooling. Think *psql for a
Power BI model*, shipped as a single container image you can hand to anyone.

**Why it exists:** Power BI's pro tooling (Tabular Editor, DAX Studio, SSMS) is
Windows-only, and ADOMD.NET's interactive sign-in is too. DAXter acquires an Entra ID
token via MSAL and injects it into the XMLA connection — the supported cross-platform
path — so Mac/Linux users and pipelines get programmatic model access.

**What it does today**

| Module | Commands |
|--------|----------|
| **Query** | `query` (DAX/MDX), `dmv`, `ls` — table / CSV / JSON |
| **Model** | `model measures` · `measure` · `mcode` · `parameters` · `partitions` · `rls` · `export` (.bim) · `diff` |
| **Ops** | `refresh model/table/partitions` · `refresh trigger` · `refresh history` · `cache clear` (with `--dry-run`/`--yes`/`--force`) |
| **Workspace** | `ws ls/datasets/reports/lineage/permissions/gateways/datasources` (REST) |
| **Test** | `test-rls --role/--user` (XMLA impersonation) |
| **Pipeline** | `pipeline ls/stages/operations` (deployment pipelines) |
| **Foundations** | environment profiles (`--env`), device-code + service-principal auth |

See [`docs/PRODUCT.md`](docs/PRODUCT.md) for the full product plan and
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the design.

## Examples

- **CLI / client** → [`examples/cli.md`](examples/cli.md) — every command with a runnable example.
- **MCP** → [`examples/mcp.md`](examples/mcp.md) — example prompts for all 25 tools (query, metadata, inventory, gateways, datasources, pipelines, RLS testing, refresh, …).

## Why a container

ADOMD.NET's built-in interactive login and TCP connectivity are Windows-only. DAXter
sidesteps that by acquiring an Entra ID token itself (MSAL) and injecting it into the
connection via `AccessToken` — the supported cross-platform path. Packaging it as an
image means the only dependency on any machine is Docker.

## Requirements

- Docker (Desktop or Engine).
- A Power BI workspace on **Premium / PPU / Fabric** capacity with the **XMLA endpoint
  enabled** (Admin portal → Capacity settings → XMLA Endpoint = Read or Read/Write).
- An Entra ID identity with access to the workspace (see [Authentication](#authentication)).

## Install

**Pull the prebuilt image** (published to GHCR by CI on every release — no build needed):

```bash
docker pull ghcr.io/danlugo/daxter:latest
docker run --rm ghcr.io/danlugo/daxter:latest --help
```

Real commands need config. The image ships with **no credentials** — pass them at
runtime via an env file or `-e` flags, plus a volume to persist the cached token:

```bash
# 1) put your settings in a file (keys are listed in .env.example)
cat > daxter.env <<'EOF'
DAXTER_AUTH_MODE=service-principal
DAXTER_TENANT_ID=<tenant-guid>
DAXTER_CLIENT_ID=<app-id>
DAXTER_CLIENT_SECRET=<secret>
DAXTER_WORKSPACE=My Workspace
DAXTER_DATASET=My Model
EOF

# 2) run, passing the file + a token-cache volume
docker run --rm --env-file daxter.env -v daxter-tokens:/home/daxter/.daxter \
  ghcr.io/danlugo/daxter:latest query 'EVALUATE ROW("ok", 1)'

# inline -e works too (no file):
docker run --rm \
  -e DAXTER_WORKSPACE="My Workspace" -e DAXTER_DATASET="My Model" \
  -e DAXTER_AUTH_MODE=service-principal \
  -e DAXTER_TENANT_ID=... -e DAXTER_CLIENT_ID=... -e DAXTER_CLIENT_SECRET=... \
  ghcr.io/danlugo/daxter:latest ls

# interactive device-code sign-in (needs -it; token persists in the volume):
docker run --rm -it -v daxter-tokens:/home/daxter/.daxter \
  -e DAXTER_WORKSPACE="My Workspace" ghcr.io/danlugo/daxter:latest login
```

The `bin/daxter` wrapper bundles these flags for you — point `DAXTER_IMAGE` at the registry:

```bash
DAXTER_IMAGE=ghcr.io/danlugo/daxter:latest ./bin/daxter ls
```

**Or build it yourself:**

```bash
make image          # builds daxter:latest, running the test suite inside the build
```

The build fails if any unit test fails, so a successful image is always a tested image.

## Configure

Copy the template and fill it in:

```bash
cp .env.example .env
```

| Variable | Required | Description |
|----------|----------|-------------|
| `DAXTER_WORKSPACE` | yes | Workspace name (or full `powerbi://…` data source) |
| `DAXTER_DATASET` | no | Semantic model name (`Initial Catalog`) |
| `DAXTER_AUTH_MODE` | no | `device-code` (default) or `service-principal` |
| `DAXTER_TENANT_ID` | SP / optional | Entra tenant id or domain |
| `DAXTER_CLIENT_ID` | SP / optional | App registration (client) id |
| `DAXTER_CLIENT_SECRET` | SP only | Client secret — keep it only in `.env` |

## Usage

The `bin/daxter` wrapper runs the image with the token cache volume, mounts the current
directory at `/work` (so `-f file.dax` works), loads `./.env`, and attaches a TTY when
interactive:

```bash
./bin/daxter query 'EVALUATE TOPN(10, Sales)'     # DAX → table
./bin/daxter model measures --with-expr -o csv    # metadata → CSV
./bin/daxter ws gateways                          # inventory via REST
```

Or without the wrapper:

```bash
docker run --rm --env-file .env -v daxter-tokens:/home/daxter/.daxter \
  daxter:latest query 'EVALUATE ROW("ok", 1)'
```

Results go to **stdout**, status to **stderr**; `-o` is `table` (default), `csv`, or `json`.
**See [`examples/cli.md`](examples/cli.md) for every command** (query, model, refresh,
workspace inventory, RLS testing, pipelines), and [`examples/mcp.md`](examples/mcp.md) for
the MCP tools.

## Authentication

**Device code (default, interactive).** `daxter login` prints a URL and code; open it in
any browser and sign in. The token is cached in the `daxter-tokens` volume, so subsequent
runs are silent until it expires. Requires an app registration with **public client flows
enabled** (set `DAXTER_CLIENT_ID`, or rely on the built-in default).

**Service principal (automation).** Set `DAXTER_AUTH_MODE=service-principal` plus
`DAXTER_TENANT_ID`, `DAXTER_CLIENT_ID`, `DAXTER_CLIENT_SECRET`. The tenant admin must
enable *"Allow service principals to use Power BI APIs"* and the SP must be a member of
the workspace.

## Passing the image around

Easiest: **pull from GHCR** (see [Install](#install)) — CI publishes
`ghcr.io/danlugo/daxter:latest` and a tag per release (`:v1.2.0`, …) on every `v*` tag.

Offline / air-gapped: ship a tarball:

```bash
make save           # → daxter-image.tar.gz
# on another machine:
make load           # docker load < daxter-image.tar.gz
```

## MCP server

The same image doubles as a **Model Context Protocol** server (stdio) via the `mcp`
subcommand — so Claude Desktop, Cursor, or Claude Code can query and inspect your
models directly. Add to `claude_desktop_config.json`:

```jsonc
"mcpServers": {
  "daxter": {
    "command": "/usr/local/bin/docker",
    "args": ["run", "-i", "--rm",
             "--env-file", "/path/to/Daxter/.env",
             "-v", "daxter-tokens:/home/daxter/.daxter",
             "daxter:latest", "mcp"]
  }
}
```

Requires Docker running and a service-principal `.env` (interactive auth isn't viable in
a headless server). The MCP tools are at **full parity with the CLI** — 23 read tools:

- **Query/metadata:** `daxter_query`, `daxter_dmv`, `daxter_list_tables`, `daxter_measures`,
  `daxter_measure`, `daxter_mcode`, `daxter_parameters`, `daxter_partitions`, `daxter_rls`,
  `daxter_diff_measures`, `daxter_export`, `daxter_test_rls`
- **Ops:** `daxter_refresh_history`
- **Inventory:** `daxter_workspaces`, `daxter_datasets`, `daxter_reports`, `daxter_lineage`,
  `daxter_permissions`, `daxter_gateways`, `daxter_datasources`, `daxter_pipelines`,
  `daxter_pipeline_stages`, `daxter_pipeline_operations`

Each accepts optional `workspace`/`dataset` arguments; results are JSON, capped to 1,000 rows.
**See [`examples/mcp.md`](examples/mcp.md) for an example prompt per tool** (including "list
the gateways", datasources, pipelines, RLS testing).

Plus **gated** write tools (`daxter_refresh`, `daxter_clear_cache`) that are **dry-run by
default** — they only execute when `execute=true` **and** the server sets
`DAXTER_MCP_ALLOW_WRITES=true`, and PROD-looking targets are always blocked. So the server
is safe for autonomous use out of the box. "Production" = the active env is `prod`, the
workspace name contains "prod", **or** the workspace is listed in `DAXTER_PROD_WORKSPACES`
(use this when prod workspaces are unsuffixed, e.g. `Sales Analytics`).

## How it works

| Layer | Choice |
|-------|--------|
| Runtime | .NET 8 (LTS), Linux container |
| XMLA client | `Microsoft.AnalysisServices.AdomdClient` 19.114 (managed, cross-platform) |
| Auth | `Microsoft.Identity.Client` (MSAL) → injected via `AccessToken` |
| CLI | `System.CommandLine` 2.0 |
| Output | Spectre.Console (table), CSV (RFC 4180), JSON |

The image is multi-stage: a `sdk:8.0` stage restores, **tests**, and publishes; the
`runtime:8.0` stage ships only the app and runs as a non-root user.

## Finding workspace & model names

XMLA addresses use **names**, not the GUIDs you see in portal/OneLake URLs
(`…/groups/<guid>/…` or `…/dataset/<guid>/…` will not resolve). To discover names:

```bash
# Connect at the workspace level (no dataset) and list models:
./bin/daxter dmv 'SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS'
```

Set `DAXTER_WORKSPACE` to the workspace name (e.g. `Sales Analytics`) and
`DAXTER_DATASET` (or `--dataset`) to a model name from that list.

## Limitations

- Query, metadata, maintenance (refresh/cache), and inventory are supported. **Model
  editing** (create/alter/delete measures, tables, roles) is intentionally out of scope.
- The workspace must expose an XMLA endpoint (Premium/PPU/Fabric). Pro datasets do not
  (REST commands like `ws`/`refresh history` still work on Pro).
- TMSL refresh / `cache clear` need the XMLA endpoint set to **Read/Write**; `refresh
  trigger` (REST) works with read-only XMLA.
- `test-rls` impersonation requires the connecting identity to be a workspace/model admin.
- Tenant-wide audit (Scanner/Admin API) requires a Fabric admin identity — not included.
- Interactive `login` needs a TTY (`docker run -it`); the wrapper handles this.

## Project layout

```
src/Daxter.Core/    # auth, connection, query, formatting (the engine)
src/Daxter.Cli/     # System.CommandLine entry point
tests/              # xUnit unit tests (run inside the Docker build)
Dockerfile          # multi-stage build/test/publish
bin/daxter          # docker run wrapper
```

## Development

A local .NET SDK is **not** required — everything runs in Docker. If you have the SDK and
want a fast inner loop, `dotnet test` and `dotnet run --project src/Daxter.Cli` work too
(the projects roll forward to a newer runtime when net8 isn't installed).
