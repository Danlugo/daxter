# CLAUDE.md — working in this repo

Context for AI agents (and humans) developing **DAXter**. For *using* the tool, see
[`README.md`](README.md); for *installing* it on a new machine, see [`SETUP.md`](SETUP.md).

## What it is

A cross-platform CLI + MCP server for the **Power BI Service**: query (DAX/MDX/DMV),
inspect model metadata, run maintenance (refresh/cache), and read workspace inventory —
over **XMLA** (ADOMD/TOM) and the **Power BI REST API**. Shipped as a single Docker image.
Auth is an Entra ID token (MSAL) injected into the connection — the supported way to reach
Power BI from macOS/Linux (interactive ADOMD login is Windows-only).

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

Recurring workflows are packaged as composable skills. Use them instead of improvising:

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
  Auth/             # MSAL token provider, XmlaAccessToken, ITokenProvider
  Configuration/    # DaxterConfig (env + profiles + IsProductionTarget)
  Connection/       # XmlaConnectionString, Adomd session + factory (roles/EUN for RLS)
  Query/  Metadata/ # QueryResult; ModelMetadataService, ModelDiffService
  Maintenance/      # MaintenanceService (TMSL refresh / ClearCache)
  Export/           # ModelExportService (TOM → .bim)
  Rest/             # PowerBiRestClient (workspaces, datasets, reports, lineage,
                    #   permissions, gateways, datasources, pipelines, refresh)
  Formatting/       # table (Spectre) / csv / json formatters
src/Daxter.Cli/     # System.CommandLine entry point; Mcp/ = MCP server + tools
tests/              # xUnit (75+); fakes for IXmlaSession; env tests serialized
Dockerfile          # multi-stage: sdk build+test → slim non-root runtime
.github/workflows/  # CI: test → docker build → publish to GHCR on v* tags (3× retry)
```

## How the pieces connect

`Daxter.Cli` is a thin shell over `Daxter.Core`. The MCP server (`Mcp/DaxterTools.cs`,
discovered via `[McpServerTool]`) is **another shell over the same Core** — keep CLI and MCP
at parity (25 MCP tools today). Shared logic (prod detection, token provider) lives in Core
or shared CLI helpers, not duplicated.

## Conventions

- **Commits:** Conventional Commits (`feat:`, `fix:`, `docs:`, `test:`, `ci:`, `refactor:`).
- **Releases:** tag `vMAJOR.MINOR.PATCH`; pushing the tag publishes
  `ghcr.io/danlugo/daxter:<tag>` + `:latest`. Update `CHANGELOG.md` first.
- **Docs to keep current on change:** `README.md`, `examples/cli.md`, `examples/mcp.md`,
  `CHANGELOG.md`, and `docs/` (`PRODUCT.md`, `ARCHITECTURE.md`, `ROADMAP.md`).

## Adding a capability (typical flow)

1. Add the method to the relevant Core service (or REST client) returning `QueryResult`.
2. Wire a CLI command in `Program.cs`; wire the matching MCP tool in `Mcp/DaxterTools.cs`
   (keep parity — the `McpToolsTests` reflection test enforces the tool set).
3. Add tests (use the fakes; no live endpoint). `make test` must pass.
4. Update `examples/*.md` + `CHANGELOG.md`. Tag a release if user-facing.
