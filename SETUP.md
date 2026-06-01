# DAXter — Setup for Claude Desktop

Goal: get the **`daxter` MCP server** working in **Claude Desktop** on a fresh machine.
This guide is written so a **Claude agent can follow it top to bottom** (it can run the
shell commands and edit the config file), or a person can do it manually.

## Choose your install

- **Installing it yourself (easiest):** the **[Claude Desktop Extension (`.mcpb`)](#easiest-path--the-claude-desktop-extension-mcpb)** — one click, no terminal, no JSON.
- **Asking Claude to set it up:** the **[manual flow](#0-prerequisites)** (steps 0–4) — Claude runs Docker, merges the config, starts the web console, and verifies sign-in. *(Claude can't click the Extensions dialog, so this is its path.)*
- **Headless / CI / service principal:** **[Advanced](#advanced-service-principal--headless)**.

All paths run the **same image and `daxter-tokens` volume**; auth is interactive **sign-in** (the `.env` / service-principal route is **CLI-only**). The agent callouts below apply to the **manual flow**.

> **No env file, no credentials to paste.** The image ships with zero secrets. You sign in and
> set your defaults in the **web console**, which saves them to a config file on the mounted
> volume that the CLI, the MCP server, and the console all read. (Headless / service-principal
> automation can still inject settings via env vars — see [Advanced](#advanced-service-principal--headless).)

> **Install agent — the flow in one line:** run the Docker/config commands in steps 1–2
> automatically, then **hand off to the user twice** (the only things you can't do for them):
> 1. **After you start the web console in step 3**, tell the user to open
>    **<http://localhost:8080> → Status → Sign in** (and optionally set defaults in **Configure**) —
>    **wait for them to confirm** they're signed in before continuing.
> 2. **In step 4, tell the user to fully quit & reopen Claude Desktop** — the `daxter` tools only
>    load on that restart.

> **Agent — keep it simple; the user may know nothing about DAXter, Docker, or MCP.** Narrate in
> plain language and only at a high level — e.g. *"Setting things up… (step 2 of 4)"* — and
> **don't paste raw command output, JSON, or PowerShell** unless something errors (then show just
> the relevant line). Boil it down to the only two things they do: **sign in** (step 3) and
> **restart Claude Desktop** (step 4). When it's done, **reassure them it's all working, show the
> workspaces you found, and invite them to ask questions in plain English** — don't quiz them.

## Easiest path — the Claude Desktop Extension (`.mcpb`)

If your Claude Desktop has **Settings → Extensions**, this is the whole install — no terminal, no JSON:

1. Make sure **Docker Desktop is running** — the extension launches DAXter in its container (see [Prerequisites](#0-prerequisites)).
2. Download **`daxter-<version>.mcpb`** from the [latest release](https://github.com/Danlugo/daxter/releases/latest), **double-click it** (or drag it into **Settings → Extensions**), review the details, and **Install**. The `daxter` tools appear right away — *if they don't, fully restart Claude Desktop once to refresh the extension registry.*
3. **Sign in:** ask Claude *"sign me in to Power BI"* — it runs `daxter_login`, returns a device-code link, and caches your token in the `daxter-tokens` volume.

That's the whole thing for reading/querying — name the workspace in your request and go. **To set a default workspace, override the tenant, or enable writes (refresh from chat),** run the web console once ([step 3](#3-sign-in-to-power-bi-and-set-your-defaults)); those settings persist to the same volume the extension uses.

No JSON editing, no env file. The extension is only a **launcher** for the same Docker image, so the .NET/XMLA engine still runs in the container on any OS. Auth is **sign-in only** — no credentials are stored in the extension. For an agent-driven setup, a hand-configured server, or service-principal / headless automation, use the manual steps below.

## 0. Prerequisites

- **Docker Desktop** installed and **running** — *running*, not just installed; you'll verify
  with `docker info` in step 1 before pulling anything.
- **python3** for the macOS/Linux config merge in step 2 (on **Windows** use the PowerShell
  merge — no python needed). `git` only if you build from source.
- A **Power BI account** with access to a Premium/PPU/Fabric workspace — you'll **sign in as
  yourself** (default). *(For automation/CI you can use a service principal instead — see
  [Advanced](#advanced-service-principal--headless).)*

## 1. Get the image

**First, verify the Docker daemon is actually running** — *installed ≠ running*, and a stopped
daemon is the #1 cause of the failures below. Don't pull or run anything until this passes:

```bash
docker info >/dev/null 2>&1 \
  && echo "Docker is running ✓ — continue" \
  || echo "Docker is NOT running ✗ — open Docker Desktop, wait until it shows 'Running', then re-run this"
```

If it reports **not running**: launch **Docker Desktop** (Windows/macOS) or start the engine
(`sudo systemctl start docker` on Linux), wait until the whale icon stops animating / the status
reads **Running**, then re-run the check. Every `docker` command in this guide (pull, `run`, and
the MCP server Claude Desktop launches) needs the daemon up.

Once the check passes, the image is **public on GHCR**, so you can pull it directly — no login, no build:

```bash
docker pull ghcr.io/danlugo/daxter:latest
```

You can even skip this: the MCP server's `docker run` (step 2) auto-pulls the image on first use.
Building from source is optional (`git clone … && make image`).

## 2. Configure Claude Desktop

Config file location:
- **macOS:** `~/Library/Application Support/Claude/claude_desktop_config.json`
- **Windows:** `%APPDATA%\Claude\claude_desktop_config.json`

Add a `daxter` server under `mcpServers`. **Merge — don't overwrite** existing servers. There's
**nothing to put in an env file** — the server reads its settings (workspace, tenant, allow-writes,
…) from the shared `daxter-tokens` volume, which you set in the web console (step 3). The merge
below makes a backup, preserves other servers, and uses your real docker path.

**macOS / Linux (bash):**

```bash
CFG="$HOME/Library/Application Support/Claude/claude_desktop_config.json"   # macOS
# Linux: CFG="$HOME/.config/Claude/claude_desktop_config.json"
mkdir -p "$(dirname "$CFG")"; [ -f "$CFG" ] && cp "$CFG" "$CFG.bak"
python3 - "$CFG" "$(command -v docker)" <<'PY'
import json, os, sys
cfg, docker = sys.argv[1], sys.argv[2]
d = json.load(open(cfg)) if os.path.exists(cfg) and os.path.getsize(cfg) else {}
d.setdefault("mcpServers", {})["daxter"] = {
    "command": docker,
    "args": ["run", "-i", "--rm",
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
    args = @("run","-i","--rm","-v","daxter-tokens:/home/daxter/.daxter",
             "ghcr.io/danlugo/daxter:latest","mcp")
}) -Force
# Write UTF-8 WITHOUT a BOM — Claude Desktop's JSON parser rejects a BOM ("not valid JSON").
# (Windows PowerShell 5.1's `Set-Content -Encoding UTF8` adds one, so don't use it here.)
$json = $d | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($cfg, $json, (New-Object System.Text.UTF8Encoding $false))
"mcpServers: " + ($d.mcpServers.PSObject.Properties.Name -join ', ')
```

Either way, the equivalent JSON, if editing by hand:

```jsonc
"mcpServers": {
  "daxter": {
    "command": "/usr/local/bin/docker",
    "args": ["run", "-i", "--rm",
             "-v", "daxter-tokens:/home/daxter/.daxter",
             "ghcr.io/danlugo/daxter:latest", "mcp"]
  }
}
```

## 3. Sign in to Power BI (and set your defaults)

Everything is configured in the **web console** — a local page where you click **Sign in** and,
optionally, set defaults. It uses the **same `daxter-tokens` volume** as the MCP server, so
whatever you do here is what Claude Desktop uses. *(Service principal? See
[Advanced](#advanced-service-principal--headless) — you can skip the interactive sign-in.)*

**Claude does this:** start the web console (a separate, long-running container; the MCP config
above does **not** start it):

```bash
# macOS / Linux:
docker run -d -p 8080:8080 --name daxter-web --restart unless-stopped \
  -v daxter-tokens:/home/daxter/.daxter ghcr.io/danlugo/daxter:latest web
# Windows (PowerShell): same command works as-is.
```

**You do this** (Claude can't click for you): open **<http://localhost:8080>** → **Status** →
**Sign in**. A Microsoft page opens with a **clickable link and a copyable code** — click the
link, paste the code, sign in. The page updates itself when you're done. Then, if you like,
**⚙ Configure** → set your **default workspace/dataset**, your **tenant** (recommended if sign-in
resolves the wrong org), and **Allow writes** (to refresh from chat) → **Save**. It all persists
to the shared volume that the MCP server reads.

> **Agent — verify it yourself; don't make the user test it.** Once they confirm they've signed
> in, prove the sign-in **and** the data path with the CLI (same image, same volume — no Claude
> Desktop needed):
> ```bash
> docker run --rm -v daxter-tokens:/home/daxter/.daxter ghcr.io/danlugo/daxter:latest ws ls
> ```
> If it lists workspaces, sign-in works and the connection is good. **Keep the workspace names** —
> you'll show them to the user in your final reassurance.

Your sign-in and settings live on the `daxter-tokens` volume, so **you stay signed in across
sessions** and Claude Desktop reuses them automatically.

## 4. Restart Claude Desktop — last step, and you're done

> **Agent:** you can't do this part — tell the user the final step is to fully quit & reopen
> Claude Desktop (the `daxter` tools load only at startup). You already proved sign-in + the data
> path with `ws ls` (step 3), so **once they've restarted, reassure them in plain language that
> it's all set, show them the workspaces you found, and invite them to ask questions** — e.g.
> *"You're all set ✅ — I can see your workspaces: A, B, C. You can now ask me about your Power BI
> in plain English, like 'what measures are in the Sales model?' or 'when was X last refreshed?'"*
> Don't make them run a test. **Don't look for an `…mcp` container in `docker ps`** — Claude
> Desktop runs it over stdio, so it won't appear (normal, not a failure).

**Fully quit and reopen.** *Fully quit* means **Cmd+Q** (macOS) or **right-click the tray icon →
Quit** (Windows) — **closing the window is not enough**. Claude reads the MCP config and launches
the `daxter` container **only at startup**, so the tools don't appear until you do this. Because
you signed in first (step 3), it finds your cached token immediately and the tools work on the
first try. (Docker must be running.)

**That's it.** The `daxter` tools are now in Claude Desktop, using the sign-in already verified.
*(Want to see them? Type **"List my workspaces"** in a chat — it returns right away. If a tool
ever says "Not signed in" — it shouldn't — reopen **<http://localhost:8080> → Status → Sign in**.)*
See [`examples/mcp.md`](examples/mcp.md) for a prompt per tool.

## Advanced: service principal / headless

For a **headless/CI server or automation** with no UI to click, or to inject a service-principal
secret **without writing it to the volume**, pass settings as env vars at runtime — add
`--env-file` to the `docker run` (or the MCP `args`). Env vars are a **fallback**: explicit values
win, then the web-console config, then env. A minimal SP env file:

```ini
DAXTER_AUTH_MODE=service-principal
DAXTER_TENANT_ID=<tenant-guid>
DAXTER_CLIENT_ID=<app-client-id>
DAXTER_CLIENT_SECRET=<app-secret>
DAXTER_WORKSPACE=<default workspace name>
# DAXTER_PROD_WORKSPACES=<Prod WS 1>,<Prod WS 2>   # protect unsuffixed prod from writes
# DAXTER_MCP_ALLOW_WRITES=true                     # enable refresh/cache over MCP
```

```bash
docker run -i --rm --env-file "$HOME/daxter.env" \
  -v daxter-tokens:/home/daxter/.daxter ghcr.io/danlugo/daxter:latest mcp
```

(Tenant admin must enable *"Allow service principals to use Power BI APIs"*; the SP must be a
member of the workspace.) With SP you can skip the interactive sign-in in step 3. **Don't edit
config in Claude Desktop's built-in editor** — it refuses paths outside the session folder; use a
normal editor.

## Multiple clients / environments

Give each client its **own token volume** (its config + sign-in live there) and one MCP server
entry per client:

```jsonc
"daxter-acme":  { "command": "/usr/local/bin/docker", "args": ["run","-i","--rm","-v","daxter-acme-tokens:/home/daxter/.daxter","ghcr.io/danlugo/daxter:latest","mcp"] },
"daxter-globex":{ "command": "/usr/local/bin/docker", "args": ["run","-i","--rm","-v","daxter-globex-tokens:/home/daxter/.daxter","ghcr.io/danlugo/daxter:latest","mcp"] }
```

Run one web console per volume (different `-p` ports) to sign in / configure each. Within a client,
target environments by naming the workspace per request (the name encodes the env, e.g.
`Sales - QA`; unsuffixed = prod).

## Troubleshooting

| Symptom | Fix |
|---------|-----|
| Tool says **"Not signed in to Power BI"** | No cached token yet — open the web console (**http://localhost:8080 → Status → Sign in**) and finish the browser sign-in. If the console isn't running, have Claude start it (`docker run -d -p 8080:8080 … web`). |
| `The authority ... must be in a well-formed URI format` | A tenant id was set to a placeholder/malformed value. Fix it in the web console **⚙ Configure → tenant** (a real GUID, no `<>`/quotes/spaces) and **Save**, then fully restart Claude Desktop. |
| Claude: **"Could not load app settings… not valid JSON"** | The config file has a **UTF-8 BOM** (older Windows PowerShell's `Set-Content -Encoding UTF8` adds one). Rewrite it BOM-free, then fully restart: `$p="$env:APPDATA\Claude\claude_desktop_config.json"; [System.IO.File]::WriteAllText($p,[System.IO.File]::ReadAllText($p),(New-Object System.Text.UTF8Encoding $false))` — keeps your `daxter` entry. (Step 2's merge writes BOM-free.) |
| Tools don't appear | Docker running? Did you fully **quit & reopen** Desktop? Check Settings → Developer / MCP logs (the server's stderr). |
| **Extension** installed but tools missing | Restart Claude Desktop **once** to refresh the extension registry; confirm Docker Desktop is running (the extension launches the container on demand). |
| No `…mcp` container in `docker ps` | **Normal — not a failure.** Claude Desktop runs the MCP server over **stdio** (`docker run -i --rm … mcp`), not as a standalone long-lived container, so it won't show in `docker ps`. Confirm it loaded by running **"List my workspaces"** in the Claude Desktop chat — not from the shell. |
| `pull access denied` from GHCR | The package is public; this only appears if it was made private — `docker login ghcr.io` or build from source. |
| Auth / connection errors | Right tenant/workspace in **⚙ Configure**? Workspace on Premium/PPU/Fabric with XMLA enabled? For SP: *Allow service principals…* enabled and the SP a workspace member? |
| First call is slow | Cold start (container + token); subsequent calls are fast. |
| Wrong workspace queried | Name the workspace in your request, or set a default in **⚙ Configure**. |

## Remove / uninstall DAXter

Undo the pieces in any order. **Steps 1–2 fully stop DAXter; 3–4 are optional cleanup.** DAXter
only *reads* from Power BI — removing it changes nothing in your tenant.

1. **Unhook it from Claude Desktop.** Open the config (macOS
   `~/Library/Application Support/Claude/claude_desktop_config.json`, Windows
   `%APPDATA%\Claude\claude_desktop_config.json`), delete the `"daxter": { … }` entry under
   `mcpServers`, save, then **fully quit & reopen Claude Desktop**. (A `.bak` from setup sits next
   to the file if you'd rather restore the previous version.)
2. **Stop the web console** (if you started it): `docker rm -f daxter-web`
3. **Erase your sign-in + settings** *(optional — signs you out and clears your config/rules)*:
   `docker volume rm daxter-tokens`
4. **Remove the image** *(optional)*: `docker rmi ghcr.io/danlugo/daxter:latest`

## Agent checklist

- [ ] Docker daemon running — `docker info` succeeds (step 1) **before** any pull/run
- [ ] `daxter` merged into `claude_desktop_config.json` (absolute docker path, backup made, **no `--env-file`**)
- [ ] **Web console started by Claude** (`docker run -d -p 8080:8080 … web`); user signed in at
      **http://localhost:8080 → Status → Sign in** (and set defaults in ⚙ Configure if wanted)
- [ ] **Verified by Claude:** `… ws ls` lists workspaces — proves sign-in + connection, so after the
      restart Claude **confirms it's all working** (no user test required)
- [ ] **Restarted last:** Claude Desktop **fully** quit & reopened so it loads the `daxter` config
