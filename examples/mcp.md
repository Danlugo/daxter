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

## Discover what's available

Before anything else, ask Claude **"what can DAXter do?"** — it calls `daxter_capabilities`,
which reflects the live tool set at runtime (each entry tagged read / write-gated / write-gated-destructive).
Every release auto-surfaces its new tools here; no out-of-band docs to keep in sync.

| You say | Tool |
|---|---|
| "What DAXter tools are available?" | `daxter_capabilities` (returns version + every tool's name, title, kind, description) |
| "Which DAXter version am I talking to?" | `daxter_version` — small + cheap: version, GHCR image tag, .NET runtime, OS / arch. Pass `checkLatest=true` for an extra GitHub API call that returns the latest published release + `update_available` boolean. |

## Sign in (device-code default)

When the server is configured for your own login (default), sign in once per machine:

| Ask | Tool |
|-----|------|
| "Sign in to Power BI" | `daxter_login` — returns a URL + code; open it, enter the code, sign in. The token is then cached. |
| "Sign in for Fabric SQL" (one-time second sign-in for the SQL audience) | `daxter_login` with `target="sql"` — same flow against a separate pre-authorized client id. |
| "List my workspaces" (after sign-in) | `daxter_workspaces` — then pick a default, or name a workspace per request. |

If any tool replies *"Not signed in to Power BI"*, just say "sign in" again. If it says
*"Not signed in to Fabric SQL endpoints"*, sign in once more with `target="sql"` — that's a
separate token because Power BI's first-party client id isn't pre-authorized for
`database.windows.net` (AADSTS65002). After that one extra sign-in, both surfaces refresh
silently for ~90 days. (With a service principal there's nothing to sign in.)

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
| "Show the DAX filter expressions for the IHUB role" | `daxter_role_filters` (returns `(Table, FilterExpression)` for each filtered table — the raw DAX a model author wrote in Tabular Editor) |
| "Who's a member of the Regional Manager role?" | `daxter_role_members` |
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

## Fabric SQL endpoints (Warehouses + Lakehouse SQL endpoints)

T-SQL against any Fabric Warehouse or Lakehouse SQL endpoint, AAD-authenticated via the
same MSAL account (one extra one-time `daxter_login` with `target="sql"`). Discover → browse
→ query → export.

| Ask | Tool |
|-----|------|
| "What SQL endpoints exist in the Data Warehouse workspace?" | `daxter_sql_endpoints` (lists every Warehouse + Lakehouse SQL endpoint with server + database + kind) |
| "What tables/views/procs are on the RetailWH endpoint?" | `daxter_sql_objects` (one INFORMATION_SCHEMA round-trip; (schema, kind, name) rows for tables/views/functions/stored procedures) |
| "Show me the top 10 orders from RetailWH" | `daxter_sql_query` (read-only by default; capped JSON result for sampling) |
| "Export every row of `dbo.orders` to a file" | `daxter_sql_export` (streams full result-set CSV straight to `~/.daxter/exports/sql/<ts>-<endpoint>.csv` on the persistent volume — no in-memory materialization, safe for `SELECT *` on multi-million-row tables) |
| "Export with Excel-Windows formatting" | `daxter_sql_export` with `quoteAll=true, crlf=true` (matches Power BI / Excel "Export data" byte-for-byte) |

Write statements (INSERT/UPDATE/DELETE/MERGE/DDL/EXEC) are **refused by default** —
`daxter_sql_query` and `daxter_sql_export` only execute reads unless the Allow-writes gate
is on AND the workspace doesn't match the read-only patterns.

## Fabric Copy Jobs + Notebooks (view, run, monitor)

Browse Data Factory Copy Jobs and Fabric Notebooks · view definitions · run on demand
(writes-gated, dry-run by default) · watch run history · cancel a running instance.
The `daxter_item_runs` / `daxter_item_job_status` / `daxter_cancel_item_job` tools work
on ANY Fabric item — Copy Job, Notebook, Pipeline — so the monitoring loop is uniform.

| Ask | Tool |
|-----|------|
| "List the Copy Jobs in this workspace" | `daxter_copy_jobs` |
| "Show the JSON definition for the IngestSales copy job" | `daxter_copy_job_definition` (the full `copyjob-content.json` — source/destination/mapping) |
| "Run the IngestSales copy job" (dry run) | `daxter_run_copy_job` (returns the plan without firing) |
| "Run the IngestSales copy job, do it" | `daxter_run_copy_job` with `execute: true` — returns the new instance id |
| "Did that run finish?" | `daxter_item_job_status` with the item id + instance id (terminal states: `Completed` / `Failed` / `Cancelled`) |
| "Show me the last few runs of IngestSales" | `daxter_item_runs` (instanceId, status, invokeType, start/end, durationSec, failureReason) |
| "Cancel that running instance" | `daxter_cancel_item_job` with `execute: true` |
| "List the Notebooks" | `daxter_notebooks` |
| "Show me the cells of the DataPrep notebook" | `daxter_notebook_definition` (ipynb by default; pass `format: "FabricGitSource"` for the .py/.scala/.sql source file) |
| "Run the DataPrep notebook" | `daxter_run_notebook` with `execute: true` |
| "Run DataPrep with as_of_date=2026-06-01" | `daxter_run_notebook` with `execute: true, executionData: '{"parameters":{"as_of_date":{"value":"2026-06-01","type":"string"}}}'` |

The discover → inspect → run → poll loop:
`daxter_copy_jobs` → `daxter_copy_job_definition` → `daxter_run_copy_job (execute=true)` →
poll `daxter_item_job_status` until status is terminal.

## Workspace writes-gate (read-only / write-allowed patterns)

DAXter's MCP refuses writes against workspaces in your **read-only list** (deny-list) or
outside your **write-allowed list** (allow-list, when set). Patterns use `*` as wildcard,
case-insensitive, anchored to the whole name. Refuse messages name the matched pattern:
*"REFUSED — 'Reporting Prod East' is READ-ONLY (read-only pattern '*Prod*')"*.

Configure once on the **/configure** web page (the two new inputs) or via env vars on the
MCP server container:

```bash
# Deny-list: anything matching is locked from writes:
DAXTER_READONLY_WORKSPACES=*Prod*, Reporting*
# Allow-list: when set, ONLY workspaces matching one of these can be written to:
DAXTER_WRITE_WORKSPACES=Data*Dev, *QA
```

When you set either list, the MCP **auto-enforces it** — no extra opt-in env var needed.
(Legacy heuristic-only setups still gate via `DAXTER_MCP_BLOCK_PROD_WRITES=true`.)

## Gated write tools (off by default)

`daxter_refresh` and `daxter_clear_cache` are **dry-run by default** and only act when `execute=true`
**and** writes are enabled. Workspaces matching your **read-only patterns** (or outside your
**write-allowed patterns**, when that list is non-empty) are refused even with writes on —
see [Workspace writes-gate](#workspace-writes-gate-read-only--write-allowed-patterns).
For legacy heuristic-only setups (no explicit lists), the prod-block stays opt-in via
`DAXTER_MCP_BLOCK_PROD_WRITES=true`.

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

## Artifact store — the transport-agnostic file plane

DAXter persists file-shaped output (PBIR exports, SQL CSVs, future definition uploads)
into `~/.daxter/artifacts/` on the volume. Five MCP tools let the agent **list, fetch,
zip-bundle, inspect, delete** without needing filesystem access to the container — same
API whether DAXter is local Docker or a hosted instance. Files ≤ 10 MB come back inline as
base64; bigger ones return a `download_url` the agent fetches via plain HTTP (the Web host's
`/api/artifacts/{key}` streaming endpoint).

| Ask | Tool |
|---|---|
| "What's in the artifact store?" | `daxter_artifact_list` (optional `prefix` to narrow) |
| "Get me the report.json for the sales dashboard" | `daxter_artifact_get` with `key: "reports/sales-dashboard/report.json"` |
| "Download the whole PBIR folder for sales-dashboard" | `daxter_artifact_bundle` with `prefix: "reports/sales-dashboard"` (returns zip, inline or url) |
| "How big is reports/sales-dashboard/report.json — and when does it expire?" | `daxter_artifact_meta` with `key: …` |
| "Delete the old sql/exports/q3.csv" | `daxter_artifact_delete` with `keyPrefix: "sql/exports/q3.csv"` — echoes count + bytes |

### Two existing tools now mirror to the store

- **`daxter_export_report`** accepts `artifact_key` — when set, the PBIR parts mirror into
  the store under that key prefix and the `.pbix` lands at `<key>.pbix`. Pair with
  `daxter_artifact_bundle` to grab the whole PBIR folder over the wire.
- **`daxter_sql_export`** accepts `artifactKey` — when set, the streaming CSV is ALSO
  written into the artifact store under that key (in addition to the legacy
  `~/.daxter/exports/sql/` file path).

### End-to-end flow — Diego's alignment-check round-trip

```text
You:    "Export the Sales Dashboard report from Data Hub - Dev and put the PBIR parts
         under alignment/sales-dashboard so I can run the alignment check."
Claude: → daxter_export_report
          report:       "Sales Dashboard"
          workspace:    "Data Hub - Dev"
          pbix:         false
          artifact_key: "alignment/sales-dashboard"
        ↳ "definition: 17 parts → /home/daxter/.daxter/exports/Sales Dashboard/
                       also mirrored to artifact prefix 'alignment/sales-dashboard/'."

You:    "Download the whole thing as a zip — I'll feed it to the alignment skill."
Claude: → daxter_artifact_bundle  prefix: "alignment/sales-dashboard"
        ↳ inline base64 (zip) for ≤ 10 MB, OR a download_url for bigger.

Claude runs powerbi-alignment-check on the unzipped folder (Diego's skill).
Phase 3 (future): `daxter_update_report_definition` to upload corrected files back.
```

### Phase 2 — write path (`daxter_artifact_put` / `_extract`)

Both tools take EITHER inline `contentBase64` / `zipBase64` (small) OR a `fetchUrl` the
DAXter daemon will GET (large; the URL must be reachable from inside the container).
Optional `ttlHours` + `sourceTool` flow through to the metadata index.

| Ask | Tool |
|---|---|
| "Upload my corrected page.json under reports/fixed/page.json" | `daxter_artifact_put` with `key: …`, `contentBase64: …`, `sourceTool: "powerbi_alignment_fix"` |
| "Take this zip of a corrected PBIR folder and put it under alignment/sales-dashboard-fixed" | `daxter_artifact_extract` with `keyPrefix: …`, `zipBase64: …` |
| "Fetch this 200 MB .pbix from https://… into the store" | `daxter_artifact_put` with `fetchUrl: …` (no MCP payload size limit) |

Quota refusal returns a clean **REFUSED** message naming the byte budget — pair with
`daxter_artifact_list` + `_delete` to free space, then retry.

### End-to-end with Phase 2: Diego's full alignment loop except the last step

```text
You:    "Pull the Sales Dashboard PBIR, run alignment-fix, then upload the corrected folder."
Claude: → daxter_export_report  artifact_key:"alignment/sales-dashboard"
        → daxter_artifact_bundle prefix:"alignment/sales-dashboard"
        ↳ unzipped to a temp folder; powerbi-alignment-fix runs on it
        → daxter_artifact_extract keyPrefix:"alignment/sales-dashboard-fixed", zipBase64:<corrected>
        ↳ ready for Phase 3's daxter_update_report_definition to push back to Fabric.
```

## Phase 3 — Fabric write-backs (gated)

Closes the round-trip the artifact store opened. The agent puts a corrected definition
into the store (Phase 2's `daxter_artifact_extract` or `_put`), then these three tools
publish it to Fabric. Gated like every other DAXter write — dry-run unless `execute=true`
**and** the writes gate is on **and** the workspace isn't on the read-only list.

| Ask | Tool |
|---|---|
| "Push my corrected PBIR back to Sales Dashboard (dry-run first)" | `daxter_update_report_definition` with `report: "Sales Dashboard"`, `artifact_key_prefix: "alignment/sales-dashboard-fixed"` |
| "Now actually publish it" | same tool with `execute: true` (writes must be enabled) |
| "Publish a corrected notebook over notebook id `…`" | `daxter_update_notebook_definition` with `notebook_id: "<guid>"`, `artifact_key_prefix: "…"` |
| "Publish a corrected copy-job over copy-job id `…`" | `daxter_update_copy_job_definition` with `copy_job_id: "<guid>"`, `artifact_key_prefix: "…"` |

The MCP tool reads every artifact under `artifact_key_prefix`, trims the prefix from each
key to produce the relative PBIR part path, and POSTs the whole bundle as a single
`updateDefinition` LRO — Fabric runs it, status polled until done.

### Diego's full alignment loop, end-to-end (now works one-shot)

```text
You:    "Pull Sales Dashboard, run alignment-check + fix, then publish the corrected PBIR back."
Claude: → daxter_export_report  report:"Sales Dashboard"  artifact_key:"alignment/sales-dashboard"
        → daxter_artifact_bundle prefix:"alignment/sales-dashboard"
        ↳ unzip; powerbi-alignment-check + _fix run locally
        → daxter_artifact_extract keyPrefix:"alignment/sales-dashboard-fixed", zipBase64:<corrected>
        → daxter_update_report_definition  report:"Sales Dashboard"
            artifact_key_prefix:"alignment/sales-dashboard-fixed"  (dry-run)
        ↳ "DRY RUN — would push 17 part(s) from artifact prefix '…' to reports/Sales Dashboard …"
You:    "Apply"
Claude: → same tool with execute:true → "Updated — 17 part(s) published successfully."
```

No `docker cp`, no Power BI Desktop, no bind-mount. The entire loop rides the artifact
store + the Fabric REST `updateDefinition` LRO.

## Shared-knowledge plane (Phase 4 — `daxter_context_*`)

Sits on top of the artifact store under the reserved `context/` prefix. Text-not-base64
by design — these payloads are markdown, prose, prompts; not file bytes. **Visible to
every MCP session connected to this DAXter** — write once from any session, every future
session reads it. Five tools:

| Ask | Tool |
|---|---|
| "What shared knowledge has the team curated?" | `daxter_capabilities` (now lists `context_namespaces` too) |
| "List everything under clients/acme" | `daxter_context_list` with `namespace_path: "clients/acme"` |
| "Show me the ACME glossary" | `daxter_context_get` with `key: "clients/acme/glossary.md"` |
| "Write this client glossary so every session can see it" | `daxter_context_put` with `key: "clients/acme/glossary.md"`, `content: "<markdown>"`, `source_tool: "team-curator"` |
| "Find every context entry mentioning TLA" | `daxter_context_search` with `query: "TLA"` (matches keys + bodies, ranked by hit count) |
| "Delete the stale incident notes" | `daxter_context_delete` with `key_or_namespace: "incidents/INC123"` |

### Auto-attach — context arrives WITH the data

`daxter_query` and `daxter_sql_query` automatically append matching context cards to
their response footer. **No extra call needed**:

- `daxter_query workspace:"Data Hub - Prod" dataset:"Sales Model" ...` →
  attaches every entry under `context/workspaces/Data Hub - Prod/` AND `context/datasets/Data Hub - Prod/Sales Model/`.
- `daxter_sql_query workspace:"Data Hub - Prod" endpoint:"Sales WH" ...` →
  attaches `context/workspaces/Data Hub - Prod/` AND `context/endpoints/Data Hub - Prod/Sales WH/`.

The footer looks like this on the wire:

```text
{ ...primary JSON result... }

/* ────── attached context (curated team knowledge for this target) ────── */

/* context: workspaces/Data Hub - Prod/conventions.md  (source: team-curator, 1,247 B) */
Never write to Prod without explicit approval from the owner...

/* context: datasets/Data Hub - Prod/Sales Model/glossary.md  (source: dgonzalez, 892 B) */
TLA = trailing last audit (not "three letter acronym"). RegionN = stores 100-199...
```

The agent reads it as part of the response text and applies it to the question.

### Conventional namespaces (we document these; nothing's enforced)

| Namespace | What goes there | Who reads |
|---|---|---|
| `clients/<client>/` | per-client glossary + conventions | every agent answering questions for that client |
| `workspaces/<ws>/` | per-workspace context cards | auto-attached to `daxter_query` against that workspace |
| `endpoints/<ws>/<ep>/` | per-Fabric-SQL-endpoint cards | auto-attached to `daxter_sql_query` |
| `skills/<topic>/` | reusable knowledge snippets (DAX patterns, SQL cheatsheets) | any agent working on the topic |
| `conventions/` | global team rules | every agent (read at session start via daxter_capabilities) |
| `incidents/<ticket>/` | active investigation notes (often with `ttl_hours`) | anyone joining the ticket |

### Multi-session use case (the killer story)

```text
Session 1 (Diego, Monday):
  daxter_context_put
    key: "clients/acme/glossary.md"
    content: "ACME's stores numbered 100-499. Brand IDs: ARB=Arby's, SON=Sonic..."
    source_tool: "diego"

Session 2 (you, Tuesday — different Claude Desktop on a different machine but same DAXter):
  daxter_query workspace:"ACME - Prod" "EVALUATE TOPN(10, Sales)"
  ↳ JSON result + AUTO-ATTACHED context with Diego's glossary as the footer.
  ↳ You didn't know the glossary existed; the agent applied it anyway.
```

## Fleet identity (Semantics-friendly hooks — v1.36.0)

When DAXter runs under a fleet orchestrator (the L60 **Semantics** platform spins up one
DAXter container per client), two env vars stamp the container's identity into every
"who am I" response:

| Env var | Purpose | Surfaced in |
|---|---|---|
| `DAXTER_TENANT_ID` | opaque short id (e.g. `inspire`, `acme`) | `daxter_version`, `daxter_capabilities`, `GET /api/health` |
| `DAXTER_LABEL` | human-readable label (e.g. `Inspire Brands — Prod`) | same three, plus the Web home-page banner |

Both are optional — local-laptop users won't set either and see the same UX as before.

Agents that need to know which tenant they're connected to:

| You say | Tool |
|---|---|
| "Which tenant am I on?" | `daxter_version` — fields `tenant_id` + `label` appear at the top of the JSON when set |
| "What's the full capability set for this tenant?" | `daxter_capabilities` — same `tenant_id` + `label` stamping; Semantics fleet UI uses this to build a per-tenant catalogue grid |

The `GET /api/health` endpoint on the Web host returns the same identity + uptime + store
stats in one JSON envelope, designed for Semantics dashboards to poll without exec'ing
into the container:

```bash
curl -s http://daxter-tenant-inspire:8080/api/health | jq
# → {
#     "tenant_id":  "inspire",
#     "label":      "Inspire Brands — Prod",
#     "version":    "v1.36.0",
#     "image":      "ghcr.io/danlugo/daxter:v1.36.0",
#     "uptime_seconds": 12345,
#     "artifacts":  { "used_bytes": 3145728, "quota_bytes": 5368709120, "count": 14 },
#     "context":    { "entry_count": 9, "namespace_count": 3 }
#   }
```

No auth required (the endpoint never returns secrets — no signed-in identity, no Power BI
data, no tokens). Put a reverse proxy in front of Semantics if per-tenant access control
matters.
