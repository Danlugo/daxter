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

## Demo — every feature

All commands accept connection options (`--workspace`, `--dataset`, `--env`, `--auth`,
`--tenant`, `--client-id`) or read them from `.env`. Results print to **stdout**; status
and prompts go to **stderr**, so output pipes cleanly.

### Authenticate

```console
# Interactive (device code) — token is cached, so you sign in once:
$ ./bin/daxter login
To sign in, open https://microsoft.com/devicelogin and enter code F7X2K9Q20.
Signed in. Token valid until 2026-05-29 23:10:00Z.

# Or non-interactive (service principal) via .env:
#   DAXTER_AUTH_MODE=service-principal
#   DAXTER_TENANT_ID / DAXTER_CLIENT_ID / DAXTER_CLIENT_SECRET
```

### Query — DAX / MDX (`query`)

```console
$ ./bin/daxter query "EVALUATE TOPN(3, Product)"          # table (default)
╭───────────┬─────────────╮
│ ProductId │ ProductName │
├───────────┼─────────────┤
│ 1         │ Widget      │
│ 2         │ Gadget      │
│ 3         │ Gizmo       │
╰───────────┴─────────────╯
(3 rows)

$ ./bin/daxter query "EVALUATE Product" -o csv > products.csv   # CSV to a file
$ ./bin/daxter query "EVALUATE Product" -o json                 # JSON
$ ./bin/daxter query -f monthly_sales.dax                       # query from a file
```

### DMV & schema (`dmv`, `ls`)

```console
$ ./bin/daxter ls                                  # tables in the model
$ ./bin/daxter dmv 'SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS'   # datasets in workspace
$ ./bin/daxter dmv 'SELECT * FROM $SYSTEM.DISCOVER_STORAGE_TABLES'          # VertiPaq storage
```

### Model metadata (`model …`)

```console
$ ./bin/daxter model measures                      # all measures (name, type, folder)
$ ./bin/daxter model measures --with-expr -o csv   # include the DAX expression
$ ./bin/daxter model measure "Total Sales"         # one measure, full definition
$ ./bin/daxter model mcode --table Sales           # Power Query (M) per partition
$ ./bin/daxter model parameters                    # shared M expressions / parameters
$ ./bin/daxter model partitions --table Sales      # partitions + last-refresh times
$ ./bin/daxter model rls                            # RLS roles
$ ./bin/daxter model rls --role "Regional Manager" # a role's table filters + members
```

```console
$ ./bin/daxter model partitions --table Sales
╭──────────┬───────┬──────┬─────────────────────╮
│ Name     │ State │ Mode │ RefreshedTime       │
├──────────┼───────┼──────┼─────────────────────┤
│ 2026Q1   │ 3     │ 0    │ 2026-05-29 17:57:59 │
│ 2026Q2   │ 1     │ 0    │ 2026-05-29 17:42:50 │
╰──────────┴───────┴──────┴─────────────────────╯
(2 rows)
```

### Environments (`env`, `--env`)

```console
$ ./bin/daxter env ls                              # configured profiles (* = active)
* dev
  qa
  prod
$ ./bin/daxter model measures --env qa             # same command, QA workspace
```

### Maintenance / Ops (`refresh`, `cache`)

```console
$ ./bin/daxter refresh history --top 5                # recent refreshes (REST)
$ ./bin/daxter refresh model --dry-run                # preview the TMSL, run nothing
$ ./bin/daxter refresh partitions --table Sales --order newest-first --yes
$ ./bin/daxter refresh trigger --yes                  # REST refresh (no XMLA write needed)
$ ./bin/daxter cache clear --yes                      # XMLA ClearCache
```

Mutating ops require `--yes`, support `--dry-run`, and refuse PROD-looking targets
without `--force`.

### Workspace inventory (`ws` — REST)

```console
$ ./bin/daxter ws ls                                  # workspaces + group ids
$ ./bin/daxter ws datasets                            # datasets in the workspace
$ ./bin/daxter ws reports                             # reports
$ ./bin/daxter ws lineage                             # report → dataset
$ ./bin/daxter ws permissions --dataset 'Sales'       # who has access (or workspace-wide)
$ ./bin/daxter ws datasources --dataset 'Sales'       # data sources
$ ./bin/daxter ws gateways                             # gateways
```

### Export, diff & RLS testing

```console
$ ./bin/daxter model export --out model.bim           # save-as .bim (TOM)
$ ./bin/daxter model diff 'Sales (Prod)'              # measure differences vs another model
$ ./bin/daxter test-rls --role 'Manager' --user u@x.com   # query under an identity
```

### Deployment pipelines (`pipeline`)

```console
$ ./bin/daxter pipeline ls                             # deployment pipelines
$ ./bin/daxter pipeline stages --pipeline <id>         # dev/test/prod → workspace
$ ./bin/daxter pipeline operations --pipeline <id>     # deployment history
```

### Output formats & piping

```console
$ ./bin/daxter query "EVALUATE Product" -o json | jq '.[].ProductName'
$ ./bin/daxter model measures --with-expr -o csv > measures.csv
```

`-o` / `--output` accepts `table` (default), `csv`, or `json` on every query/metadata
command.

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

## Build

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
interactive.

```bash
./bin/daxter login                                   # interactive sign-in (device code)
./bin/daxter query 'EVALUATE TOPN(10, Sales)'        # DAX → table
./bin/daxter query 'EVALUATE Sales' -o csv > out.csv # CSV
./bin/daxter query -f report.dax -o json             # query from a file → JSON
./bin/daxter dmv 'SELECT * FROM $SYSTEM.TMSCHEMA_MEASURES'
./bin/daxter ls                                      # list tables in the model
```

### Model metadata

```bash
./bin/daxter model measures --with-expr -o csv       # measures + DAX expressions
./bin/daxter model measure 'Total Sales'             # one measure's full definition
./bin/daxter model mcode --table 'Sales'      # Power Query (M) for a table
./bin/daxter model parameters                        # shared M expressions / parameters
./bin/daxter model partitions --table 'Sales' # partitions + last-refresh times
./bin/daxter model rls                               # RLS roles
./bin/daxter model rls --role 'Manager'              # a role's table filters + members
```

### Environments (one SP, workspace per env)

Define `DAXTER_WORKSPACE_<ENV>` (and optionally `DAXTER_DATASET_<ENV>`) in `.env`, then:

```bash
./bin/daxter env ls                                  # list profiles (* = active)
./bin/daxter model measures --env qa                 # run against the QA workspace
```

`--env <name>` (or `DAXTER_ENV`) selects the per-env workspace/dataset; `--workspace`
still overrides everything.

Results go to **stdout**; status, prompts, and errors go to **stderr** — so you can pipe
results cleanly. Output formats: `table` (default), `csv`, `json`.

### Without the wrapper

```bash
docker run --rm -it \
  -v daxter-tokens:/home/daxter/.daxter \
  --env-file .env \
  daxter:latest query 'EVALUATE ROW("ok", 1)'
```

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

```bash
make save           # → daxter-image.tar.gz
# on another machine:
make load           # docker load < daxter-image.tar.gz
```

Or push to a registry: `docker tag daxter:latest <registry>/daxter:1.0 && docker push …`.

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
a headless server). Exposes **read-only** tools (`daxter_query`, `daxter_dmv`,
`daxter_list_tables`, `daxter_measures`, `daxter_measure`, `daxter_mcode`,
`daxter_parameters`, `daxter_partitions`, `daxter_rls`, `daxter_diff_measures`,
`daxter_refresh_history`, `daxter_workspaces`, `daxter_datasets`, `daxter_reports`,
`daxter_lineage`) — each accepting optional `workspace`/`dataset` arguments. Results are
JSON, capped to 1,000 rows. Mutating operations are intentionally excluded.

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
