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

The image is **public on GHCR**, so you can pull it directly — no login, no build:

```bash
docker pull ghcr.io/danlugo/daxter:latest
```

You can even skip this: the MCP server's `docker run` (step 3) auto-pulls the image on
first use. Building from source is optional (`git clone … && make image`).

## 2. Create the env file

Create an env file anywhere private — e.g. `~/daxter.env` (the image carries no creds, so
you supply them here). Service-principal is the right mode for a headless MCP server:

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

(If you cloned the repo, `cp .env.example .env` gives you a starting template.)
Keep this file private and never commit it.

**Fill in real values now — before you touch Claude Desktop.** Format matters:

- **No angle brackets** — replace each `<...>` entirely, brackets included.
- **No surrounding quotes** and **no trailing spaces** on any value.
- `DAXTER_TENANT_ID` and `DAXTER_CLIENT_ID` must be **GUIDs**
  (e.g. `12345678-1234-1234-1234-123456789abc`).

A placeholder, empty, or malformed `DAXTER_TENANT_ID` is the #1 cause of setup failure — it
makes every tool call throw `The authority (including the tenant ID) must be in a
well-formed URI format`.

> **Editor gotcha:** do **not** edit this file with **Claude Desktop's built-in editor** —
> it refuses with *"This file can't be saved — its path is outside the session folder."*
> Use a normal editor (Notepad, VS Code, `nano`, …).

> **The container reads `--env-file` only once, at startup**, and Claude Desktop launches it
> once per session — so **any change to the env file takes effect only after a full
> quit-and-reopen of Claude Desktop** (step 4), not just saving the file.

## 3. Configure Claude Desktop

Config file location:
- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`

Add a `daxter` server under `mcpServers`. **Merge — don't overwrite** existing servers.
Set `ENVFILE` to the **absolute path** of your env file from step 2. The safe merge below
makes a backup, preserves other servers, and uses your real docker path:

```bash
CFG="$HOME/Library/Application Support/Claude/claude_desktop_config.json"
ENVFILE="$HOME/daxter.env"        # ← absolute path to your env file from step 2
mkdir -p "$(dirname "$CFG")"; [ -f "$CFG" ] && cp "$CFG" "$CFG.bak"
python3 - "$CFG" "$(command -v docker)" "$ENVFILE" <<'PY'
import json, os, sys
cfg, docker, envfile = sys.argv[1], sys.argv[2], sys.argv[3]
d = json.load(open(cfg)) if os.path.exists(cfg) and os.path.getsize(cfg) else {}
d.setdefault("mcpServers", {})["daxter"] = {
    "command": docker,
    "args": ["run", "-i", "--rm",
             "--env-file", envfile,
             "-v", "daxter-tokens:/home/daxter/.daxter",
             "ghcr.io/danlugo/daxter:latest", "mcp"],
}
json.dump(d, open(cfg, "w"), indent=2)
print("mcpServers:", list(d["mcpServers"]))
PY
```

The equivalent JSON, if editing by hand (use absolute paths):

```jsonc
"mcpServers": {
  "daxter": {
    "command": "/usr/local/bin/docker",
    "args": ["run", "-i", "--rm",
             "--env-file", "/ABSOLUTE/PATH/TO/daxter.env",
             "-v", "daxter-tokens:/home/daxter/.daxter",
             "ghcr.io/danlugo/daxter:latest", "mcp"]
  }
}
```

## 4. Restart Claude Desktop

> **Do not restart until `daxter.env` contains real values — placeholders cause an auth
> error.** Restarting while the file still has `<...>` / empty / malformed values makes
> every tool call fail with `The authority (including the tenant ID) must be in a
> well-formed URI format`.

**Fully quit and reopen.** *Fully quit* means **Cmd+Q** (macOS) or **right-click the tray
icon → Quit** (Windows) — **closing the window is not enough**. Claude reads the MCP config
and launches the env-file'd container only at startup, so this same full restart is required
**every time you change `daxter.env`**. Docker must be running.

## 5. Verify

In a Claude Desktop chat, ask: **"List my Power BI workspaces"** → it should call
`daxter_workspaces`. Or open the tools / 🔌 menu (Settings → Developer) and confirm
`daxter` is connected. See [`examples/mcp.md`](examples/mcp.md) for prompts per tool.

## Multiple clients / environments

One `.env` per client (each with that client's SP), and one server entry per client with
its own token volume:

```jsonc
"daxter-acme":  { "command": "/usr/local/bin/docker", "args": ["run","-i","--rm","--env-file","/path/acme.env","-v","daxter-acme-tokens:/home/daxter/.daxter","ghcr.io/danlugo/daxter:latest","mcp"] },
"daxter-globex":{ "command": "/usr/local/bin/docker", "args": ["run","-i","--rm","--env-file","/path/globex.env","-v","daxter-globex-tokens:/home/daxter/.daxter","ghcr.io/danlugo/daxter:latest","mcp"] }
```

Within a client, target environments by naming the workspace per request (the name encodes
the env, e.g. `Sales - QA`; unsuffixed = prod).

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| `The authority ... must be in a well-formed URI format` | `DAXTER_TENANT_ID` is still a placeholder, empty, or malformed — **or** you edited `daxter.env` without fully restarting Claude Desktop. Verify the values are real GUIDs/strings (no `<>`, quotes, or spaces) and **fully quit & reopen** the app. |
| Edited the env file but nothing changed | The container reads `--env-file` only at startup. Fully quit & reopen Claude Desktop (closing the window isn't enough). |
| `can't be saved — path is outside the session folder` | You're editing `daxter.env` in Claude Desktop's built-in editor. Use a normal editor (Notepad, VS Code, `nano`). |
| Tools don't appear | Docker running? Did you fully **quit & reopen** Desktop? Check Settings → Developer / MCP logs (the server's stderr). |
| `pull access denied` from GHCR | The package is public; this only appears if it was made private — `docker login ghcr.io` or build from source. |
| Auth / connection errors | SP creds correct? Tenant setting *Allow service principals…* enabled? SP added to the workspace? Workspace on Premium/PPU/Fabric with XMLA enabled? |
| First call is slow | Cold start (container + token); subsequent calls are fast. |
| Wrong workspace queried | Name the workspace in your request, or set `DAXTER_WORKSPACE` / `DAXTER_ENV` in `.env`. |

## Agent checklist

- [ ] Docker running
- [ ] env file has **real** values — GUID tenant/client ids, no `<>`/quotes/trailing spaces
- [ ] env file edited with a normal editor (not Claude Desktop's), absolute path noted
- [ ] `daxter` merged into `claude_desktop_config.json` (absolute paths, backup made)
- [ ] image pulls on first run (public GHCR) — or pre-pulled / built
- [ ] Claude Desktop **fully** quit & reopened (after the env file had real values)
- [ ] "List my Power BI workspaces" returns results
