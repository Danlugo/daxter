# Contributing to DAXter

Thanks for your interest! DAXter is developed **Docker-only** — you do not need a local
.NET SDK.

## Build & test

```bash
make image          # build daxter:latest (runs the full test suite in the build stage)
make test           # build only the test stage (fails on any test failure)
```

Fast inner loop without rebuilding the whole image (compiles/tests mounted source in an
SDK container, NuGet cached in a volume):

```bash
docker run --rm -v "$PWD":/src -w /src -v daxter-nuget:/root/.nuget/packages \
  mcr.microsoft.com/dotnet/sdk:8.0 \
  dotnet test tests/Daxter.Core.Tests/Daxter.Core.Tests.csproj -c Release
```

## Project layout

```
src/Daxter.Core/    # engine: auth, connection, query, metadata, formatting
src/Daxter.Cli/     # System.CommandLine entry point + command wiring
tests/              # xUnit unit tests (run inside the Docker build)
docs/               # architecture, product, roadmap
```

## Conventions

- **Style:** standard .NET conventions; nullable enabled; XML docs on public APIs.
- **Tests:** every change ships a test. Prefer `[Theory]` parametrization over copy-paste;
  cover error paths, not just the happy path. Tests must not hit a live endpoint — use the
  fakes (`FakeXmlaSession`, etc.).
- **Commits:** [Conventional Commits](https://www.conventionalcommits.org/) —
  `feat:`, `fix:`, `docs:`, `test:`, `refactor:`, `chore:`.
- **Output discipline:** results to **stdout**, status/errors to **stderr**.
- **Write safety:** any mutating operation must support `--dry-run` and `--yes`.

## Secrets & confidentiality

- Never commit `.env` or any credential. Configuration examples use generic placeholders.
- Do not include real workspace/dataset/tenant names in code, tests, or docs.

## Pull requests

1. Branch from `main`, keep changes focused.
2. `make image` must pass (build + tests green).
3. Update `CHANGELOG.md` (Unreleased) and any affected docs.
