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
| "List the deployment pipelines" | `daxter_pipelines` |
| "Show the stages of pipeline <id>" | `daxter_pipeline_stages` |
| "Show recent deploy operations for pipeline <id>" | `daxter_pipeline_operations` |

## Ops

| Ask | Tool |
|-----|------|
| "When was the model last refreshed?" | `daxter_refresh_history` |

## Gated write tools (off by default)

`daxter_refresh` and `daxter_clear_cache` are **dry-run by default** and only execute when
`execute=true` **and** the server is launched with `DAXTER_MCP_ALLOW_WRITES=true`.
PROD-looking targets are always refused.

| Ask | Behavior |
|-----|----------|
| "Show me the TMSL to refresh the Sales table" | `daxter_refresh` (dry run — returns TMSL) |
| "Refresh the Sales table" (writes disabled) | refused with instructions to enable |
| "Refresh the Sales table, execute it" (writes enabled, non-prod) | runs the refresh |

To enable writes, add `-e DAXTER_MCP_ALLOW_WRITES=true` to the server's `docker run` args
(or `DAXTER_MCP_ALLOW_WRITES=true` in its env file), then restart the client.

## Targeting environments / other workspaces

Every tool takes `workspace` (and `dataset`). Examples:
- "List measures in **Marketing - QA**" → `daxter_measures` with `workspace: "Marketing - QA"`
- "Query **Sales Analytics** (prod): EVALUATE ROW(\"x\",1)" → `daxter_query` with `workspace: "Sales Analytics"`
