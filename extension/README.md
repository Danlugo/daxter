# DAXter — Claude Desktop Extension (`.mcpb`)

One-click install of the DAXter MCP server for Claude Desktop. The extension is a thin
**launcher**: `manifest.json` tells Claude Desktop to run the published Docker image
(`ghcr.io/danlugo/daxter:latest … mcp`), so the cross-platform .NET XMLA/ADOMD engine runs in the
container exactly as it does for the CLI and web console.

## What it does (and doesn't)

- **Does:** removes the hand-edited `claude_desktop_config.json` step — drag-and-drop install,
  enable/disable/update from **Settings → Extensions**.
- **Doesn't bundle the image.** Docker Desktop must be installed and **running**; `docker run`
  pulls `:latest` on first use.
- **No env file, no credentials.** Auth is interactive **sign-in** (`daxter_login` device-code, or
  the web console) and the token persists in the `daxter-tokens` volume. The `.env` /
  service-principal path is for the **CLI only** (headless automation) — never the MCP.

## Build

```bash
# from the repo root
npx @anthropic-ai/mcpb@latest validate extension/manifest.json
npx @anthropic-ai/mcpb@latest pack extension extension/daxter-<version>.mcpb
```

`mcpb pack` zips `manifest.json` into `daxter-<version>.mcpb`. Attach that file to the matching
GitHub Release. (Signing is optional: `mcpb sign` — unsigned bundles install with a one-time
"unverified" prompt.)

## Notes / gotchas

- **`docker` on PATH:** Claude Desktop's spawned process may not see `/usr/local/bin`. The manifest
  pins `darwin` → `/usr/local/bin/docker` (Docker Desktop's symlink, both Intel and Apple Silicon).
  Adjust via `platform_overrides` if your install differs.
- **Version:** bump `manifest.json` `version` to match the image tag you intend it to track.
- Validate against the current [MCPB spec](https://github.com/modelcontextprotocol/mcpb) before each
  release — the manifest schema evolves.
