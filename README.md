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

The easiest way to use DAXter is as an **MCP server** — then you just **ask Claude** about
your Power BI in plain language.

**You need:** Docker Desktop running, and a Power BI account (you sign in **as yourself** —
no service principal required to start).

**Set it up — let Claude do it.** Point **Claude Code** (which can run commands; a plain
Claude Desktop *chat* can't execute the steps) at the setup guide:

> *"Set up DAXter for Claude Desktop by following https://github.com/Danlugo/daxter/blob/main/SETUP.md"*

It auto-pulls the public image and merges the `daxter` server into your Claude Desktop config
(no build, no credentials to obtain). Then **fully quit & reopen Claude Desktop** and, in a chat:

1. **"Sign in to Power BI"** → Claude shows a URL + code; sign in as yourself.
2. **"List my workspaces"** → pick one as your default.
3. Ask away — *"What measures are in the Sales model? Run `EVALUATE TOPN(10, Sales)`. When was it last refreshed? Who has access?"*

Full walkthrough (Windows notes, multi-client, service-principal automation) is in
**[`SETUP.md`](SETUP.md)**; a prompt per tool in [`examples/mcp.md`](examples/mcp.md).

**What it does today**

| Module | Commands |
|--------|----------|
| **Query** | `query` (DAX/MDX), `dmv`, `ls` — table / CSV / JSON |
| **Model** | `model measures` · `measure` · `mcode` · `parameters` · `partitions` · `rls` · `export` (.bim) · `diff` |
| **Ops** | `refresh model/table/partitions` · `refresh trigger` · `refresh history` · `cache clear` (with `--dry-run`/`--yes`/`--force`) |
| **Workspace** | `ws ls/datasets/reports/lineage/permissions/gateways/datasources` (REST) |
| **Test** | `test-rls --role/--user` (XMLA impersonation) |
| **Pipeline** | `pipeline ls/stages/operations` + `pipeline rules --pipeline X --model Y` (deployment rules — inferred from per-stage parameter differences) + `pipeline audit --pipeline X` (list models without rules; or `--mode check --stage X --param Y --value Z [--model M]` to find matching models) + saved rule sets (`--saved "name"`, `--list-saved`, `--run-all-saved`) |
| **Foundations** | environment profiles (`--env`), device-code + service-principal auth |

See [`docs/PRODUCT.md`](docs/PRODUCT.md) for the full product plan and
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md) for the design.

## Web console (easiest sign-in)

Prefer a UI? `daxter web` serves a local Blazor console — the simplest way to **sign in** and
pick your defaults, no terminal back-and-forth:

```bash
# --env-file takes an absolute path so Docker finds it from any working directory:
docker run -d -p 8080:8080 --env-file "$HOME/daxter.env" \
  -v daxter-tokens:/home/daxter/.daxter \
  ghcr.io/danlugo/daxter:latest web          # → http://localhost:8080
```

1. Open **http://localhost:8080** → **Status** → **Sign in** — opens the Microsoft device-login
   page (clickable link) with a one-click-copy code; the page updates itself once you finish.
2. **⚙ Configure** (gear icon, top-right) → set your default workspace/dataset and **Save**.
3. **Explore** → browse workspaces → datasets → tables; or **DAX Query** → write and run DAX.

The `-v …:/home/daxter/.daxter` volume holds your **sign-in token and Configure settings**, so
they persist across restarts and upgrades.

- **Status** — health checks, sign-in, and a **Version & updates** check.
- **Explore** — a drill-down browser (workspace → dataset → table → **M code** / partitions,
  measures, RLS, **Test RLS**, permissions, …). **Test RLS** runs a DAX query while impersonating
  a role and/or user (UPN) so you can see exactly what they'd see (requires admin on the model).
- **DAX Query** — a dedicated query editor with **field autocomplete + signature help**,
  **syntax highlighting**, **⌗ Format**, and live validation. "Run DAX" / "Top N rows" / a clicked
  Recent query from Explore open here pre-filled.
- **Gateways** — on-premises data gateways visible to the signed-in identity (REST).
- **Pipelines** — deployment pipelines with their stages (Dev → Test → Prod workspaces) and
  recent operations (REST).
- **Audit** — a per-model deployment-rule board plus pipeline-wide checks: pick a stage,
  parameter, and expected value; **➕ Add rule** to stack several into a rule set; **▶ Run all
  rules**; and **★ save** a check (shared with the CLI/MCP and persisted to the volume). Scope
  to every model in the pipeline or a single model.
- **⚙ Configure** (gear icon, top-right) — edit auth mode / defaults / prod workspaces and
  **Save** (persisted to the volume); also generates the env file + Claude Desktop MCP entry.
- **Logs** — recent activity (operations with row counts + timing, sign-in, errors); secrets redacted.

## Examples

- **CLI / client** → [`examples/cli.md`](examples/cli.md) — every command with a runnable example.
- **MCP** → [`examples/mcp.md`](examples/mcp.md) — example prompts for all 33 tools (query, metadata, columns, inventory, gateways, datasources, pipelines, deployment-rule audits, RLS testing, refresh, …).

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

The image ships with **no credentials** — pass them at runtime via an env file (the
variables are documented in [Configure](#configure) below) plus a volume for the cached
token:

```bash
docker run --rm --env-file .env -v daxter-tokens:/home/daxter/.daxter \
  ghcr.io/danlugo/daxter:latest query 'EVALUATE ROW("ok", 1)'
```

Prefer to **build it yourself**: `make image`. For a guided, end-to-end setup (including
Claude Desktop), see **[`SETUP.md`](SETUP.md)**; for `-e` flags, device-code login, and the
`bin/daxter` wrapper, see [`examples/cli.md`](examples/cli.md).

## Upgrade

Your sign-in token and settings live on the `daxter-tokens` volume — **not** in the image — so
upgrading never touches them. Pull the new image and recreate the container with the **same**
volume mount:

```bash
docker pull ghcr.io/danlugo/daxter:latest
docker rm -f daxter                          # your container name
docker run -d -p 8080:8080 --env-file "$HOME/daxter.env" \
  -v daxter-tokens:/home/daxter/.daxter \
  ghcr.io/danlugo/daxter:latest web
```

The web console's **Status** page shows the running version and checks GitHub for newer
releases. Or just ask Claude: *"upgrade my DAXter container and keep the volume."*

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

Results go to **stdout**, status to **stderr**; `-o` is `table` (default), `csv`, or `json`.
**Every command is in [`examples/cli.md`](examples/cli.md)**; MCP prompts in
[`examples/mcp.md`](examples/mcp.md).

## Authentication

**Device code (default, interactive).** `daxter login` prints a URL and code; open it in
any browser and sign in. The token is cached in the `daxter-tokens` volume, so subsequent
runs are silent until it expires. Requires an app registration with **public client flows
enabled** (set `DAXTER_CLIENT_ID`, or rely on the built-in default). Use this to run **as
yourself** for operations that need *your* user permissions rather than the SP's — e.g.
**gateway names** (only returned for gateways the caller administers). See the device-code
example in [`examples/cli.md`](examples/cli.md#authenticate).

**Service principal (automation).** Set `DAXTER_AUTH_MODE=service-principal` plus
`DAXTER_TENANT_ID`, `DAXTER_CLIENT_ID`, `DAXTER_CLIENT_SECRET`. The tenant admin must
enable *"Allow service principals to use Power BI APIs"* and the SP must be a member of
the workspace.

## Passing the image around

Easiest: **pull from GHCR** (see [Install](#install)) — CI publishes
`ghcr.io/danlugo/daxter:latest` and a tag per release (`:v1.7.0`, …) on every `v*` tag.

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

By default you **sign in as yourself**: ask Claude to "sign in to Power BI" and it calls
`daxter_login`, which returns a device-code URL you complete in the browser; then list
workspaces and pick one. (A service principal is the alternative for automation.)
[`SETUP.md`](SETUP.md) walks through it on a new machine. The MCP tools are at **full parity
with the CLI** — the query, metadata, ops, and inventory operations from the feature matrix
above, as **33 tools** (`daxter_login` + 30 read + 2 gated write). Each accepts optional
`workspace`/`dataset` arguments; results are JSON, capped to 1,000 rows.
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

Multi-stage build — the `sdk:8.0` stage restores + **tests** + publishes; the slim
`runtime:8.0` stage ships only the app as a non-root user. Full design in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md).

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

## Contributing & development

Docker-only — no local .NET SDK required. Build/test commands, the repo layout, and
conventions live in **[`CLAUDE.md`](CLAUDE.md)**; design in
[`docs/ARCHITECTURE.md`](docs/ARCHITECTURE.md); roadmap in
[`docs/ROADMAP.md`](docs/ROADMAP.md).
