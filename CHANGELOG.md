# Changelog

All notable changes to DAXter are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/) and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.7.3] - 2026-05-31

### Fixed
- **`daxter ws ls` and `ws gateways` no longer require a workspace.** Listing workspaces/gateways
  is a tenant-level operation, but the CLI resolved config with `requireWorkspace: true` and failed
  with `No workspace configured` even when the token was valid. They now resolve with
  `requireWorkspace: false`, matching the MCP `daxter_workspaces` / `daxter_gateways` tools.
  This also un-breaks the `ws ls` sign-in self-check the install agent runs in `SETUP.md`.
  (`ConnectionOptions.Resolve` gained a `requireWorkspace` parameter; regression-tested.)

## [1.7.2] - 2026-05-30

### Docs
- **Install-guide overhaul (`SETUP.md`)** for an agent-driven, non-technical-user experience:
  - **Sign in (step 4) before the final restart (step 5)** — Claude Desktop loads the `daxter`
    config on its first restart and the cached token is already there, so the tools work first try.
  - **The agent self-verifies** the whole path with the CLI (`ws ls`) instead of asking the user
    to run a test, then reassures them it's working and shows their workspaces.
  - **"Keep it simple" guidance** — narrate high-level ("step 2 of 5…"), hide raw command output
    unless something errors; reduce it to the two human actions (sign in, restart).
  - **`docker info` daemon-running gate** before install; **web console** is the primary,
    single sign-in path (separate process, absolute `--env-file`, configurable port, persistence);
    **Windows PowerShell** config-merge; **`DAXTER_TENANT_ID`** recommended with a placeholder-trap
    opt-out; clarified that **no `…mcp` container in `docker ps` is normal** (stdio).
- **New "Remove / uninstall DAXter" section** — unhook from Claude Desktop, stop the web console,
  and optional cleanup (token volume, image, env file).
- Web console container is now named **`daxter-web`** for an easy stop/remove.
- **Recommended setup prompt** points the agent at the **raw** `SETUP.md` for exact commands and
  names "Claude Code" + the two user hand-offs explicitly.

## [1.7.1] - 2026-05-30

### Changed
- **Simpler in-chat sign-in.** `daxter_login` now returns a clean, click-first message —
  *"🔐 Signing you in to Power BI: 1) Open <link> 2) Enter code: XXXX 3) Sign in"* plus the
  next step — built from the structured device-code URL/code instead of the raw MSAL string.
  When a cached token is still valid it just says you're already signed in. Matches the
  streamlined `SETUP.md` step 5 (`DaxterToolRuntime.FormatLoginPrompt`, unit-tested).

### Docs
- **`SETUP.md`** rewritten for a faster first run: a `docker info` "is the daemon running?"
  gate before install; the web console called out as a separate process (with an absolute
  `--env-file` path, configurable port, and persistence note); `DAXTER_TENANT_ID` promoted to
  recommended with a placeholder-trap warning; a **Windows PowerShell** config-merge alongside
  the macOS/Linux one; and a dead-simple, click-the-link sign-in step.
- **`README.md` / `CLAUDE.md` / `examples/mcp.md`** synced to the current surface (33 MCP tools,
  audit rule sets, the web Audit page).

## [1.7.0] - 2026-05-30

### Added
- **Three XMLA/REST capabilities that were CLI/MCP-only are now in the web console:**
  - **Test RLS** (Explore → a dataset's **Test RLS** command). Run a DAX query while
    **impersonating a role and/or a user (UPN)** to see exactly what they'd see. The role
    dropdown is populated from the model's roles; the effective-user box takes any UPN. Read-only,
    but the signed-in identity must be an **admin** of the workspace/model to impersonate
    (`Roles` + `EffectiveUserName` on the XMLA connection).
  - **Gateways** (new **Gateways** nav page). Lists the on-premises data gateways visible to the
    signed-in identity (REST `/gateways`) — only gateways the caller administers return a name.
  - **Deployment Pipelines** (new **Pipelines** nav page). Lists deployment pipelines; click one
    to drill into its **stages** (Dev → Test → Prod workspaces) and recent **operations**
    (REST `/pipelines`, `/pipelines/{id}/stages`, `/pipelines/{id}/operations`).

### Added
- **Deployment-rule check on the Pipelines page (model-first).** Pick a **model** and DAXter finds
  every accessible pipeline that contains it, then lays out **one card per stage** (Dev → QA → Prod).
  Each card shows two sections: **Model Parameters** (every parameter + its value in that stage, read
  over XMLA) and **Deployment Rules** (the parameters whose value differs from the source/Dev stage =
  the inferred rule overriding them there). Rule values are tinted for quick scanning. The public Power
  BI REST API has no operation to read rule objects directly, so these are inferred from per-stage value
  differences; stages where the model can't be read are noted. Raw stages + operations remain under a
  collapsible "Browse pipelines" section. (Requires the signed-in identity to have pipeline access —
  works in the web console, not the MCP service principal.) The Pipelines page has its own
  **Frequent · Pipelines** sidebar with a **Models** group — click a recent model to rebuild its
  stage board instantly.
- **Same check on the CLI + MCP.** The rule-inference logic lives in `Daxter.Core` and is shared by
  all three layers:
  - **CLI**: `daxter pipeline rules --pipeline <id> --model <name>` — prints a `Parameter | <stage 1> | <stage 2> | … | RuleApplied` table (table/csv/json via `--output`) and a stderr summary `(N stages, N parameters, N differ → N deployment rules inferred)`.
  - **MCP**: `daxter_pipeline_rules(pipelineId, model)` — same matrix as JSON, with stage-read failures listed as notes.
- **Pipeline scan results persist to disk** — `PipelineScanStore` now saves completed scans to
  `~/.daxter/pipeline-scans.json` (last 20) on the mounted volume, so container restarts don't lose them.
- **Recent audits sidebar** — new `AuditHistoryStore` tracks the last 10 ran audits in
  `~/.daxter/audit-history.json`. The Audit page sidebar shows them under **Recent audits** with
  one-click recall (clicking re-loads the persisted scan immediately), plus per-entry remove and clear.
- **New `/audit` page** — cross-cutting checks across pipelines/workspaces/models, with an
  **Audit type** picker so new audits drop in without rearranging the layout. Today: Pipeline
  deployment-rule audits (pick a pipeline → run "models without rules" / "parameter value check");
  more audit types (refresh status, measure dependencies, etc.) slot in next to it. Has its own
  **Frequent · Audit** sidebar tracking the most-used pipelines (each item links to
  `audit?pipeline=<id>`). Replaces the audit tabs that previously sat on the Pipelines page; the
  Pipelines page now links across to /audit and keeps the per-model deployment-rule board.
- **Pipeline-wide audits.** Below each model board, a new **Audits** card with two tabs:
  - **Models without rules** — scans every model in the pipeline and lists the ones whose parameter values are identical across every stage (no deployment rule or manual override in effect anywhere).
  - **Parameter value check** — pick a stage workspace, a parameter name, and an expected value (equals / does-not-equal); lists every model whose parameter matches in that stage. Useful for verifying a rule is consistently applied (e.g. all models in *Prod* have `WAREHOUSE_URL = <prod-endpoint>`).
  Both tabs share a single scan that streams progress (`N/Total`) and runs once per pipeline.
  Mirrored on the CLI (`daxter pipeline audit --pipeline X` with `--mode no-rules|check --stage Y --param Z --value V [--not-equals]`) and on the MCP (`daxter_pipeline_models_without_rules`, `daxter_pipeline_param_check`).

### Added
- **Saved param-checks (Audit page).** After running a Parameter-value check (e.g. `WAREHOUSE_URL = …` in
  a stage), a **★ Save this check** button stores it — with an editable, auto-suggested name
  (`WAREHOUSE_URL = <prod-endpoint> · Prod`) — to a new **Saved checks** group in the Frequent · Audit
  sidebar. Clicking a saved check deep-links back (`audit?pipeline=…&type=check&stage=&param=&value=&ne=`),
  selects the pipeline, and prefills the check. Saved via a **shared `Daxter.Core` store**
  (`~/.daxter/audit-saved.json`) so they survive restarts **and are usable from CLI + MCP**:
  - CLI: `daxter pipeline audit --list-saved` and `daxter pipeline audit --saved "<name>"`.
  - MCP: `daxter_audit_list_saved` and `daxter_audit_run_saved(name)`.
  Each has a remove × in the sidebar; legacy saved entries are migrated out of `audit-history.json`
  on first read.
- **Rule sets — ➕ Add rule & Run all.** The check's save button is now **➕ Add rule**, and after
  saving it clears the param/value (keeps the stage) so you can stack several rules quickly (like
  Power BI's "+ Add rule"). A **▶ Run all rules** button evaluates every saved rule for the pipeline
  against the current scan in one table — **Rule · Param · Stage · Expected · Compliant/Checked · ⚠ Violations**
  (expand a row to see the offending models). Same across surfaces: CLI
  `daxter pipeline audit --run-all-saved --pipeline X` and MCP `daxter_audit_run_all_saved(pipelineId)`,
  all sharing the one `Daxter.Core.PipelineRulesService.EvaluateRule`.
- **Audits scope to a single model (or all).** Every audit can target one model instead of the whole
  pipeline — much cheaper (one model's stages, read in parallel). `Daxter.Core.ScanModelAsync` produces
  a one-model scan that the same evaluators run on. Available everywhere:
  - **MCP:** `daxter_pipeline_param_check(…, model: "Sales")` (reports the model's actual value + pass/fail)
    and `daxter_audit_run_all_saved(pipelineId, model: "Sales")` ("run all audits for Sales").
  - **CLI:** `daxter pipeline audit --model Sales …` scopes `--mode check` / `--run-all-saved` to that model.
  - **Web:** a **Model** box on the Audit page narrows the param-check + Run-all to one model.

### Fixed
- **DAX Query Fields pane didn't populate on first load** — it only worked after closing and
  re-opening it. `LoadObjects` now calls `StateHasChanged()` on completion (Blazor doesn't
  auto-render after an `OnAfterRenderAsync` await), so the tree paints the first time. The Fields
  pane now also starts **hidden by default** (the model still loads in the background so editor
  autocomplete works whether or not the pane is open).
- **"Run →" / "Top N rows" / Recent-query hand-off to the DAX Query page.** Prerendering rendered
  the target page twice in two DI scopes; the throwaway prerender pass consumed the handed-off
  query, so the DAX Query page opened empty (showing its placeholder). Prerendering is now disabled,
  so pages initialize once on the live circuit and the query arrives correctly.

### Changed
- **New "Slate & Teal" theme** — re-skinned to an enterprise-analytics look: navy chrome topbar
  (`--chrome:#1b2533`) with muted→bright nav states, a teal interactive accent (`--accent:#0f766e`),
  cool slate neutrals, lighter borders, and a bit more depth. Because the stylesheet was already
  fully tokenized, the whole re-theme was ~20 `:root` token-value edits + one topbar rule — every
  page, card, grid, and badge picked it up automatically. Frozen into `.claude/ui-contract.md` as
  the project's design contract; `ui-consistency` enforces it. (Established via the `ui-consistency`
  ESTABLISH flow.)
- **UI professional pass + theme tokenization.** Promoted the palette to a full CSS-variable token
  set (text / surface / line / link / status / brand, plus `--radius*`, `--shadow*`, `--ring`) and
  replaced ~60 hardcoded hex values with `var(--…)` — re-theming is now editing a handful of tokens,
  not hunting the stylesheet. Added subtle depth (card/topbar/grid shadows), button hover states, and
  **keyboard focus rings** (`:focus-visible`) for accessibility — no layout change. Added the standard
  **← Back** button to the **Refresh** and **Audit** pages (both are reached via deep-link from other
  pages, per the UI contract). Verified with the `ui-consistency` skill: theme-drift and the two
  missing back-buttons were the only gaps; both now closed.
- **Measure "Run →" now loads the definition.** Instead of a bare `EVALUATE ROW("M", [M])`
  reference, it opens the DAX Query page with a runnable `DEFINE MEASURE 'Table'[M] = <definition>
  EVALUATE ROW("M", [M])` block — so the measure's actual logic is visible and editable. The home
  table is resolved via a TableID→table join.
- **Measures now appear in the Frequent · Explore sidebar.** Opening or running a measure records
  it; the most-used show under a new **Measures** group (click to open its definition).
- **Measures view is now a searchable grid** (Explore → a dataset → **Measures**): a search box
  (matches name, display folder, or definition text) over a **Measure | Definition | Actions**
  table. The **Definition** shows the DAX inline, collapsed with a fade and **click-to-expand**;
  **Run →** runs the measure on the DAX Query page; **Export CSV** downloads the listed measures
  + definitions (respects the current search filter). (Was a click-through list of names.)
- **Explore split into two pages.** The DAX editor moved out of the Explore tabs into its own
  **DAX Query** page (`/query`); **Explore** (`/explore`) is now purely the drill-down browser.
  "Run DAX →", "Top N rows", "Run measure", and clicking a **Recent query** in the sidebar all
  open the DAX Query page **pre-filled** (and run, where applicable) — carried across via the
  shared `ExploreActions` bus. The Recent-queries sidebar + Frequent shortcuts show on both pages.
  The DAX Query page has a **← Back** button that returns to the previous page (e.g. the Explore view you came from).
- **Configure moved to a ⚙ gear icon** in the top-right corner (out of the main nav), so the
  app's own settings aren't confused with a workspace/dataset's configuration. The gear **toggles**
  — a second click returns to the previous view.

## [1.6.8] - 2026-05-30

### Added
- **DAX autocomplete + signature help.** The query editor now suggests as you type (≥2 chars):
  the model's **tables, columns, and measures** (from the loaded Fields tree) plus ~130 **DAX
  functions** and keywords. Arrow keys to navigate, Enter/Tab to accept, Esc to dismiss; the
  chosen reference is inserted correctly (`'Table'[Column]`, `[Measure]`, `FUNCTION(`).
  Inside a function call, a **signature hint** shows its arguments with the **current argument
  highlighted** (e.g. `CALCULATE(Expression, **Filter1**, Filter2, …)`), and field suggestions
  are **biased to what that argument expects** (a `Table` arg surfaces tables first, a `Column`
  arg surfaces columns, an `Expression`/`Value` arg surfaces measures + columns). The signature
  hint also shows a **one-line description** of the function and a **↗ docs** link to Microsoft
  Learn (and each function suggestion in the dropdown has its own ↗ docs link). Self-contained —
  no external editor/CDN dependency (a small `dax-complete.js` over the textarea).
- The **Fields tree loads automatically** when you open the DAX tab / pick a dataset — no more
  "Load fields" button.
- **Taller DAX editor**, and **Recent queries moved to the left sidebar** (under
  *Frequent · Explore*) — freeing the workbench width. Clicking a recent query still runs it on
  the DAX tab (via a small `ExploreActions` bus); remove/clear are in the sidebar.
- **DAX syntax highlighting.** The query editor now color-codes **functions**, **keywords**,
  **strings**, **'table'** and **[column/measure]** references, **numbers**, and **comments** — via
  a highlighted overlay behind a transparent textarea, painted client-side as you type (no server
  round-trip), kept in sync after Format / field insertion. Self-contained (no editor library).
- **Format button + live validation** in the DAX editor. **⌗ Format** pretty-prints the query
  (indents by call depth, one argument per line, keeps simple calls inline, uppercases
  functions/keywords) — done **locally**, the query is never sent anywhere. A validation strip
  flags **unbalanced parentheses / brackets / quotes** and a missing `EVALUATE`/`DEFINE` as you
  type (the exact class of error that an unclosed `[` causes).

### Fixed
- **Interrupted jobs can be resumed or dismissed.** A job that was queued/running when the
  container was bounced comes back as **Interrupted** — but there was no way to clear or re-run
  it. Now each finished job (interrupted/failed/canceled/succeeded) has **↻ Resume** (re-runs the
  same spec, re-checking the write gates) and **×** (remove from the list), on both the Jobs page
  and the Refresh-page tracker. **Clear finished** now also removes interrupted jobs.
- **Frequent sidebar disambiguates same-named tables/datasets.** Standardized models often reuse
  table names across datasets, so the Frequent list couldn't tell two `FACT - Sales` apart. Each
  table now shows its **dataset** as a subtitle (and each dataset its **workspace**), plus a hover
  **tooltip** with the full context (e.g. *"FACT - Sales — in dataset 'Sales Brand' (workspace
  'Sales Analytics - Dev')"*).

## [1.6.7] - 2026-05-30

### Added
- **CLI parity with the web Refresh page + Fields tree** (matching the MCP additions in 1.6.6):
  - `daxter refresh partition --table T --partition P` — refresh **one** partition.
  - `daxter refresh partitions --table T [--partitions A,B,C] [--order …]` — refresh **all** or a
    **subset** of a table's partitions, now processed **in order** (TMSL `Sequence`,
    `maxParallelism: 1`). `--type` covers full/automatic/**calculate**/dataOnly/clearValues.
  - `daxter model columns [--table T]` — list a model's columns (name, hidden).

## [1.6.6] - 2026-05-30

### Added
- **MCP parity with the web Refresh page + Fields tree.**
  - `daxter_refresh` now supports **`scope=partition`** (refresh one named partition) and a
    **`partitions`** argument (comma-separated subset) for `scope=partitions`; all partition
    refreshes are wrapped in a TMSL `Sequence` with `maxParallelism: 1` so they process **in
    order** (a plain multi-partition refresh runs in parallel). `type` already covered
    full/automatic/**calculate**/dataOnly/clearValues.
  - New **`daxter_columns`** tool — list a model's columns (name, hidden), optionally for one
    table (excludes the internal RowNumber column). Backed by `ModelMetadataService.Columns`.
  - **27 MCP tools** total (`daxter_login` + 24 read + 2 gated write).

## [1.6.5] - 2026-05-30

### Added
- **DAX tab is now a Power BI Desktop-style workbench.** Three panes: a **Fields** tree on the
  **left** (every non-hidden table → its columns and measures, searchable; click a field to
  **insert its reference** — `'Table'[Column]` or `[Measure]` — into the query), the **query +
  results** in the center, and **Recent queries** on the **right**. The Fields and Recent panes
  are **collapsible** (×, with one-click reopen) and the query editor is full-width monospace.
  The fields load when you open the DAX tab (or pick a dataset). Backed by a new
  `DaxterUi.ModelTreeAsync` (TMSCHEMA DMVs).
- **Console uses the full window width.** The content area no longer caps at 1000 px and centers
  (which wasted most of a wide screen and squeezed the DAX results); it now fills the space next
  to the sidebar, so grids and the DAX workbench are actually usable. Reading-width text still
  caps for legibility.
- **One-click "run it" actions in Explore.** Drill into a **table** → **Top 10 rows →** /
  **Top 100 rows →** buttons jump to the DAX tab pre-filled with `EVALUATE TOPN(n, 'Table')`
  and run it. **Measures** are now a **clickable list** that drills into a **measure detail**
  showing its **DAX definition** (expression + display folder) with a **Run measure →** button
  (`EVALUATE ROW("Measure", [Measure])`) — so you can both **read** and **run** it. Table/measure
  names are DAX-escaped; the measure detail joins the Browse back-stack.
- **Per-partition progress for "all / pick partitions" refreshes.** These jobs now refresh each
  partition as its own step, so the Jobs page shows **"Partition X / N: <name>"**, a progress bar
  that advances per completed partition, and an **activity log with the time each partition took**
  (e.g. `[2/7] ✓ 2026Q1 — 18s`). Cancel stops cleanly between partitions. (Trade-off: each
  partition is processed with the chosen type, so a `Full` of N partitions recalculates N times —
  in exchange for the live visibility.)

## [1.6.4] - 2026-05-29

### Fixed
- **Workspace/Dataset pickers couldn't show other values once selected.** A native `<datalist>`
  filters its suggestions to substrings of the current input, so a full value hid every other
  option. Each picker now has a **clear (×) button** (one click empties it and shows the full
  list again — clearing the workspace also resets the dataset) and **selects its text on focus**
  so you can immediately type a new search.

### Added
- **Dedicated Refresh page** (`/refresh`). Pick a workspace + dataset, then choose **what to
  refresh** — the **full model**, a **table**, or **partitions** (all **in order**
  newest→oldest / oldest→newest, or a **single** partition) — a **processing type** (Full /
  Automatic / **Calculate** / Data only / Clear values) — and **confirm** before it runs. The
  partition options are **table-aware**: a table with one partition just refreshes it (no
  "in order" choice); the order options appear only when a table has multiple partitions.
  Partitions can be refreshed three ways: **all** (in order), or **Pick partitions…** to
  check **one or more** specific partitions (select-all / clear helpers). Whichever you choose,
  the page shows the **exact ordered list** of partitions to process (the confirmation repeats
  it), and the refresh **truly processes them in that order** — the TMSL is wrapped in a
  `Sequence` with `maxParallelism: 1` (a plain refresh runs partitions in parallel).
  `MaintenanceService.BuildPartitionsRefresh` gained an optional `maxParallelism` and an
  explicit-partition-set overload.
- **Explore → ↻ Refresh** now carries only the **workspace + dataset** to the Refresh page (you
  pick the scope there); it no longer preselects a specific table.
- **Jobs survive a redeploy.** The job list is persisted to `~/.daxter/jobs.json` on the mounted
  volume, so restarting/upgrading the container keeps the refresh history. Jobs that were
  queued or running when the container stopped are reconciled to **Interrupted** on load (a
  write is never silently auto-resumed). Duration history (for ETAs) was already persisted.
  The page is **tabbed** — **New refresh** and **History**: picking a workspace+dataset loads
  the dataset's **refresh history** (read-only — works even for PROD or with writes off).
  The Jobs page now offers a **← Back to Refresh** link (keeping the workspace/dataset) when you
  arrive from a per-dataset "All jobs →" link, so you don't have to re-pick them.
  A **"Refreshing…"** banner shows while a refresh is in progress for the dataset, and a
  per-dataset job tracker (plus the **Jobs** page) lets you **cancel** and **see details**
  (status, duration, target, error). Refreshes deep-link from Explore (a dataset or table →
  **↻ Refresh…**).
- **Refreshes run as background jobs.** A single-worker `JobService` runs them **one at a time
  in launch order** (so partitions process in sequence), reusing Core's `MaintenanceService`
  (TMSL). Gated exactly like the MCP/CLI: only with **Allow writes** on, and **always refused
  for PROD** targets. **Cancellation** skips queued jobs and aborts a running refresh (closes
  its connection).
- **Jobs page shows progress, an activity log, and an ETA.** Each job has a live **Step**
  (Queued → Started → Connecting → Processing → Completed/Failed), a click-to-expand
  **activity-log timeline**, and — once a similar refresh has run before — an **estimated
  duration** from history (`~Ns est.`) with a progress bar (elapsed vs estimate, updated every
  second). Durations are remembered per kind+dataset+table in a persisted `JobHistoryStore`
  (`~/.daxter/job-history.json`). The **nav shows `Jobs (N)`** while N are active.
- **Browse is now the default Explore tab** (and listed first); the workspace list loads on
  arrival so you land straight in the explorer.
- **Back button in Browse.** The drill-down explorer now has a **← Back** that returns to the
  exact previous view — from a table's M code back to the full **list of tables**, from a command
  result back to the dataset's commands, and so on up the chain (browser-style history, not just
  the breadcrumb's level jumps). Disabled at the top; reset when you deep-link in from the
  Frequent sidebar.
- **DAX query history.** Successful queries are remembered and listed as **Recent queries**
  under the query box on the Explore → DAX query tab. Click one to **re-run it** (re-applies
  its workspace/dataset + DAX); each row shows a single-line preview, its dataset, and how many
  times you've run it. Remove individual entries (×) or **clear all**. Persisted to
  `~/.daxter/query-history.json` on the mounted volume (de-duped, newest-first, last 100).
- **"Frequent" sidebar — per page.** A left rail lists your **most-used workspaces, datasets,
  and tables** (by open count). It's **scoped to the current page**: Explore has its own
  Frequent set and Refresh has its own, and clicking an item deep-links into *that* page at the
  right level (a table even opens to its M code on Explore, or preselected on Refresh). Usage is
  tracked per page-context in a persisted `UsageStore` (`~/.daxter/usage.json` on the mounted
  volume) and only records targets that actually resolved, so typos don't pollute the list.
- **Version awareness + update check.** The image is stamped with its version at build time
  (`DAXTER_VERSION`, from the git tag); the web console shows it in the header, and the Status
  page has a **Version & updates** card that checks GitHub `releases/latest` on demand. If a
  newer release exists it shows the release link and **volume-preserving upgrade commands**
  (your sign-in + Configure settings are kept). Outbound-only, on click — no telemetry, no
  background polling. Fork-friendly via `DAXTER_REPO`.

### Changed
- **Docs lead with the web console for sign-in.** README and `SETUP.md` now present
  `daxter web` → **Sign in** as the easiest first-run path (the device-code dance moves to a
  one-click page), and document an **Upgrade** flow that reuses the `daxter-tokens` volume so
  config and tokens survive. Clarified that the volume — not the image — holds all state.

## [1.6.3] - 2026-05-29

### Security
- **Logs are redacted.** Every line captured by the in-app log passes through a tested
  `SecretRedactor` that masks JWT/OAuth access tokens, `Bearer` headers, and keyed secrets
  (`password=`, `client_secret=`, `access_token=`, `api_key=`, …). DAXter already injects tokens
  out-of-band (never in connection strings) and never logs DAX text, cell values, or the client
  secret — the redactor is a defense-in-depth net so a third-party exception message can't leak
  one either. (14 redactor tests.)

### Added
- **Logs page** in the web console — recent console activity in-app: every operation with its
  row count and timing (e.g. `datasets [Sales Analytics] → 94 rows in 887 ms`), sign-in, health checks,
  and errors. Level filter (All / Information+ / Warning+ / Error), auto-refresh (2 s), and
  Clear. Backed by a bounded in-memory `LogSink` (last 1000 entries, `DAXTER_LOG_BUFFER` to
  change) fed by an `ILogger` provider, so the same lines also go to the container console
  (`docker logs`). `DaxterUi` now logs each operation's start/result/failure.

### Fixed
- **Device-code sign-in no longer fails with `AADSTS7000218 / invalid_client`.** When an env had a
  service-principal `DAXTER_CLIENT_ID` (+secret) *and* used device-code auth (the default), the
  device-code flow reused that **confidential** app id as a public client — so Entra demanded a
  client secret and sign-in failed. Device-code now always uses a **public** client:
  `DefaultPublicClientId`, or a new optional `DAXTER_PUBLIC_CLIENT_ID` for your own native app.
  `DAXTER_CLIENT_ID` is now strictly the service-principal (confidential) id.
- **Sign-in page is now click-to-open.** The device-code prompt renders the verification URL as a
  **clickable link** (opens in a new tab) and the user code in a large, **one-click-copy** field —
  no more selecting/copying out of a text blob. (`DeviceLogin` now carries the structured
  `VerificationUrl`/`UserCode` from MSAL, not just the message.)
- **Confirmed: saving the configuration does not sign you out.** `Save()` only writes
  `~/.daxter/console-config.json`; the MSAL token cache (`~/.daxter/msal_cache.bin`) is a separate
  file that nothing deletes. (Changing identity fields — auth mode, tenant, client id — can make
  the *silent* token lookup miss because MSAL keys its cache by client+tenant, but the cached
  token is still on disk; the sign-in link above recovers it.)
- **Web sign-in now completes reliably.** The device-code flow returned the code but swallowed
  the background result, so after you entered the code on the page **nothing happened** (and a
  failed sign-in was invisible). The Status page now **awaits sign-in completion**, auto
  re-checks health when you finish authenticating, and **shows the reason** if sign-in fails.
  `MsalTokenProvider.StartDeviceLoginAsync` returns a `DeviceLogin(Message, Completion)`.
- **Auth errors are now actionable.** When an operation fails because the session ended (e.g.
  after changing the configuration), the error renders a friendly banner with a **link to the
  Status page** to sign in again — instead of the raw MCP-flavored "use the daxter_login tool"
  text. New shared `ErrorBanner` component, used by the result grid and the Browse explorer.

### Added
- Explore page: the **Workspace and Dataset pickers are now searchable** (type-to-filter via
  an input + `<datalist>`), which matters with ~dozens of workspaces.
- Explore is now **tabbed** — **DAX query** and **Browse**. Browse is a **drill-down explorer**:
  start from zero, click **Workspaces** → a workspace → **Datasets** → a dataset → run any model
  command (tables, measures, parameters, partitions, RLS, datasources, permissions, refresh
  history) or workspace command (reports, lineage), with **breadcrumb** navigation back up. A
  **Run DAX →** button jumps to the query tab with the drilled-into workspace/dataset filled in.
- Browse drill-down now extends to **tables**: **Tables** lists the model's tables as a clickable
  list; clicking a table shows its **Power Query (M) code** (rendered as a formatted code block)
  and its **partitions**, with a fourth breadcrumb level (`Workspaces › ws › ds › table`).
- A **"Load default"** link appears when the server has a default workspace/dataset
  (`DAXTER_WORKSPACE`/`DAXTER_DATASET`) — one click fills them in.
- **Editable Configure page** — change auth mode, tenant, default workspace/dataset, prod
  workspaces, and allow-writes; edits apply to the console immediately and **Save** persists
  them to the mounted `~/.daxter` volume (survives restarts). Backed by a `ConfigState`
  service the console uses for all operations; the env file / Claude Desktop snippet preview
  reflects the edits.

## [1.6.2] - 2026-05-29

### Added
- Result grid now has **pagination** (50 rows/page, First/Prev/Next/Last) and an
  **Export CSV** button.

### Fixed
- Explore page always showed "error" and never rendered results — the `ResultGrid`'s string
  `Error` parameter was bound as a literal (`Error="error"`) instead of the field
  (`Error="@error"`), so it was never null. Now successful queries render the grid and real
  errors show their message.

## [1.6.1] - 2026-05-29

### Fixed
- Web console pages are now **interactive** — the `--empty` Blazor template didn't apply a
  render mode, so buttons/dropdowns did nothing (Explore appeared broken). Applied global
  `InteractiveServer` render mode. Also cleaned up nullability warnings in the Explore page.

## [1.6.0] - 2026-05-29

### Added
- **Web console** — `daxter web` serves a local Blazor UI
  (`docker run -p 8080:8080 --env-file daxter.env daxter:latest web` → http://localhost:8080):
  - **Status / health** — config summary, sign-in/token + connectivity checks, with a
    device-code **Sign in** button.
  - **Explore** — workspaces → datasets → tables / measures, a DAX query box → results grid,
    refresh history.
  - **Configure** — view the current config and generate an env file + the Claude Desktop MCP
    entry to copy.
  Reuses `Daxter.Core` (same logic as CLI/MCP); the runtime image moved to `aspnet:8.0` to
  host it.

## [1.5.1] - 2026-05-29

### Fixed
- MCP tools now surface user-actionable messages (e.g. *"Not signed in — use the daxter_login
  tool"*) instead of the SDK's generic *"An error occurred invoking …"* — `DaxterException`
  text was being hidden, which would have stalled the device-code onboarding. `daxter_login`
  under a service principal returns a clear "no interactive sign-in needed" message.

## [1.5.0] - 2026-05-29

### Added
- **Sign-in-as-yourself onboarding (MCP).** New **`daxter_login`** tool returns the
  device-code URL + code as the tool result (shown in chat) and finishes auth in the
  background — no terminal needed. Normal tools no longer block on an interactive prompt:
  if not signed in they return a clear "use the daxter_login tool" message.
- Tenant-level tools (`daxter_workspaces`, `daxter_gateways`, `daxter_pipelines`,
  `daxter_pipeline_stages`/`_operations`) now run with **no workspace configured**, so you
  can sign in and then pick a workspace (`DaxterConfig.FromEnvironment(requireWorkspace: false)`).
- **26 MCP tools**. `SETUP.md` now defaults to device-code (your own login); service
  principal is documented as the automation alternative.

## [1.4.0] - 2026-05-29

### Added
- **Datasource connection details** — `ws datasources` / `daxter_datasources` now return
  `server`, `database`, `path`, `url` (e.g. the Snowflake account + warehouse), not just the
  gateway id.
- **`DAXTER_PROD_WORKSPACES`** — comma-separated workspace names treated as production by
  the write guard, for tenants whose prod workspaces are unsuffixed (e.g. `Sales Analytics`).
  Prod detection is now centralized in `DaxterConfig.IsProductionTarget()` (env=prod, name
  contains "prod", or listed) and shared by the CLI and MCP write paths.

## [1.3.0] - 2026-05-29

### Added
- **MCP ↔ CLI parity** — 8 more MCP tools so the server exposes the full client surface:
  `daxter_export`, `daxter_permissions`, `daxter_gateways`, `daxter_datasources`,
  `daxter_test_rls`, `daxter_pipelines`, `daxter_pipeline_stages`,
  `daxter_pipeline_operations`. 25 tools total (23 read + 2 gated write).

## [1.2.0] - 2026-05-29

### Added
- **Gated MCP write tools** — `daxter_refresh` (model/table/partitions) and
  `daxter_clear_cache`, **dry-run by default**. They execute only when `execute=true`
  **and** the server sets `DAXTER_MCP_ALLOW_WRITES=true`; PROD-looking targets are always
  refused. Safe for autonomous use by default.
- MCP tool-registration tests (reflection over `DaxterTools`).

## [1.1.0] - 2026-05-29

### Added
- **MCP server** — the image doubles as a Model Context Protocol server (`daxter mcp`,
  stdio). 15 read-only tools (`daxter_query`, `daxter_dmv`, `daxter_list_tables`,
  `daxter_measures`, `daxter_measure`, `daxter_mcode`, `daxter_parameters`,
  `daxter_partitions`, `daxter_rls`, `daxter_diff_measures`, `daxter_refresh_history`,
  `daxter_workspaces`, `daxter_datasets`, `daxter_reports`, `daxter_lineage`) with
  optional `workspace`/`dataset` args, JSON output capped to 1,000 rows. Reuses 100% of
  `Daxter.Core`. Logging routed to stderr to keep stdout a clean JSON-RPC channel.

## [1.0.0] - 2026-05-29

First public release — a cross-platform Power BI Service CLI covering query, model
metadata, maintenance, inventory, RLS testing, and deployment pipelines.

### Added
- **Query module** — `query` (DAX/MDX), `dmv` ($SYSTEM), `ls` (tables); output as
  table, CSV, or JSON; query text inline or from a file.
- **Model module** — `model measures` / `measure` / `mcode` / `parameters` /
  `partitions` (with last-refresh times) / `rls` (roles, filters, members) /
  `export` (.bim via TOM) / `diff` (measure differences between two models).
- **Ops module** — `refresh model` / `table` / `partitions` (newest-first, TMSL),
  `refresh trigger` (REST), `refresh history` (REST), `cache clear` (XMLA). Mutating
  ops require `--yes`, support `--dry-run`, and refuse PROD-looking targets without
  `--force`.
- **Workspace module** (REST) — `ws ls` (workspaces + group ids), `ws datasets`,
  `ws reports`, `ws lineage` (report → dataset), `ws permissions` (workspace or
  `--dataset`), `ws gateways`, `ws datasources`.
- **Test module** — `test-rls --role <r> --user <upn> [-q DAX]` runs a query under an
  RLS role / impersonated identity (XMLA `Roles` + `EffectiveUserName`). Requires the
  connecting identity to be a workspace/model admin.
- **Pipeline module** (REST) — `pipeline ls` / `stages` (env → workspace mapping) /
  `operations` (deployment history).
- **Foundations** — environment profiles (`--env` / `DAXTER_ENV` →
  `DAXTER_WORKSPACE_<ENV>`), `env ls`; service-principal and device-code auth via MSAL
  with a persisted token cache.
- **Distribution** — multi-stage Docker image (tests gate the build), non-root runtime,
  `bin/daxter` wrapper, `Makefile` (image/test/save/load).
- **Docs** — README, architecture, product plan, roadmap.

### Security
- OAuth token injection (no passwords in connection strings); secrets via `.env`
  (gitignored), never in the image; non-root container.

## Planned (post-1.0)

- TMDL folder export (in addition to `.bim`).
- Deployment-pipeline parameter/datasource *rules* detail (beyond stages).
- Tenant-wide inventory & audit (Scanner/Admin API) — needs a Fabric admin identity.
- Model editing (create/alter/delete measures, tables, roles) — write, opt-in.
