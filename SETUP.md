# DAXter — Setup for Claude Desktop

Goal: get the **`daxter` MCP server** working in **Claude Desktop** on a fresh machine.
This guide is written so a **Claude agent can follow it top to bottom** (it can run the
shell commands and edit the config file), or a person can do it manually.

> Replace `<...>` placeholders with real values. The image carries **no credentials** —
> you supply them at runtime via `.env`.

## 0. Prerequisites

- **Docker Desktop** installed and **running**.
- **git** and **python3** (python3 is used only for the safe config merge in step 3).
- A **Power BI service principal** with access to a Premium/PPU/Fabric workspace:
  tenant id, client id, client secret. (Tenant admin must enable *"Allow service
  principals to use Power BI APIs"*, and the SP must be a member of the workspace.)

## 1. Get the image

Build from source — works from the public repo, no registry auth:

```bash
git clone https://github.com/Danlugo/daxter
cd daxter
make image        # builds daxter:latest (runs the test suite inside the build)
```

Or pull the prebuilt image (only if the GHCR package is public; otherwise
`docker login ghcr.io` first):

```bash
docker pull ghcr.io/danlugo/daxter:latest
```

## 2. Create the `.env`

```bash
cp .env.example .env
```

Edit `.env` and set (service-principal is the right mode for an MCP server):

```ini
DAXTER_AUTH_MODE=service-principal
DAXTER_TENANT_ID=<tenant-guid>
DAXTER_CLIENT_ID=<app-client-id>
DAXTER_CLIENT_SECRET=<app-secret>
DAXTER_WORKSPACE=<default workspace name>
DAXTER_DATASET=<default model name>          # optional
# optional: protect prod when writes are enabled (unsuffixed prod workspaces)
# DAXTER_PROD_WORKSPACES=<Prod WS 1>,<Prod WS 2>
```

`.env` is gitignored — never commit it.

## 3. Configure Claude Desktop

Config file location:
- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`

Add a `daxter` server under `mcpServers`. **Merge — don't overwrite** existing servers.
The safe merge below makes a backup, preserves other servers, uses your real docker path
and an **absolute** `.env` path:

```bash
CFG="$HOME/Library/Application Support/Claude/claude_desktop_config.json"
mkdir -p "$(dirname "$CFG")"; [ -f "$CFG" ] && cp "$CFG" "$CFG.bak"
python3 - "$CFG" "$(command -v docker)" "$(pwd)/.env" <<'PY'
import json, os, sys
cfg, docker, envfile = sys.argv[1], sys.argv[2], sys.argv[3]
d = json.load(open(cfg)) if os.path.exists(cfg) and os.path.getsize(cfg) else {}
d.setdefault("mcpServers", {})["daxter"] = {
    "command": docker,
    "args": ["run", "-i", "--rm",
             "--env-file", envfile,
             "-v", "daxter-tokens:/home/daxter/.daxter",
             "daxter:latest", "mcp"],
}
json.dump(d, open(cfg, "w"), indent=2)
print("mcpServers:", list(d["mcpServers"]))
PY
```

> If you pulled from GHCR, replace `daxter:latest` with `ghcr.io/danlugo/daxter:latest`.

The equivalent JSON, if editing by hand:

```jsonc
"mcpServers": {
  "daxter": {
    "command": "/usr/local/bin/docker",
    "args": ["run", "-i", "--rm",
             "--env-file", "/ABSOLUTE/PATH/TO/daxter/.env",
             "-v", "daxter-tokens:/home/daxter/.daxter",
             "daxter:latest", "mcp"]
  }
}
```

## 4. Restart Claude Desktop

**Fully quit** (Cmd+Q on macOS / quit from the tray on Windows) and reopen — Claude reads
the MCP config only at launch. Docker must be running.

## 5. Verify

In a Claude Desktop chat, ask: **"List my Power BI workspaces"** → it should call
`daxter_workspaces`. Or open the tools / 🔌 menu (Settings → Developer) and confirm
`daxter` is connected. See [`examples/mcp.md`](examples/mcp.md) for prompts per tool.

## Multiple clients / environments

One `.env` per client (each with that client's SP), and one server entry per client with
its own token volume:

```jsonc
"daxter-acme":  { "command": "/usr/local/bin/docker", "args": ["run","-i","--rm","--env-file","/path/acme.env","-v","daxter-acme-tokens:/home/daxter/.daxter","daxter:latest","mcp"] },
"daxter-globex":{ "command": "/usr/local/bin/docker", "args": ["run","-i","--rm","--env-file","/path/globex.env","-v","daxter-globex-tokens:/home/daxter/.daxter","daxter:latest","mcp"] }
```

Within a client, target environments by naming the workspace per request (the name encodes
the env, e.g. `Sales - QA`; unsuffixed = prod).

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Tools don't appear | Docker running? Did you fully **quit & reopen** Desktop? Check Settings → Developer / MCP logs (the server's stderr). |
| `pull access denied` from GHCR | Package is private — **build from source** (step 1) or `docker login ghcr.io`. |
| Auth / connection errors | SP creds correct? Tenant setting *Allow service principals…* enabled? SP added to the workspace? Workspace on Premium/PPU/Fabric with XMLA enabled? |
| First call is slow | Cold start (container + token); subsequent calls are fast. |
| Wrong workspace queried | Name the workspace in your request, or set `DAXTER_WORKSPACE` / `DAXTER_ENV` in `.env`. |

## Agent checklist

- [ ] Docker running
- [ ] image available (`docker images | grep daxter` or pulled)
- [ ] `.env` created with SP creds + workspace
- [ ] `daxter` merged into `claude_desktop_config.json` (absolute paths, backup made)
- [ ] Claude Desktop fully restarted
- [ ] "List my Power BI workspaces" returns results
