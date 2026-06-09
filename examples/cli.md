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
