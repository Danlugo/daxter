# DAXter CLI examples

Every command, with a runnable example. Commands use the `./bin/daxter` wrapper (which
adds the env file + token volume); the bare `docker run ... daxter:latest <args>` form
works too. All commands accept connection options — `--workspace/-w`, `--dataset/-d`,
`--env/-e`, `--auth`, `--tenant`, `--client-id` — or read them from `.env`. Output is
`-o table` (default), `csv`, or `json`. Results go to **stdout**, status to **stderr**.

> Names below (`Sales Analytics`, `Retail Model`, `Sales`, `Regional Manager`) are generic
> placeholders — substitute your own workspace/model/table names.

## Authenticate

Two modes, set via `DAXTER_AUTH_MODE` (or `--auth`):

**Service principal** (automation / headless) — set `DAXTER_TENANT_ID`/`DAXTER_CLIENT_ID`/
`DAXTER_CLIENT_SECRET` in `.env`. Best for the MCP server and CI.

**Device code** (interactive, as *yourself*) — useful for operations that need **your** user
permissions rather than the SP's. For example, gateway **names** are only returned for
gateways the caller administers, so sign in as yourself (a gateway admin) to see them:

```bash
# Sign in once as yourself; token cached in a (separate) volume:
docker run --rm \
  -e DAXTER_AUTH_MODE=device-code \
  -e DAXTER_TENANT_ID=<tenant-guid> \
  -e DAXTER_WORKSPACE="<a workspace>" \
  -v daxter-user-tokens:/home/daxter/.daxter \
  ghcr.io/danlugo/daxter:latest ws gateways
# → prints a code; open https://login.microsoft.com/device, enter it, sign in.
# Subsequent commands with the same volume reuse the cached token silently.
```

Uses a built-in public client by default; set `DAXTER_CLIENT_ID` to your own public-client
app registration if your tenant requires one. (With the `./bin/daxter` wrapper and a
device-code `.env`, this is just `./bin/daxter login` then `./bin/daxter ws gateways`.)

## Environments (one SP, workspace per env)

```bash
./bin/daxter env ls                     # list configured profiles (* = active)
./bin/daxter ls --env qa                # run against DAXTER_WORKSPACE_QA
./bin/daxter measures --env prod        # --env picks the per-env workspace
```

## Query — DAX / MDX

```bash
./bin/daxter query "EVALUATE TOPN(10, Sales)"            # table (default)
./bin/daxter query "EVALUATE Sales" -o csv > sales.csv   # CSV to a file
./bin/daxter query "EVALUATE Sales" -o json              # JSON
./bin/daxter query -f report.dax                         # read query from a file
```

## DMV & schema

```bash
./bin/daxter ls                                                   # list tables
./bin/daxter dmv 'SELECT [CATALOG_NAME] FROM $SYSTEM.DBSCHEMA_CATALOGS'   # datasets in workspace
./bin/daxter dmv 'SELECT * FROM $SYSTEM.DISCOVER_STORAGE_TABLES'          # VertiPaq storage
```

## Model metadata

```bash
./bin/daxter model measures                      # measures (name, type, folder)
./bin/daxter model measures --with-expr -o csv   # include the DAX expression
./bin/daxter model measure "Total Sales"         # one measure, full definition
./bin/daxter model mcode --table Sales           # Power Query (M) for a table
./bin/daxter model parameters                    # shared M expressions / parameters
./bin/daxter model partitions --table Sales      # partitions + last-refresh times
./bin/daxter model rls                            # RLS roles
./bin/daxter model rls --role "Regional Manager" # a role's table filters + members
./bin/daxter model export -o model.bim            # export the .bim (TOM)
./bin/daxter model diff "Retail Model" "Retail Model v2"   # compare measures of two models
```

## Maintenance / Ops (safe by default)

Refreshes are **queued** onto a shared queue and run by the single worker hosted by the DAXter **web
container** — serialized one refresh per model (different models in parallel). `refresh …` commands
return a **job id**; they refuse to queue without `--yes`; `--dry-run` prints the plan (no connection
needed); PROD-looking targets require `--force`. **Start the web container** so its worker drains the
queue.

```bash
./bin/daxter refresh history --top 10            # recent refreshes (REST)
./bin/daxter refresh model --dry-run             # print the plan, queue nothing
./bin/daxter refresh model --yes                 # QUEUE a full-model refresh → prints job id
./bin/daxter refresh table --table Sales --yes   # queue a one-table refresh
./bin/daxter refresh partitions --table Sales --order newest-first --dry-run
./bin/daxter refresh partitions --table Sales --order newest-first --yes --retries 3  # queue; worker retries on transient failure
./bin/daxter refresh status                      # show the queue (queued/running/finished) + worker liveness
./bin/daxter refresh trigger --yes               # whole-model refresh via REST (async, NOT queued)
./bin/daxter cache clear --dry-run               # ClearCache XML preview (NOT queued)
```

> **The worker runs the refresh, not the CLI process.** `refresh … --yes` enqueues and returns
> immediately; killing the CLI doesn't stop a queued/running job. If `refresh status` reports
> *worker: none detected*, start the web container (`daxter web`) — it hosts the worker.

> **`--retries N`** (on any `refresh …`) is stored on the job; the **worker** re-attempts on a *transient*
> failure (connection drop, timeout, throttling) up to N more times with linear backoff (5 s, 10 s, …
> cap 30 s); default 0. Because the worker owns execution, retries survive the CLI process exiting.

## Workspace inventory (REST)

```bash
./bin/daxter ws ls                               # workspaces (with group ids)
./bin/daxter ws datasets                         # datasets in the workspace
./bin/daxter ws reports                          # reports
./bin/daxter ws lineage                          # report → dataset
./bin/daxter ws permissions                      # workspace users
./bin/daxter ws permissions --dataset "Retail Model"   # dataset users (who has access)
./bin/daxter ws gateways                         # gateways you administer (see note below)
./bin/daxter ws datasources --dataset "Retail Model"   # datasource + gateway binding (server/db/path + gatewayId)
```

### Take control & bind to a gateway (⚠ writes; dry-run unless `--yes`)

```bash
# Take over ownership (required before rebinding a model you don't own)
./bin/daxter ws takeover --dataset "Retail Model"            # dry run
./bin/daxter ws takeover --dataset "Retail Model" --yes      # apply

# Discover bindable gateways, then list one gateway's connections
./bin/daxter ws discover-gateways --dataset "Retail Model"
./bin/daxter ws gateway-datasources --gateway <gateway-guid>

# Bind the model to a gateway (optionally map specific connection ids)
./bin/daxter ws bind-gateway --dataset "Retail Model" --gateway <gateway-guid> --yes
./bin/daxter ws bind-gateway --dataset "Retail Model" --gateway <gateway-guid> --datasources id1,id2 --yes
```

> Supports **on-premises and VNet gateways**. A model uses a **single** gateway connection; the
> shareable **cloud-connection** "Maps to" (e.g. a Fabric source → a named cloud connection) has no
> public API — set it in the Service (model *Settings → Gateway and cloud connections*).

> **Gateway names:** `ws gateways` only returns gateways the **caller administers**, and
> `ws datasources` gives a dataset's `gatewayId` (not its name). A service principal usually
> administers no gateways, so it sees none. To get names, run **device-code as a user who
> administers the gateway** (see [Authenticate](#authenticate)), be added as an admin of that
> gateway, or look it up in *Manage connections and gateways* in the Power BI portal.

## RLS — view definitions, test impersonation

```bash
# List roles + view a role's DAX filter expressions + members (same definitions
# Tabular Editor shows in its role tree; read-only — edits go through `model edit role`)
./bin/daxter model rls                                # list roles + permission level
./bin/daxter model rls --role "Regional Manager"      # table filters (DAX) + members

# Run a query under the role's identity (impersonate a user; engine applies the RLS filter)
./bin/daxter test-rls --role "Regional Manager" --user jdoe@contoso.com \
  -q "EVALUATE ROW(\"rows\", COUNTROWS('Sales'))"
```

## Deployment pipelines

```bash
./bin/daxter pipeline ls                          # list deployment pipelines
./bin/daxter pipeline stages --pipeline <id>      # stages (dev/test/prod) + workspaces
./bin/daxter pipeline operations --pipeline <id>  # recent deploy operations
```

## Fabric SQL endpoints (Warehouses + Lakehouse SQL endpoints)

T-SQL against any Fabric Warehouse or Lakehouse SQL endpoint, AAD-authenticated with your
existing sign-in (one extra one-time device-code for the SQL scope — Power BI's client id
isn't pre-authorized for `database.windows.net`).

```bash
# One-time second sign-in for the SQL scope (silent thereafter):
./bin/daxter login --target sql

# Discovery — list every Warehouse + Lakehouse SQL endpoint in a workspace
./bin/daxter sql endpoints --workspace "Sales Analytics"

# Browse what's queryable on a specific endpoint (schemas → tables/views/functions/procs)
./bin/daxter sql objects --workspace "Sales Analytics" --endpoint "RetailWH"

# Sampling query (read-only by default; results in the chosen output format)
./bin/daxter sql query --workspace "Sales Analytics" --endpoint "RetailWH" \
  "SELECT TOP 10 * FROM [dbo].[orders] ORDER BY order_date DESC"

# Stream the FULL result set straight to a .csv (no in-memory materialization;
# safe for SELECT * on multi-million-row tables)
./bin/daxter sql query --workspace "Sales Analytics" --endpoint "RetailWH" \
  --out /out/orders_full.csv "SELECT * FROM [dbo].[orders]"

# Power BI / Excel "Export data" byte-compatible CSV (quote every field + CRLF endings)
./bin/daxter sql query --workspace "Sales Analytics" --endpoint "RetailWH" \
  --out /out/orders_excel.csv --quote-all --crlf \
  "SELECT * FROM [dbo].[orders]"

# Writes are gated behind --allow-writes (off by default — only SELECT/EXPLAIN/SHOW go through)
./bin/daxter sql query --workspace "Sales Analytics" --endpoint "RetailWH" \
  --allow-writes "UPDATE staging.summary SET status='processed' WHERE id IN (1,2,3)"
```

## Fabric Copy Jobs + Notebooks (view, run, monitor)

Browse Data Factory Copy Jobs and Fabric Notebooks in a workspace, view their definitions,
run on demand, and watch run history. Reads anytime; runs/cancels are dry-run unless `--yes`.

```bash
# List + inspect Copy Jobs
./bin/daxter fabric copy-jobs ls --workspace "Sales Analytics"
./bin/daxter fabric copy-jobs show --workspace "Sales Analytics" --copy-job <id>   # full copyjob-content.json

# Run on demand + watch (--yes to actually fire)
./bin/daxter fabric copy-jobs run --workspace "Sales Analytics" --copy-job <id> --yes
./bin/daxter fabric copy-jobs runs --workspace "Sales Analytics" --copy-job <id>   # instances + status + duration
./bin/daxter fabric copy-jobs cancel --workspace "Sales Analytics" --copy-job <id> --instance <instance-id> --yes

# Same shape for Notebooks
./bin/daxter fabric notebooks ls --workspace "Sales Analytics"
./bin/daxter fabric notebooks show --workspace "Sales Analytics" --notebook <id>             # ipynb by default
./bin/daxter fabric notebooks show --workspace "Sales Analytics" --notebook <id> --format FabricGitSource  # source file
./bin/daxter fabric notebooks run --workspace "Sales Analytics" --notebook <id> --yes
./bin/daxter fabric notebooks run --workspace "Sales Analytics" --notebook <id> \
  --execution-data '{"parameters":{"as_of_date":{"value":"2026-06-01","type":"string"}}}' --yes
./bin/daxter fabric notebooks runs --workspace "Sales Analytics" --notebook <id>
```

## Writes-gate workspaces (deny / allow lists with glob patterns)

DAXter's writes-gate is two lists of glob patterns (`*` = zero-or-more chars, case-insensitive,
match the whole name). **Deny wins; allow-list restricts further when non-empty.** Set via the
web console (Configure page) or env vars on every container:

```bash
# Deny-list — anything matching is locked from writes even with Allow writes on:
export DAXTER_READONLY_WORKSPACES='*Prod*, Reporting*'

# Allow-list — when non-empty, ONLY workspaces matching one of these can be written to:
export DAXTER_WRITE_WORKSPACES='Data*Dev, *QA'

# Legacy DAXTER_PROD_WORKSPACES still works — its entries become additional read-only patterns.
```

The refuse messages name the matched pattern: `Refusing to refresh a READ-ONLY target
('Reporting Prod East') — matched read-only pattern "*Prod*".`

## Editing the model (`model edit`)

Dry-run by default — prints the TMSL. Add `--yes` to apply (`--force` for a prod-looking target).
⚠ Editing a Desktop-authored model over XMLA is **irreversible for PBIX download**; a `.bim` backup is
written to `~/.daxter/backups/` before every apply, and the workspace XMLA endpoint must be **Read/Write**.

```bash
# Preview, then apply, a measure:
./bin/daxter model edit measure -t Sales -n Revenue --dax "SUM(Sales[Amount])" --format "\$#,0.00"
./bin/daxter model edit measure -t Sales -n Revenue --dax "SUM(Sales[Amount])" --yes

# Parameter / shared M expression, RLS role, calculated column, partition source, calculated table:
./bin/daxter model edit parameter -n ServerName --m '"sql.contoso.com" meta [IsParameterQuery=true]' --yes
./bin/daxter model edit role -n US --permission read --members "dana@contoso.com" \
  --filter-table Geography --filter '[Country] = "US"' --yes
./bin/daxter model edit column -t Sales -n Margin --dax "[Revenue]-[Cost]" --data-type double --yes
./bin/daxter model edit calc-table -n DimDate --dax "CALENDARAUTO()" --yes

# Import table (M source + typed columns) and relationships:
./bin/daxter model edit import-table -n Region --m 'let Source = Sql.Database("srv","db"){[Item="Region"]}[Data] in Source' --columns "RegionKey:int64:RegionKey,Region:string:Region" --yes
./bin/daxter model edit relationship --from-table Sales --from-column RegionKey --to-table Region --to-column RegionKey --name Sales_Region --yes
./bin/daxter model edit delete-relationship -n Sales_Region --yes

# Deletes + raw TMSL escape hatch:
./bin/daxter model edit delete-measure -t Sales -n "Old KPI" --yes
./bin/daxter model edit tmsl --tmsl '{"delete":{"object":{"database":"Retail Model","table":"Sales","measure":"X"}}}' --yes
```

## Targeting other clients / workspaces

```bash
# pick the client via the env file, the environment via --env:
docker run --rm --env-file ~/.daxter/clients/acme.env \
  -v daxter-acme-tokens:/home/daxter/.daxter \
  ghcr.io/danlugo/daxter:latest model measures --env qa

# or target any workspace by name (the name encodes the env):
./bin/daxter ls --workspace "Marketing - QA" --dataset "Some Model"
```

## Artifact store — the transport-agnostic file plane

DAXter persists file-shaped output (PBIR exports, SQL CSVs, future definition uploads)
into `~/.daxter/artifacts/` on the container volume. Five CLI verbs let you list, download,
zip, inspect, and delete entries — same store every MCP tool / Web page reads. Useful when
running DAXter remotely (hosted on another box): the artifact-store path is the API, not
the filesystem.

```bash
# Pre-seed the store: export a report and mirror its PBIR parts to the store under a key
# you choose. (The legacy ~/.daxter/exports/<report>/ path still works too — alongside.)
./bin/daxter fabric reports export --report "Sales Dashboard" --workspace "Data Hub - Dev"
#   → writes both PBIR parts and the .pbix into ~/.daxter/exports/Sales Dashboard/

# (To mirror into the artifact store too, use the MCP tool with artifact_key= or the SQL
#  --artifact-key option below — the export CLI doesn't expose it yet; coming in Phase 2.)

# List artifacts (with optional prefix to narrow):
./bin/daxter artifact list
./bin/daxter artifact list reports/sales-dashboard
# → key, bytes, created_utc, expires_utc, source_tool — JSON / table / CSV via -o

# Download one artifact's bytes:
./bin/daxter artifact get reports/sales-dashboard/report.json
./bin/daxter artifact get reports/sales-dashboard/report.json --out ./report.json

# Zip an entire prefix (every file under reports/sales-dashboard/ goes into one archive):
./bin/daxter artifact bundle reports/sales-dashboard --out ./sales-dashboard.zip
# → great for handing a full PBIR folder to the Power BI alignment-check skill in one shot.

# Inspect metadata without downloading:
./bin/daxter artifact meta reports/sales-dashboard/report.json

# Delete a single artifact or a whole prefix (recursive). Without --yes it confirms first.
./bin/daxter artifact rm reports/sales-dashboard
./bin/daxter artifact rm sql/exports/old.csv --yes
```

`daxter sql query --out` accepts `--artifact-key` to mirror the CSV into the store:

```bash
./bin/daxter sql query --workspace "Data Hub - Dev" --endpoint "Sales WH" \
  "SELECT * FROM dbo.fact_orders" \
  --out ./orders.csv --artifact-key sql/exports/orders-2026-06-09.csv
# → streams to ./orders.csv on disk AND mirrors into the artifact store under that key.
```

Quota + root are env-controlled — defaults are 5 GB at `~/.daxter/artifacts/`:

```bash
# Override on the docker run (the daxter-tokens volume already mounts ~/.daxter):
docker run -e DAXTER_ARTIFACTS_QUOTA_MB=20480 -e DAXTER_ARTIFACTS_ROOT=/data/daxter-artifacts ...
```

### Phase 2 — write path (`put` / `extract` / `purge-expired`)

```bash
# Put one file into the store with an optional TTL + source-tool stamp:
./bin/daxter artifact put reports/fixed/page.json ./corrected-page.json \
  --ttl-hours 48 --source-tool powerbi_alignment_fix

# Unzip a corrected PBIR archive INTO the store under a prefix:
./bin/daxter artifact extract alignment/sales-dashboard-fixed ./corrected.zip \
  --ttl-hours 24 --source-tool powerbi_alignment_fix

# On-demand sweep of every expired entry (mirrors the nightly purge):
./bin/daxter artifact purge-expired
```

The Web host runs an `ArtifactPurgeHostedService` every 6 hours by default
(`DAXTER_ARTIFACTS_PURGE_HOURS` env, set to `0` to disable).

## Phase 3 — Fabric write-backs

Direct CLI access to `updateDefinition` LROs isn't exposed (gated writes prefer the MCP
path so the dry-run / execute / writes-allowed checks land in one place). Use the MCP
tools above, or operate on the REST API directly via `gh api` if scripting.

## Shared-knowledge plane (Phase 4 — `daxter context`)

Text-first layer over the artifact store. Five verbs mirror the MCP surface:

```bash
# Curate a client glossary that every future session will see:
./bin/daxter context put clients/acme/glossary.md \
  --from-file ./acme-glossary.md \
  --source-tool team-curator

# Browse what's been curated:
./bin/daxter context list                         # everything
./bin/daxter context list clients                 # under clients/
./bin/daxter context list clients/acme            # under clients/acme/

# Read a single entry:
./bin/daxter context get clients/acme/glossary.md

# Search across keys + bodies (case-insensitive, ranked by hit count):
./bin/daxter context search TLA

# Delete (confirms first unless --yes):
./bin/daxter context rm incidents/INC1445816 --yes
```

Per-workspace cards under `workspaces/<ws>/` are auto-attached to `daxter_query` / MCP
responses (CLI `daxter query` returns rows as a table; auto-attach is a JSON-footer
feature meant for the MCP path). Per-endpoint cards under `endpoints/<ws>/<ep>/` work
the same way for `daxter_sql_query`.

## Which version am I running?

```bash
./bin/daxter version
# → {
#     "version": "v1.35.0",
#     "image":   "ghcr.io/danlugo/daxter:v1.35.0",
#     "repo_url": "https://github.com/Danlugo/daxter",
#     "releases_url": "https://github.com/Danlugo/daxter/releases",
#     "dotnet_version": "8.0.x",
#     "platform": "Linux ...",
#     "architecture": "X64"
#   }

# Also check GitHub for the latest published release (one outbound API call):
./bin/daxter version --check-latest
# → ... + "latest_published": "v1.35.0", "update_available": false
```

Same JSON envelope the MCP `daxter_version` tool returns — pipe to `jq` for scripting.

## Fleet identity (Semantics-friendly hooks — v1.36.0)

Two env vars stamp the container's identity into every "who am I" response (CLI / MCP / Web):

```bash
docker run \
  -e DAXTER_TENANT_ID=inspire \
  -e DAXTER_LABEL='Inspire Brands — Prod' \
  ... ghcr.io/danlugo/daxter:latest web
```

- `daxter version` now includes `tenant_id` + `label` at the top of the JSON when set.
- The Web home page shows a quiet banner when `DAXTER_LABEL` is set (hidden otherwise).
- `curl http://localhost:8080/api/health` returns one JSON envelope with tenant id, label,
  version, uptime, and artifact/context store stats — designed for fleet dashboards.

## Structured JSON errors (v1.37.0) — for fleet / orchestrator consumers

When a `ws ls` / `ws datasets` (and every other `RunRestQueryAsync`-backed) command is
invoked with `--output json`, **failures come back as a structured envelope on stderr**
instead of the human-readable "daxter: ..." line. Designed so consumers like
[Semantix](https://github.com/Level60/semantix) (the L60 control plane that wraps DAXter)
can match on a stable `error_code` instead of scraping `AADSTS*` text.

```bash
$ ./bin/daxter ws ls --output json 2>err.json
# stdout (1>) — the data (JSON array of workspaces) when it succeeds
# stderr (2>) — empty on success; structured envelope on failure:

$ cat err.json
{
  "error": {
    "error_code": "BAD_CLIENT_SECRET",
    "message": "The client secret is wrong or expired. Re-paste it from the Azure portal — note that 'secret value' (the long string) is required, not 'secret ID' (the GUID).",
    "aad_code": "AADSTS7000215",
    "trace_id": "abc123de-1234-...",
    "details": "AADSTS7000215: Invalid client secret provided. ..."
  }
}
```

### Stable error codes

These are the contract — Semantix and any other automated consumer parses them. New codes
go at the bottom of the list; existing codes never get repurposed.

| `error_code` | Maps to | Operator hint |
|---|---|---|
| `BAD_CLIENT_SECRET` | AADSTS7000215 | Re-paste the secret value (not the secret ID) |
| `BAD_CLIENT_ID` | AADSTS700016 | App registration missing or in wrong tenant |
| `BAD_TENANT_ID` | AADSTS90002, AADSTS900023, AADSTS50020 | Tenant id wrong / SP not in that tenant |
| `INSUFFICIENT_PERMISSIONS` | AADSTS65001, AADSTS50105 | Grant + admin-consent the API permissions |
| `NOT_SIGNED_IN` | DaxterException "Not signed in" | Run `daxter login` or set SP env vars |
| `WORKSPACE_NOT_FOUND` | DaxterException workspace failures | Wrong name, or SP can't see it |
| `ITEM_NOT_FOUND` | DaxterException dataset/report failures, HTTP 404 | Resource doesn't exist or isn't visible |
| `FORBIDDEN` | HTTP 403 | Authenticated but no role for the operation |
| `NETWORK_FAILURE` | Timeouts, non-403/404 HTTP errors | Connectivity issue |
| `UNKNOWN` | Anything else | Mapping not yet defined; `details` carries the raw message |

Exit code is **1** for any classified failure (i.e. anything except `UNKNOWN`), **2** for
`UNKNOWN`. So a consumer that just wants pass/fail-with-a-known-class can read the exit
code without parsing the JSON.

### When to use it

- **Automation / fleet code** (Semantix, CI scripts) → use `--output json` and check
  `error_code`.
- **Humans at a terminal** → omit `--output json` (or use `table` / `csv`) and read the
  one-line "daxter: ..." message as usual. Unchanged behaviour from v1.36.0 down.

## Native HTTP MCP transport (v1.38.0)

`daxter mcp` runs as a Model Context Protocol server. By default it serves over **stdio**
(unchanged — that's what every Claude Desktop install uses today). v1.38.0 adds an
optional `--http` flag that wraps the same tool catalogue in a **Streamable HTTP**
transport with bearer-token auth, suitable for hosted multi-tenant deployments.

```bash
# Stdio mode (default; unchanged behaviour, what Claude Desktop launches):
./bin/daxter mcp

# HTTP mode with explicit token from env:
DAXTER_MCP_BEARER_TOKEN=sk_my_secret_token \
  ./bin/daxter mcp --http --port 8000 --http-path /mcp

# HTTP mode with NO auth — local dev ONLY (refuses unless --no-auth is explicit):
./bin/daxter mcp --http --no-auth --port 8000 --http-path /mcp

# Regenerate the self-generated fallback token (Semantix-managed deployments don't
# need this — they pass the token via env and rotation = restart with a new value):
./bin/daxter mcp rotate-token
# → prints the new token on stdout; persists at ~/.daxter/mcp-bearer-token (chmod 600)
```

**Auth contract** (matches Caddy's response shape byte-for-byte so existing reverse-proxy
clients work without code changes):

| Case | Response |
|---|---|
| `Authorization: Bearer <correct token>` | request flows to the MCP pipeline |
| `Authorization: Bearer <wrong token>` | `401 "Unauthorized"` + `WWW-Authenticate: Bearer realm="daxter-mcp"` |
| no `Authorization` header | same `401 "Unauthorized"` |
| `Authorization: Basic …` (wrong scheme) | same `401 "Unauthorized"` |
| `GET /healthz` (any/no auth) | `200 "ok"` — for orchestrator liveness probes |

**Token sourcing** (priority order):

1. `DAXTER_MCP_BEARER_TOKEN` env var (primary path — Semantix and other fleet orchestrators set this per-container).
2. `~/.daxter/mcp-bearer-token` persisted file (fallback — auto-generated on first start; persists across container restarts via the token volume).
3. `--no-auth` flag — must be explicit. Refuses to start HTTP mode otherwise.

**Token format**: `sk_` prefix + 48 hex chars (192 bits entropy). Matches the existing
Semantix `sk_<hex>` shape so a token generated by either side is interoperable. The format
is enforced only for self-generation; the bearer check accepts any non-empty string from
the env var, so existing Semantix tokens keep working without reformatting.

**Logging**: full tokens are NEVER logged. Anywhere a token would appear in a log line it
goes through `Redact()` first → `sk_abc12***` (8-char prefix). Lets you correlate logs
with the value pasted into Semantix without leaking the secret to log aggregators.

## Apply Refresh Policy (v1.39.0) — for new-environment deploys

After deploying a model to a new workspace, tables with an incremental refresh policy
exist but their **policy-defined partitions haven't been materialised** yet. The
single default partition (the one with the GUID suffix in Tabular Editor's TOM Explorer)
needs to be replaced by the policy-walked rolling window. Tabular Editor does this with
the right-click "Apply refresh policy" menu item — DAXter does it with two flavours:

### Option A — bundled with a full model refresh

```bash
./bin/daxter refresh model --apply-policy --yes
```

Refreshes the WHOLE model (every table) + walks the refresh policy on tables that have
one (materialises the policy partitions). Non-policy tables get a normal full refresh.
Use this when you're refreshing anyway and want to bootstrap the policy partitions in
the same job.

### Option B — surgical, only touches policy tables (recommended for post-deploy)

```bash
# Discover all tables with a policy + apply on each (most common):
./bin/daxter refresh apply-policy --yes

# Just one specific table:
./bin/daxter refresh apply-policy --table "FACT - Trans Line" --yes
```

DAXter pre-flight scans the model via XMLA, identifies tables with a
`BasicRefreshPolicy`, and submits ONE refresh job scoped to ONLY those tables. **Non-policy
tables are not touched.** Mirrors Tabular Editor's per-table semantics.

Error cases:
- No tables in the model have a policy → `"No tables in this model have an incremental refresh policy — nothing to apply. Did you mean to run a regular refresh? (\`daxter refresh model\`)"`.
- `--table T` named a table without a policy → `"Table 'T' has no incremental refresh policy — nothing to apply. Use a normal refresh."`.
- Used with `--apply-policy` on a partition-targeted refresh → refused (apply-policy is a table-level operation).

## Securing the web console (v1.40.0)

The web console holds the signed-in token and can mutate Power BI, so it is **localhost-only
by default**:

```bash
daxter web                       # binds 127.0.0.1:8080 — safe on a shared network
daxter web --bind 0.0.0.0        # expose on all interfaces (see warning below)
DAXTER_WEB_BIND=0.0.0.0 daxter web   # same, via env (the container deploy uses this)
```

When you expose it beyond localhost, **gate the dangerous `/api/*` endpoints** with a bearer
token (otherwise `POST /api/sql/export` runs arbitrary T-SQL with the server's AAD token,
unauthenticated):

```bash
DAXTER_WEB_BEARER_TOKEN=$(openssl rand -hex 24) daxter web --bind 0.0.0.0
# Now /api/sql/export and /api/artifacts/* require:  Authorization: Bearer <that token>
# /api/health stays open (no secrets — liveness probe).
```

DAXter prints a loud warning if you bind wide without a token. See `SECURITY.md` for the
full posture (including the token-volume guidance).

## Encrypting the token cache (v1.41.0)

On Linux / in a container the MSAL token cache is plaintext by default (macOS keychain and
Windows DPAPI encrypt it; the slim Linux image has no keyring). Set `DAXTER_CACHE_KEY` to
encrypt it at rest with AES-256-GCM — supply the key from a secrets manager, not from a
file on the token volume:

```bash
DAXTER_CACHE_KEY=$(some-secrets-fetch) daxter web        # cache → msal_cache.enc (encrypted)
# Unset → platform default + a one-time "stored UNENCRYPTED" warning on Linux.
```

The first sign-in after setting the key re-authenticates once (the old plaintext cache is
not migrated by design). A wrong/rotated key just triggers a re-auth — it never breaks
sign-in. See `SECURITY.md`.
