# DAXter CLI examples

Every command, with a runnable example. Commands use the `./bin/daxter` wrapper (which
adds the env file + token volume); the bare `docker run ... daxter:latest <args>` form
works too. All commands accept connection options — `--workspace/-w`, `--dataset/-d`,
`--env/-e`, `--auth`, `--tenant`, `--client-id` — or read them from `.env`. Output is
`-o table` (default), `csv`, or `json`. Results go to **stdout**, status to **stderr**.

> Names below (`Sales Analytics`, `Retail Model`, `Sales`, `Regional Manager`) are generic
> placeholders — substitute your own workspace/model/table names.

## Authenticate

```bash
./bin/daxter login                      # interactive device-code sign-in (token cached)
# service principal: set DAXTER_AUTH_MODE=service-principal + tenant/client/secret in .env
```

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
./bin/daxter refresh trigger --yes               # whole-model refresh via REST
./bin/daxter cache clear --dry-run               # ClearCache XML preview
```

## Workspace inventory (REST)

```bash
./bin/daxter ws ls                               # workspaces (with group ids)
./bin/daxter ws datasets                         # datasets in the workspace
./bin/daxter ws reports                          # reports
./bin/daxter ws lineage                          # report → dataset
./bin/daxter ws permissions                      # workspace users
./bin/daxter ws permissions --dataset "Retail Model"   # dataset users (who has access)
./bin/daxter ws gateways                         # gateways (needs gateway-admin)
./bin/daxter ws datasources --dataset "Retail Model"   # datasource + gateway binding
```

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

## Targeting other clients / workspaces

```bash
# pick the client via the env file, the environment via --env:
docker run --rm --env-file ~/.daxter/clients/acme.env \
  -v daxter-acme-tokens:/home/daxter/.daxter \
  ghcr.io/danlugo/daxter:latest model measures --env qa

# or target any workspace by name (the name encodes the env):
./bin/daxter ls --workspace "Marketing - QA" --dataset "Some Model"
```
