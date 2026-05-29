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
