# DAXter — Setup for Claude Desktop

Goal: get the **`daxter` MCP server** working in **Claude Desktop** on a fresh machine.
This guide is written so a **Claude agent can follow it top to bottom** (it can run the
shell commands and edit the config file), or a person can do it manually.

> **Install agent — the flow in one line:** run the Docker/config commands in steps 1–4
> automatically, **then hand off to the user twice** (these are the only things you can't do
> for them):
> 1. **After you start the web console in step 4**, tell the user to open
>    **<http://localhost:8080> → Status → Sign in** and complete the browser sign-in —
>    **then wait for them to confirm** they're signed in before continuing.
> 2. **In step 5, tell the user to fully quit & reopen Claude Desktop** — the `daxter` tools
>    only load on that restart, so this is the last step that makes the MCP side work.

> Replace `<...>` placeholders with real values. The image carries **no credentials** —
> you supply them at runtime via `.env`.

> **Just want to sign in and look around first?** The quickest path is the **web console** —
> no Claude Desktop config needed.
>
> ⚠️ **The web console is a separate process from the Claude Desktop MCP server.** Completing
> the install steps (1–5) below does **NOT** start it — you must run the command below yourself.
> ```bash
> # Use an ABSOLUTE path to the env file so Docker finds it from any working directory.
> # macOS / Linux:
> docker run -d -p 8080:8080 --env-file "$HOME/daxter.env" \
>   -v daxter-tokens:/home/daxter/.daxter \
>   ghcr.io/danlugo/daxter:latest web
> # Windows (PowerShell): swap the env path → --env-file "C:\Users\<you>\daxter.env"
> ```
> Open <http://localhost:8080> → **Status → Sign in**, then **Configure** your default
> workspace/dataset. The same `daxter-tokens` volume is reused by the MCP server below, so
> **you only sign in once.** Continue here to also wire up Claude Desktop.
>
> - **Default port is `8080`.** For a different port, pass `web --port <N>` **and** match the
>   publish flag `-p <N>:<N>` (e.g. `-p 9000:9000 … web --port 9000`).
> - **Not persistent by default** — this container does **not** survive a reboot or Docker
>   restart. Add `--restart unless-stopped` to the `docker run` to keep it coming back
>   automatically.

## 0. Prerequisites

- **Docker Desktop** installed and **running** — *running*, not just installed; you'll verify
  this with `docker info` in step 1 before pulling anything.
- **python3** for the macOS/Linux config merge in step 3 (on **Windows** use the PowerShell
  merge — no python needed). `git` only if you build from source.
- A **Power BI account** with access to a Premium/PPU/Fabric workspace — you'll **sign in as
  yourself** (default). *(For automation/CI you can use a service principal instead — see
  [Service principal](#alternative-service-principal-automation).)*

## 1. Get the image

**First, verify the Docker daemon is actually running** — *installed ≠ running*, and a stopped
daemon is the #1 cause of the failures below. Don't pull or run anything until this passes:

```bash
docker info >/dev/null 2>&1 \
  && echo "Docker is running ✓ — continue" \
  || echo "Docker is NOT running ✗ — open Docker Desktop, wait until it shows 'Running', then re-run this"
```

If it reports **not running**: launch **Docker Desktop** (Windows/macOS) or start the engine
(`sudo systemctl start docker` on Linux), wait until the whale icon stops animating / the
status reads **Running**, then re-run the check. Every `docker` command in this guide (pull,
`run`, and the MCP server Claude Desktop launches) needs the daemon up — if it's down, the
container never starts and `http://localhost:8080` (or the MCP tools) silently won't respond.

Once the check passes, the image is **public on GHCR**, so you can pull it directly — no login, no build:

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
# Recommended — pin your Entra tenant so device-code reliably resolves your org.
# Replace <tenant-guid> with your REAL tenant GUID (Entra admin center -> Overview,
# or the GUID in your Power BI URL). Do NOT leave the <...> placeholder: an unreplaced
# value makes every tool fail with "...must be in a well-formed URI format".
# Don't have it handy? Comment this line out (#) to use the default authority — you can
# add it later if sign-in resolves the wrong org or fails.
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
> quit-and-reopen of Claude Desktop** (step 5), not just saving the file.

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
member of the workspace.) With SP, skip the sign-in in step 4.

## 3. Configure Claude Desktop

Config file location:
- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`

Add a `daxter` server under `mcpServers`. **Merge — don't overwrite** existing servers.
Set the env path to the **absolute path** of your env file from step 2. The safe merge below
makes a backup, preserves other servers, and uses your real docker path.

**macOS / Linux (bash):**

```bash
CFG="$HOME/Library/Application Support/Claude/claude_desktop_config.json"   # macOS
# Linux: CFG="$HOME/.config/Claude/claude_desktop_config.json"
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

**Windows (PowerShell):** no bash, so use this equivalent merge — config lives at
`%APPDATA%\Claude\claude_desktop_config.json`:

```powershell
$cfg = "$env:APPDATA\Claude\claude_desktop_config.json"
$envfile = "C:\Users\<you>\daxter.env"   # ← absolute path to your env file from step 2
$docker = (Get-Command docker).Source
New-Item -ItemType Directory -Force -Path (Split-Path $cfg) | Out-Null
if (Test-Path $cfg) { Copy-Item $cfg "$cfg.bak" -Force }
$d = if ((Test-Path $cfg) -and (Get-Item $cfg).Length -gt 0) {
        Get-Content $cfg -Raw | ConvertFrom-Json
     } else { [pscustomobject]@{} }
if (-not $d.PSObject.Properties['mcpServers']) {
    $d | Add-Member -NotePropertyName mcpServers -NotePropertyValue ([pscustomobject]@{}) -Force
}
$d.mcpServers | Add-Member -NotePropertyName daxter -NotePropertyValue ([pscustomobject]@{
    command = $docker
    args = @("run","-i","--rm","--env-file",$envfile,
             "-v","daxter-tokens:/home/daxter/.daxter",
             "ghcr.io/danlugo/daxter:latest","mcp")
}) -Force
$d | ConvertTo-Json -Depth 10 | Set-Content $cfg -Encoding UTF8
"mcpServers: " + ($d.mcpServers.PSObject.Properties.Name -join ', ')
```

Either way, the equivalent JSON, if editing by hand (use absolute paths):

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

## 4. Sign in to Power BI

Sign-in has two parts — **Claude starts the sign-in page; you finish it in the browser.**
*(Service principal? You're already authenticated — skip this step.)*

**Claude does this:** starts the **web console** — a local sign-in page (a separate,
long-running container; the MCP config above does **not** start it):

```bash
# macOS / Linux:
docker run -d -p 8080:8080 --restart unless-stopped --env-file "$HOME/daxter.env" \
  -v daxter-tokens:/home/daxter/.daxter ghcr.io/danlugo/daxter:latest web
# Windows (PowerShell): swap the env path → --env-file "C:\Users\<you>\daxter.env"
```

**You do this** (Claude can't click for you): open **<http://localhost:8080>** → **Status** →
**Sign in**. A Microsoft page opens with a **clickable link and a copyable code** — click the
link, paste the code, and sign in with your account. The page updates itself when you're done.

> **Agent:** after running the command above, tell the user to do this now, and **wait for them
> to confirm they're signed in** before moving to step 5 — don't report success until they have.

Your sign-in is cached on the shared `daxter-tokens` volume, so **Claude Desktop's `daxter`
server will reuse it automatically** — you only sign in once.

## 5. Restart Claude Desktop — last step, and you're done

> **Before restarting, if you set any values (tenant id, or service-principal creds), make sure
> they're real — not `<...>` placeholders.** A malformed/empty tenant id makes every tool call
> fail with `The authority (including the tenant ID) must be in a well-formed URI format`.

> **Agent:** you can't do this part — tell the user it's the final step and ask them to fully
> quit & reopen Claude Desktop now (the `daxter` tools won't appear until they do). **Don't try
> to verify the MCP server from the shell** — it runs *inside* Claude Desktop over stdio, so it
> **won't show in `docker ps`** (its absence means nothing). Just ask the user to run **"List my
> workspaces"** in the Claude Desktop chat and treat a normal result as success — don't preface
> it with a long "if it's broken" checklist; only walk through fixes if they report a real error.

**Fully quit and reopen.** *Fully quit* means **Cmd+Q** (macOS) or **right-click the tray icon →
Quit** (Windows) — **closing the window is not enough**. Claude reads the MCP config and launches
the `daxter` container **only at startup** — so the tools don't appear until you do this. Because
you signed in first (step 4), the container finds your cached token immediately and the tools work
on the first try. (Docker must be running. Redo this restart **every time you change `daxter.env`**.)

**Verify:** in a chat, type **"List my workspaces"** — it should return your workspaces right away
(your token is already cached). Name one as your default (or just mention a workspace per request,
e.g. `Sales - QA`), then try **"List the tables in the `<your model>` model"**. That's it — you're
done. *(Only if a tool says "Not signed in" — which shouldn't happen — reopen
**<http://localhost:8080> → Status → Sign in**.)* See [`examples/mcp.md`](examples/mcp.md) for a
prompt per tool.

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
| Tool says **"Not signed in to Power BI"** | No cached token yet — open the web console (**http://localhost:8080 → Status → Sign in**) and finish the browser sign-in. If the console isn't running, have Claude start it (`docker run -d -p 8080:8080 … web`). |
| `The authority ... must be in a well-formed URI format` | `DAXTER_TENANT_ID` is still a placeholder, empty, or malformed — **or** you edited `daxter.env` without fully restarting Claude Desktop. Verify the values are real GUIDs/strings (no `<>`, quotes, or spaces) and **fully quit & reopen** the app. |
| Edited the env file but nothing changed | The container reads `--env-file` only at startup. Fully quit & reopen Claude Desktop (closing the window isn't enough). |
| `can't be saved — path is outside the session folder` | You're editing `daxter.env` in Claude Desktop's built-in editor. Use a normal editor (Notepad, VS Code, `nano`). |
| Tools don't appear | Docker running? Did you fully **quit & reopen** Desktop? Check Settings → Developer / MCP logs (the server's stderr). |
| No `…mcp` container in `docker ps` | **Normal — not a failure.** Claude Desktop runs the MCP server over **stdio** (`docker run -i --rm … mcp`), not as a standalone long-lived container, so it won't show in `docker ps`. Confirm it loaded by running **"List my workspaces"** in the Claude Desktop chat — not from the shell. |
| `pull access denied` from GHCR | The package is public; this only appears if it was made private — `docker login ghcr.io` or build from source. |
| Auth / connection errors | SP creds correct? Tenant setting *Allow service principals…* enabled? SP added to the workspace? Workspace on Premium/PPU/Fabric with XMLA enabled? |
| First call is slow | Cold start (container + token); subsequent calls are fast. |
| Wrong workspace queried | Name the workspace in your request, or set `DAXTER_WORKSPACE` / `DAXTER_ENV` in `.env`. |

## Agent checklist

- [ ] Docker daemon running — `docker info` succeeds (step 1) **before** any pull/run
- [ ] env file created (device-code: `DAXTER_AUTH_MODE=device-code` + recommended tenant id —
      or comment it out), any set values are real (GUID tenant id, no `<>`/quotes/spaces),
      edited in a normal editor
- [ ] `daxter` merged into `claude_desktop_config.json` (absolute paths, backup made)
- [ ] **Signed in (step 4):** Claude started the web console (`docker run -d -p 8080:8080 … web`),
      then the user finished at **http://localhost:8080 → Status → Sign in** (click link + paste
      code). Token cached on the shared volume.
- [ ] **Restarted last (step 5):** Claude Desktop **fully** quit & reopened — so it loads the
      `daxter` config and the MCP container finds the cached token (image auto-pulls on first run)
- [ ] **"List my workspaces"** returns results
