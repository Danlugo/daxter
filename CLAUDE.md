# CLAUDE.md — working in this repo

Context for AI agents (and humans) developing **DAXter**. For *using* the tool, see
[`README.md`](README.md); for *installing* it on a new machine, see [`SETUP.md`](SETUP.md).

## What it is

A Docker-only **Power BI + Fabric** client with three surfaces — a CLI, an **MCP server**
(76 tools), and a **Blazor web console** (12 pages) — all sharing one engine (`Daxter.Core`).
Capabilities: DAX/MDX/DMV queries, model metadata, model editing (TOM), refresh (Enhanced
Refresh by default · file-backed queue · one shared worker), workspace inventory + REST
operations (lineage, gateways, permissions, take-over, bindConnection), pipelines (rules
inferred from per-stage parameter diffs + audit), Fabric SQL endpoints (object explorer
+ T-SQL + streaming CSV export), Fabric Copy Jobs and Notebooks (view + run + monitor),
RLS viewer with syntax-highlighted DAX, and a two-list workspace writes-gate with glob
patterns. Two MSAL tokens (Power BI scope + Fabric SQL scope) — one signed-in account
covers both audiences silently after the first call.

## Golden rules

- **Docker-only. Do not assume a local .NET SDK.** Build and test in containers.
- **Never commit secrets or client data.** `.env` is gitignored; the repo is **public** —
  use only generic placeholders (`Sales Analytics`, `Retail Model`, `Sales`) in code, tests,
  and docs. No real tenant/workspace/model names.
- **stdout = data, stderr = status/errors.** Critical for the MCP stdio channel and for
  piping. Never print results or logs to stdout outside the formatters.
- **Every change ships a test.** Prefer `[Theory]` over copy-paste; cover error paths.
- **Mutations are gated:** CLI `--dry-run`/`--yes`/`--force`; MCP writes need
  `DAXTER_MCP_ALLOW_WRITES=true` and refuse prod. Keep it that way.

## Skills (`.claude/skills/`)

Recurring workflows are packaged as composable skills. Use them instead of improvising.
(`.claude/` is gitignored — these live locally, not in the public repo.)

| Skill | When | Notes |
|-------|------|-------|
| `daxter-conventions` | reference (auto) | layering, bridge, store pattern, Razor gotchas — `user-invocable: false` |
| `daxter-build-verify` | after any change | runs `scripts/build-verify.sh` (test gate) |
| `daxter-deploy` | "rebuild & restart" / "release it" | `disable-model-invocation: true`; `rebuild-restart.sh`, `release.sh` |
| `daxter-new-feature` | add new functionality | composes conventions + build-verify |
| `daxter-update` | change existing behavior | refactor-to-Core when shared |
| `daxter-fix` | something's broken | starts from the Blazor-gotcha checklist |
| `daxter-ui` | layout/visual change | card/sidebar/Frequent patterns |
| `daxter-capability` | expose in CLI + MCP + Web | one Core method, three surfaces |

The deterministic build/restart/release commands live in scripts (same result, no token cost).

## Build / test / run (containers)

```bash
make image          # build daxter:latest — runs the full test suite inside the build
make test           # build only the test stage (fails on any test failure)

# fast inner loop (compile/test mounted source, NuGet cached in a volume):
docker run --rm -v "$PWD":/src -w /src -v daxter-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/Daxter.Core.Tests/Daxter.Core.Tests.csproj -c Release

# run the CLI / MCP from the built image:
docker run --rm --env-file .env daxter:latest --help
docker run -i --rm --env-file .env -v daxter-tokens:/home/daxter/.daxter daxter:latest mcp
```

Target framework is **net8.0** (LTS); projects roll forward to a newer runtime when net8
isn't installed.

## Layout

```
src/Daxter.Core/    # UI-free engine
  Auth/             # MsalTokenProvider (TWO client ids: Power BI scope + Fabric SQL scope),
                    #   ITokenProvider + IFabricSqlTokenProvider, XmlaAccessToken
  Configuration/    # DaxterConfig.FromEnvironment — single config source: explicit arg >
                    #   PersistedSettings (~/.daxter/console-config.json, written by the web
                    #   console) > env var > default. CLI + MCP + Web all resolve through it.
                    #   WorkspaceMatcher (glob: *, case-insensitive, anchored) feeds the
                    #   two-list writes-gate (ReadOnly deny / WriteWorkspaces allow).
  Connection/       # XmlaConnectionString, Adomd session + factory (roles/EUN for RLS)
  Query/  Metadata/ # QueryResult; ModelMetadataService, ModelDiffService
  Editing/          # ModelEditService — TOM staging, .bim backup, dry-run preview, apply
  Maintenance/      # MaintenanceService (TMSL refresh / ClearCache)
  Export/           # ModelExportService (TOM → .bim)
  Audit/            # SavedAuditCheckStore (saved param-checks, shared by CLI/MCP/Web)
  Rest/             # PowerBiRestClient — Power BI REST (groups/datasets/reports/lineage/
                    #   permissions/gateways/pipelines/refresh) + Fabric REST
                    #   (sqlEndpoints/copyJobs/notebooks/items+jobs/instances/
                    #    semanticModels:bindConnection/getDefinition).
                    #   PipelineRulesService (rules inferred from stage param diffs).
  Sql/              # FabricSqlClient — Microsoft.Data.SqlClient over SqlConnection.AccessToken
                    #   (database.windows.net audience). StreamCsvAsync writes RFC-4180 or
                    #   quote-all+CRLF straight to a TextWriter (no in-memory rows).
                    #   SqlWriteGate (read-only by default, opt-in writes).
  Scheduling/       # RefreshQueueStore (file-backed), EnhancedRefresh, RefreshScheduler.
  Formatting/       # table (Spectre) / csv / json formatters + CsvStyle for streaming exports
src/Daxter.Cli/     # System.CommandLine entry point; Mcp/ = MCP server + 76 tools
src/Daxter.Web/     # Blazor Server console — 12 pages (Home/Status, Explore, Query, Refresh,
                    #   Jobs, Gateways, Pipelines, Audit, Model Edit, Connections, SQL, RLS,
                    #   Copy Jobs, Notebooks, Configure, Logs). Components/ has shared widgets
                    #   (SearchableSelect, ResultGrid, FabricItemViewer, ErrorBanner).
                    #   Services/DaxterUi.cs is the bridge — every page calls it; it calls Core.
                    #   Endpoints/SqlExportEndpoint.cs streams CSV bypassing SignalR.
                    #   RefreshWorkerHostedService drains the shared queue.
tests/              # xUnit (270+); fakes for IXmlaSession + HTTP stubs for REST;
                    # env-mutating tests serialized via a collection. Covers WorkspaceMatcher,
                    # the read-only precedence ladder, capabilities classification (so a
                    # ReadOnly = true tool can't drift into the destructive bucket), CsvStyle,
                    # SqlWriteGate, scheduler resume, MCP login prompt formatting, every
                    # REST JSON parsing path (Fabric items, job instances, sqlEndpoints).
Dockerfile          # multi-stage: sdk build+test → slim non-root runtime with libicu72
                    # + tzdata (Microsoft.Data.SqlClient needs ICU; InvariantGlobalization=false)
.github/workflows/  # CI: test → docker build → publish MULTI-ARCH (amd64+arm64, buildx) to GHCR on v* tags
```

## How the pieces connect

`Daxter.Cli` is a thin shell over `Daxter.Core`. The MCP server (`Mcp/DaxterTools.cs`,
discovered via `[McpServerTool]` reflection) is **another shell over the same Core** —
**76 MCP tools** today (50 read, 26 gated writes). The Blazor Web project (`Daxter.Web`)
is a **third** shell — its `Services/DaxterUi.cs` bridge wraps the same Core methods for
the Razor pages. Keep all three at parity (the `daxter-capability` skill walks this) —
shared logic (read-only gate, token provider, scheduler queue) lives in Core or shared
helpers, not duplicated. `daxter_capabilities` reflects the tool list at runtime so agents
discover new features without out-of-band docs.

## Conventions

- **Commits:** Conventional Commits (`feat:`, `fix:`, `docs:`, `test:`, `ci:`, `refactor:`).
- **Releases:** tag `vMAJOR.MINOR.PATCH`; pushing the tag publishes
  `ghcr.io/danlugo/daxter:<tag>` + `:latest`. Update `CHANGELOG.md` first.
- **Docs to keep current on change:** `README.md`, `SETUP.md`, `examples/cli.md`, `examples/mcp.md`,
  `CHANGELOG.md`, and `docs/` (`PRODUCT.md`, `ARCHITECTURE.md`, `ROADMAP.md`).

## Adding a capability (typical flow)

1. Add the method to the relevant Core service (or REST client) returning `QueryResult`.
2. Wire a CLI command in `Program.cs`; wire the matching MCP tool in `Mcp/DaxterTools.cs`
   (keep parity — the `McpToolsTests` reflection test enforces the tool set).
3. Add tests (use the fakes; no live endpoint). `make test` must pass.
4. Update `examples/*.md` + `CHANGELOG.md`. Tag a release if user-facing.
