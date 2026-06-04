# DAXter MCP examples

How to use each MCP tool from an assistant (Claude Desktop, Cursor, Claude Code) once the
`daxter` server is configured (see the README **MCP server** section). You ask in natural
language; the model picks the tool. Each tool accepts optional `workspace`/`dataset`
arguments — omit them to use the server's defaults, or name a workspace (the name encodes
the environment, e.g. `Marketing - QA`; an unsuffixed name like `Sales Analytics` = PROD).

> Names below (`Sales Analytics`, `Retail Model`, `Sales`) are generic placeholders —
> substitute your own.

**Primer to paste at the start of a session:**
> Use the `daxter` MCP server for Power BI questions. Default workspace is
> `Sales Analytics - Dev`, default model `Retail Model`. Name another workspace to target
> it. It's read-only unless writes are explicitly enabled. Start with `daxter_workspaces`.

## Sign in (device-code default)

When the server is configured for your own login (default), sign in once per machine:

| Ask | Tool |
|-----|------|
| "Sign in to Power BI" | `daxter_login` — returns a URL + code; open it, enter the code, sign in. The token is then cached. |
| "List my workspaces" (after sign-in) | `daxter_workspaces` — then pick a default, or name a workspace per request. |

If any tool replies *"Not signed in to Power BI"*, just say "sign in" again. (With a service
principal there's nothing to sign in — it's already authenticated.)

## Query & metadata

| Ask | Tool |
|-----|------|
| "Run this DAX: EVALUATE TOPN(10, Sales)" | `daxter_query` |
| "Run a DMV: SELECT * FROM $SYSTEM.TMSCHEMA_TABLES" | `daxter_dmv` |
| "List the tables in the model" | `daxter_list_tables` |
| "List the measures with their DAX" | `daxter_measures` (withExpression) |
| "Show the definition of the measure 'Total Sales'" | `daxter_measure` |
| "Show the M / Power Query code for the Sales table" | `daxter_mcode` |
| "What parameters does this model have?" | `daxter_parameters` |
| "When were the Sales partitions last refreshed?" | `daxter_partitions` |
| "List the RLS roles" | `daxter_rls` |
| "Export the model definition (.bim)" | `daxter_export` |
| "Compare measures between Retail Model and Retail Model v2" | `daxter_diff_measures` |
| "Test RLS: COUNTROWS('Sales') as jdoe@contoso.com" | `daxter_test_rls` |

## Inventory & governance

| Ask | Tool |
|-----|------|
| "List my workspaces" | `daxter_workspaces` |
| "What datasets are in Sales Analytics (PROD)?" | `daxter_datasets` |
| "List the reports in Sales Analytics - Dev" | `daxter_reports` |
| "Show report → dataset lineage for this workspace" | `daxter_lineage` |
| "Who has access to the Retail Model?" | `daxter_permissions` (with dataset) |
| "Who are the members of this workspace?" | `daxter_permissions` (no dataset) |
| **"List the gateways"** | `daxter_gateways` |
| "What datasource/gateway does Retail Model refresh through?" | `daxter_datasources` |
| "Which gateways can this model bind to?" | `daxter_discover_gateways` |
| "List the connections on gateway `<id>`" | `daxter_gateway_datasources` |
| "List the deployment pipelines" | `daxter_pipelines` |
| "Show the stages of pipeline <id>" | `daxter_pipeline_stages` |
| "Show recent deploy operations for pipeline <id>" | `daxter_pipeline_operations` |

## Deployment-rule audits

Deployment rules are inferred by comparing a model's parameter values across pipeline stages
(there is no public get-rules API). Saved checks are shared with the CLI and persisted to the volume.

| Ask | Tool |
|-----|------|
| "Show the deployment rules for model Sales in pipeline <id>" | `daxter_pipeline_rules` |
| "Which models in pipeline <id> have no deployment rule (identical across every stage)?" | `daxter_pipeline_models_without_rules` |
| "In pipeline <id>, which models have WAREHOUSE_URL = X in the Prod stage?" (add a model to check just one) | `daxter_pipeline_param_check` |
| "List my saved audit checks" | `daxter_audit_list_saved` |
| "Run my saved check 'Prod warehouse URL'" | `daxter_audit_run_saved` |
| "Run all my saved checks for pipeline <id>" (optionally scoped to one model) | `daxter_audit_run_all_saved` |

## Ops

| Ask | Tool |
|-----|------|
| "When was the model last refreshed?" | `daxter_refresh_history` |
| "List the refresh jobs / what's queued or running?" | `daxter_refresh_jobs` (shared queue, all interfaces; shows worker liveness) |

## Gated write tools (off by default)

`daxter_refresh` and `daxter_clear_cache` are **dry-run by default** and only act when `execute=true`
**and** writes are enabled. PROD targets are allowed by default once writes are on (set
`DAXTER_MCP_BLOCK_PROD_WRITES=true` to re-block them).

**Refreshes are queued, not run inline.** `daxter_refresh` with `execute=true` **enqueues** a job onto
the shared queue and returns a **job id**; the single worker hosted by the DAXter **web container** runs
it, **one refresh per model at a time** (different models in parallel). So keep the web container running,
and track progress with `daxter_refresh_jobs`. If no worker is live, the tool says so and the job waits.

| Ask | Behavior |
|-----|----------|
| "Show the plan to refresh the Sales table" | `daxter_refresh` (dry run — returns the plan, queues nothing) |
| "Refresh the Sales table" (writes disabled) | refused with instructions to enable |
| "Refresh the Sales table, do it" (writes enabled) | **queues** it → returns job #N; the worker runs it |
| "Refresh all Sales partitions newest-first, retry 3× on failure" | `daxter_refresh` with `scope: "partitions"`, `order: "newest-first"`, `execute: true`, `retries: 3` (queued; worker honors order + retries) |
| "Is that refresh done yet?" | `daxter_refresh_jobs` (optionally with `workspace`/`dataset` to filter to one model) |

To enable writes, tick **⚙ Configure → Allow writes** in the web console (it saves to the shared
volume the MCP server reads) — or set `DAXTER_MCP_ALLOW_WRITES=true` in the server env — then
restart Claude Desktop.

### Take control & bind to a gateway (gated)

`daxter_take_over` and `daxter_bind_to_gateway` are **dry-run by default**; set `execute: true` (with
writes enabled) to apply. Same prod-block as refresh.

| You say | Tool |
|---------|------|
| "Take over the Retail Model" | `daxter_take_over` (dry run → confirms what it would do) |
| "Take over the Retail Model, do it" | `daxter_take_over` with `execute: true` |
| "Which gateways can it bind to?" | `daxter_discover_gateways` |
| "Bind it to gateway `<id>`" | `daxter_bind_to_gateway` with `gatewayId`, `execute: true` |
| "Bind it to gateway `<id>`, mapping connections `a,b`" | `daxter_bind_to_gateway` with `gatewayId`, `datasourceIds: "a,b"`, `execute: true` |

Supports on-prem and VNet gateways. The shareable **cloud-connection** "Maps to" has no public API —
set it in the Service (model *Settings → Gateway and cloud connections*).

## Targeting environments / other workspaces

Every tool takes `workspace` (and `dataset`). Each accepts either the **name or the id (GUID)** —
DAXter resolves a GUID to its canonical name for you. Pass names **exactly as they appear**, including
**apostrophes** (`Reseller's Margin`) — no escaping or doubling needed. Examples:
- "List measures in **Marketing - QA**" → `daxter_measures` with `workspace: "Marketing - QA"`
- "Query **Sales Analytics** (prod): EVALUATE ROW(\"x\",1)" → `daxter_query` with `workspace: "Sales Analytics"`
- "Refresh dataset `11111111-2222-3333-4444-555555555555`" → `daxter_refresh` with `dataset: "<that id>"`

## Editing the model (gated, dry-run by default)

The edit tools (`daxter_edit_measure`, `daxter_delete_measure`, `daxter_set_parameter`,
`daxter_edit_role`, `daxter_edit_calculated_column`, `daxter_set_partition_source`,
`daxter_create_calculated_table`, `daxter_delete_*`, raw `daxter_edit_tmsl`) return the **TMSL
preview** unless `execute=true`. ⚠ Applying is **irreversible for PBIX download**; a `.bim` backup is
written first. To apply, enable *Allow model edits* in the web console (or
`DAXTER_MCP_ALLOW_MODEL_EDIT=true`).

| Ask | Tool |
|-----|------|
| "Add a measure `Revenue = SUM(Sales[Amount])` to the Sales table (preview)" | `daxter_edit_measure` (dry run) |
| "Now apply it" | `daxter_edit_measure` with `execute: true` |
| "Create an RLS role `US` filtering Geography to US" | `daxter_edit_role` |
| "Add a calculated column `Margin = [Revenue]-[Cost]` on Sales" | `daxter_edit_calculated_column` |
| "Delete the measure `Old KPI`" | `daxter_delete_measure` |
| "Add an import table `Region` from this M query with columns RegionKey, Region" | `daxter_create_import_table` |
| "Relate Sales[RegionKey] to Region[RegionKey]" | `daxter_edit_relationship` |
