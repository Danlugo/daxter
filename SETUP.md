# DAXter — Setup for Claude Desktop

Goal: get the **`daxter` MCP server** working in **Claude Desktop** on a fresh machine.
This guide is written so a **Claude agent can follow it top to bottom** (it can run the
shell commands and edit the config file), or a person can do it manually.

> Replace `<...>` placeholders with real values. The image carries **no credentials** —
> you supply them at runtime via `.env`.

## 0. Prerequisites

- **Docker Desktop** installed and **running**.
- **python3** (used only for the safe config merge in step 3). `git` only if you build from source.
- A **Power BI account** with access to a Premium/PPU/Fabric workspace — you'll **sign in as
  yourself** (default). *(For automation/CI you can use a service principal instead — see
  [Service principal](#alternative-service-principal-automation).)*

## 1. Get the image

The image is **public on GHCR**, so you can pull it directly — no login, no build:

```bash
docker pull ghcr.io/danlugo/daxter:latest
```

You can even skip this: the MCP server's `docker run` (step 3) auto-pulls the image on
first use. Building from source is optional (`git clone … && make image`).

## 2. Create the env file

Create an env file anywhere private — e.g. `~/daxter.env`. For the default **sign-in-as-
yourself** flow it's tiny — no secrets, no workspace required up front (you'll pick one in
chat after signing in):

```ini
DAXTER_AUTH_MODE=device-code
# Optional but recommended for a single org — pin to your tenant:
DAXTER_TENANT_ID=<tenant-guid>
# Optional defaults (or just name a workspace in chat):
# DAXTER_WORKSPACE=<workspace name>
# DAXTER_DATASET=<model name>
```

Keep this file private and never commit it. Format matters for any value you do set:

- **No angle brackets** — replace each `<...>` entirely, brackets included.
- **No surrounding quotes** and **no trailing spaces**.
- `DAXTER_TENANT_ID` (and client ids for SP) must be **GUIDs**
  (e.g. `12345678-1234-1234-1234-123456789abc`). A malformed/empty tenant id makes tools
  throw `The authority (including the tenant ID) must be in a well-formed URI format`.

> **Editor gotcha:** do **not** edit this file with **Claude Desktop's built-in editor** —
> it refuses with *"This file can't be saved — its path is outside the session folder."*
> Use a normal editor (Notepad, VS Code, `nano`, …).

> **The container reads `--env-file` only once, at startup**, and Claude Desktop launches it
> once per session — so **any change to the env file takes effect only after a full
> quit-and-reopen of Claude Desktop** (step 4), not just saving the file.

### Alternative: service principal (automation)

For a headless/CI server or multi-client automation, use a service principal instead — no
interactive sign-in, but you must supply the secret and a workspace:

```ini
DAXTER_AUTH_MODE=service-principal
DAXTER_TENANT_ID=<tenant-guid>
DAXTER_CLIENT_ID=<app-client-id>
DAXTER_CLIENT_SECRET=<app-secret>
DAXTER_WORKSPACE=<default workspace name>
DAXTER_DATASET=<model name>                  # optional
# DAXTER_PROD_WORKSPACES=<Prod WS 1>,<Prod WS 2>   # protect unsuffixed prod from writes
```

(Tenant admin must enable *"Allow service principals to use Power BI APIs"*; the SP must be a
member of the workspace.) With SP, skip the sign-in in step 5.

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

> **If you set any values (tenant id, or service-principal creds), make sure they're real —
> not `<...>` placeholders — before restarting.** A malformed/empty tenant id makes every
> tool call fail with `The authority (including the tenant ID) must be in a well-formed URI
> format`. (Device-code mode needs no secret and no workspace, so a minimal file is fine.)

**Fully quit and reopen.** *Fully quit* means **Cmd+Q** (macOS) or **right-click the tray
icon → Quit** (Windows) — **closing the window is not enough**. Claude reads the MCP config
and launches the env-file'd container only at startup, so this same full restart is required
**every time you change `daxter.env`**. Docker must be running.

## 5. Sign in & pick a workspace

In a Claude Desktop chat:

1. **"Sign in to Power BI"** → Claude calls `daxter_login` and shows you a URL + code. Open
   the URL, enter the code, and sign in with your account. (Skip this if you used a service
   principal — it's already authenticated.)
2. **"List my workspaces"** → Claude calls `daxter_workspaces`. Pick one as your default,
   or just name a workspace per request (the name encodes the env, e.g. `Sales - QA`).
3. Try it: **"List the tables in the &lt;your model&gt; model."**

If a tool ever reports *"Not signed in,"* just say **"sign in"** again. The token is cached
in the `daxter-tokens` volume, so you stay signed in across sessions. See
[`examples/mcp.md`](examples/mcp.md) for prompts per tool.

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
| Tool says **"Not signed in to Power BI"** | Device-code mode, no cached token yet — say **"sign in"** so Claude calls `daxter_login`, then complete the browser sign-in. |
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
- [ ] env file created (device-code: just `DAXTER_AUTH_MODE=device-code` + optional tenant id),
      any set values are real (GUID tenant id, no `<>`/quotes/spaces), edited in a normal editor
- [ ] `daxter` merged into `claude_desktop_config.json` (absolute paths, backup made)
- [ ] Claude Desktop **fully** quit & reopened (image auto-pulls from public GHCR on first run)
- [ ] **"Sign in to Power BI"** → completed the device-code login in the browser
- [ ] **"List my workspaces"** returns results
