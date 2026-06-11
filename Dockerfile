# syntax=docker/dockerfile:1
#
# DAXter — Mac/Linux XMLA query client for the Power BI Service.
# Multi-stage: the SDK stage restores, runs the test suite, and publishes;
# the runtime stage is a slim, non-root image containing only the app.

# ---- build + test + publish ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore first using only the project files so this layer is cached until a
# dependency changes. (SDK 8.0 cannot parse the .slnx solution, so restore by project.)
COPY src/Daxter.Core/Daxter.Core.csproj src/Daxter.Core/
COPY src/Daxter.Cli/Daxter.Cli.csproj src/Daxter.Cli/
COPY src/Daxter.Web/Daxter.Web.csproj src/Daxter.Web/
COPY tests/Daxter.Core.Tests/Daxter.Core.Tests.csproj tests/Daxter.Core.Tests/
RUN dotnet restore src/Daxter.Cli/Daxter.Cli.csproj \
    && dotnet restore src/Daxter.Web/Daxter.Web.csproj \
    && dotnet restore tests/Daxter.Core.Tests/Daxter.Core.Tests.csproj

# Build, test, then publish the CLI and the web console into the same /app.
COPY . .
# Tests run by default (local builds, `make image`, CI's test + Docker-build jobs). The multi-arch
# publish passes RUN_TESTS=0 to skip re-running them under slow arm64 emulation — they're already
# gated natively on amd64 by the `test` and `image` jobs, and the code is platform-agnostic.
ARG RUN_TESTS=1
RUN if [ "$RUN_TESTS" != "0" ]; then \
        dotnet test tests/Daxter.Core.Tests/Daxter.Core.Tests.csproj -c Release --no-restore; \
    fi
RUN dotnet publish src/Daxter.Cli/Daxter.Cli.csproj -c Release --no-restore -o /app
RUN dotnet publish src/Daxter.Web/Daxter.Web.csproj -c Release --no-restore -o /app

# ---- runtime ----  (aspnet base: hosts the web console; also runs the CLI/MCP)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Version stamped at build time (CI passes the git tag, e.g. v1.6.3); "dev" for local builds.
ARG DAXTER_VERSION=dev

# ICU + tzdata: Microsoft.Data.SqlClient REQUIRES real globalization data — calling
# SqlConnection.OpenAsync in Invariant Mode (the .NET 8 Linux runtime default) throws
# "Globalization Invariant Mode is not supported." The XMLA/REST stack didn't need this, but
# the Fabric SQL surface (FabricSqlClient) does. Adding ~30 MB to ship a working SQL endpoint.
RUN apt-get update \
    && apt-get install -y --no-install-recommends libicu72 tzdata \
    && rm -rf /var/lib/apt/lists/*

# Non-root user with a home directory for the MSAL token cache (~/.daxter).
RUN groupadd --system daxter \
    && useradd --system --gid daxter --create-home --home-dir /home/daxter daxter \
    && mkdir -p /home/daxter/.daxter \
    && chown -R daxter:daxter /home/daxter

COPY --from=build /app .

ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    HOME=/home/daxter \
    DAXTER_VERSION=$DAXTER_VERSION \
    # v1.40.0 — INSIDE the container the web console binds 0.0.0.0 so Docker's port-forward
    # reaches it. This is NOT the exposure control: HOST exposure is controlled by the `-p`
    # mapping — use `-p 127.0.0.1:8080:8080` to keep the console on host-localhost only (the
    # daxter-deploy skill does this). Bare-metal `daxter web` (no container) still defaults to
    # 127.0.0.1. Set DAXTER_WEB_BEARER_TOKEN to gate /api/* when exposing beyond localhost.
    DAXTER_WEB_BIND=0.0.0.0

USER daxter
EXPOSE 8080
ENTRYPOINT ["dotnet", "/app/daxter.dll"]
CMD ["--help"]
