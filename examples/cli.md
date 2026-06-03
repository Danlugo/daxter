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

Mutating commands print the exact TMSL/REST call and refuse to run without `--yes`;
`--dry-run` previews; PROD-looking targets require `--force`.

```bash
./bin/daxter refresh history --top 10            # recent refreshes (REST)
./bin/daxter refresh model --dry-run             # preview the TMSL, run nothing
./bin/daxter refresh model --yes                 # full model refresh (TMSL)
./bin/daxter refresh table --table Sales --yes   # one table
./bin/daxter refresh partitions --table Sales --order newest-first --dry-run
./bin/daxter refresh partitions --table Sales --order newest-first --yes --retries 3  # retry on transient failure
./bin/daxter refresh trigger --yes               # whole-model refresh via REST
./bin/daxter cache clear --dry-run               # ClearCache XML preview
```

> **`--retries N`** (on any `refresh …` and `cache clear`) re-attempts on a *transient* failure
> (connection drop, timeout, throttling) up to N more times with linear backoff (5 s, 10 s, … cap 30 s);
> default 0. It retries the operation **within the run** — it can't recover from the process being killed.

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

> **Gateway names:** `ws gateways` only returns gateways the **caller administers**, and
> `ws datasources` gives a dataset's `gatewayId` (not its name). A service principal usually
> administers no gateways, so it sees none. To get names, run **device-code as a user who
> administers the gateway** (see [Authenticate](#authenticate)), be added as an admin of that
> gateway, or look it up in *Manage connections and gateways* in the Power BI portal.

## RLS testing (impersonation)

```bash
./bin/daxter test-rls --role "Regional Manager" --user jdoe@contoso.com \
  -q "EVALUATE ROW(\"rows\", COUNTROWS('Sales'))"
```

## Deployment pipelines

```bash
./bin/daxter pipeline ls                          # list deployment pipelines
./bin/daxter pipeline stages --pipeline <id>      # stages (dev/test/prod) + workspaces
./bin/daxter pipeline operations --pipeline <id>  # recent deploy operations
```

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
