# DAXter Architecture

## Overview

DAXter is a cross-platform CLI for the Power BI Service, shipped as a Docker image.
It is split into a UI-free engine (`Daxter.Core`) and a thin command shell
(`Daxter.Cli`), so the engine can be reused (service, GUI) without change.

```
                 ┌─────────────────────────┐
   user / CI  →  │   Daxter.Cli            │   System.CommandLine
                 │   commands, formatters  │   (table / csv / json)
                 └───────────┬─────────────┘
                             │ depends on (interfaces)
                 ┌───────────▼─────────────┐
                 │   Daxter.Core (engine)  │
                 │                         │
                 │  Auth       ──► MSAL ───┼──► Entra ID (OAuth token)
                 │  Connection ──► ADOMD ──┼──► XMLA endpoint (Power BI)
                 │  Metadata   ──► DMVs    │
                 │  Query      ──► DAX/MDX │
                 │  Formatting             │
                 └─────────────────────────┘
```

## Runtime topology — one image, many containers, one shared volume

One DAXter **instance per machine** does **not** mean one container. It means **one shared
volume** (`daxter-tokens`) + **exactly one long-running web container** (the worker + UI).
Every surface is the *same image* run in a different **mode** (`mcp` / `cli` / `web`); the
modes coordinate **only** through the file-backed queue on the shared volume — never through
shared process memory.

```
  CLIENTS (separate Claude programs / a browser)
  ┌───────────────┐ ┌───────────────┐ ┌───────────────┐ ┌──────────┐
  │ Claude MCP #1 │ │ Claude MCP #2 │ │  CLI command  │ │ Browser  │
  └───────┬───────┘ └───────┬───────┘ └───────┬───────┘ └────┬─────┘
          │ (1 per client)  │                 │ (1 per run)   │
          ▼                 ▼                 ▼               ▼
  CONTAINERS (each = one mode; all stamped from the Daxter Image)
  ┌──────────────┐  ┌──────────────┐  ┌──────────────┐  ┌─────────────────┐
  │ MCP cont. A  │  │ MCP cont. B  │  │ CLI cont.    │  │ WEB cont.       │
  │ --rm,no port │  │ --rm,no port │  │ --rm, exits  │  │ persistent,8080 │
  │ ENQUEUE only │  │ ENQUEUE only │  │ ENQUEUE only │  │ ★ WORKER + UI   │
  └──────┬───────┘  └──────┬───────┘  └──────┬───────┘  └────────┬────────┘
         └─────────────────┴─────────────────┴───────────────────┘
                                   │  all mount ▼
                 ┌─────────────────────────────────────────────┐
                 │  SHARED VOLUME  daxter-tokens (~/.daxter)    │  ◄── the "single instance"
                 │    • queue.json   the one job queue          │
                 │    • auth token   one sign-in for all        │
                 └─────────────────────────────────────────────┘
   (the Daxter Image is the template every container is stamped from —
    it sits "behind" the containers; it is not the coordination layer)
```

**How jobs stay consistent across containers** (`Daxter.Core/Scheduling/RefreshQueueStore.cs`):

- The queue is a **cross-process, file-backed** `queue.json` on the shared volume. Reads
  re-load from disk under an exclusive **lock file**; writes are temp-file + atomic replace.
- **MCP and CLI only `Enqueue`** (`JobOrigin.Mcp` / `Cli`) — they append a job and may exit.
  They hold **no** job state in memory.
- The **web container hosts the single worker** (`RefreshWorkerHostedService`) that drains the
  queue; its `JobService` is a *façade over the same `RefreshQueueStore`*, so the **Jobs UI
  shows every job** — CLI, MCP, and web alike. A `worker.heartbeat` file lets CLI/MCP warn
  when no web worker is alive to run what they enqueued.

**The two invariants that make "single instance per machine" hold:**

1. **One shared volume** — every container mounts `-v daxter-tokens:/home/daxter/.daxter`.
   (The `.mcpb` extension and the web `docker run` both do.)
2. **Exactly one web container** running the worker.

Multiple MCP/CLI containers are **harmless** — they are stateless front doors to the one
queue. The failure mode to avoid is a surface mounting a **different** volume (e.g. a stray
`*-tokens`): its `queue.json` would be invisible to the web worker. This is why **all
surfaces standardize on `daxter-tokens`**.

**Common misconceptions** (both are wrong):

| Wrong | Right |
|-------|-------|
| Several MCP clients share one MCP container | **One MCP container per client** (per-stdio process); N clients → N containers |
| The image is the shared/coordination layer | The image is just the **template**; the **shared volume** is the coordination layer |

## Why this shape

| Concern | Decision | Rationale |
|---------|----------|-----------|
| Auth on macOS/Linux | Acquire token via MSAL, inject `AccessToken` | ADOMD interactive login is Windows-only |
| Testability | Interfaces + fakes (`ITokenProvider`, `IXmlaSession`, `IXmlaSessionFactory`) | Unit tests run with no live endpoint |
| Distribution | Multi-stage Docker, tests in build stage | One portable artifact; a failing test fails the image |
| Output | `IResultFormatter` (table/csv/json) | stdout = data, stderr = status → clean piping |
| Config | Env vars + `--flags`, env profiles | 12-factor; one SP, workspace per environment |

## Key abstractions

- **`ITokenProvider`** → `MsalTokenProvider`. Device-code (cached) and service-principal
  (client-credentials) flows. The token is the seam that makes cross-platform work.
- **`IXmlaSession`** → `AdomdXmlaSession`. Executes a query, materializes a `QueryResult`.
  `IXmlaSessionFactory` builds the connection string, injects the token, opens it.
- **`ModelMetadataService`** → wraps a session and runs TMSCHEMA DMVs (measures, M code,
  partitions, RLS), resolving id→name joins in code.
- **`ResultFormatterFactory`** → `Table` (Spectre.Console), `Csv` (RFC 4180), `Json`.
- **`DaxterConfig`** → resolves config from `--flags` → `DAXTER_*_<ENV>` → `DAXTER_*`.

## Request flow (a `query`)

1. `Program` parses args (System.CommandLine), defers config/query reads into a try.
2. `DaxterConfig.FromEnvironment` resolves workspace/dataset/auth (env profile aware).
3. `MsalTokenProvider` acquires an Entra ID token (cached when possible).
4. `AdomdXmlaSessionFactory` builds `Data Source=powerbi://…`, injects `AccessToken`, opens.
5. `AdomdXmlaSession.Execute` runs the DAX/MDX/DMV, returns a `QueryResult`.
6. The chosen `IResultFormatter` renders to stdout; row count to stderr.

## Security

- OAuth 2.0 via MSAL; **no passwords in connection strings** — token injection only.
- Secrets come from env/`.env` (gitignored); never baked into the image.
- Container runs as a **non-root** user; token cache lives in a mounted volume.
- Errors are sanitized to `daxter: <message>` (no stack traces) for user-facing failures.

## Testing

- `Daxter.Core.Tests` (xUnit): connection-string building, formatters (incl. RFC-4180
  quoting), config/env-profile resolution, metadata DMV construction + RLS join, error
  paths. Env-mutating tests are serialized via a collection.
- The Docker build runs the full suite; the image is only produced if tests pass.
