# Security

DAXter is a Power BI + Fabric client. It holds a signed-in AAD identity and can read and
(when explicitly enabled) mutate your tenant. This doc is the security posture + the
operator guidance for running it safely.

## Threat model in one line

Anyone who can reach a DAXter process that holds a signed-in token can act as that identity
against Power BI / Fabric. Protect the **network surface** and the **token volume**.

## Surfaces and how they're protected

| Surface | Default exposure | Auth |
|---|---|---|
| **CLI** (`daxter …`) | local process | the local user; uses the cached token |
| **MCP stdio** (`daxter mcp`) | stdio pipe to the launching app | the launching app (Claude Desktop) |
| **MCP HTTP** (`daxter mcp --http`) | the bind address (default `0.0.0.0` for container use) | **bearer token required** (`DAXTER_MCP_BEARER_TOKEN`); refuses to start without one unless `--no-auth` |
| **Web console** (`daxter web`) | **`127.0.0.1` only by default** (v1.40.0) | none on the Blazor UI (localhost-trusted); `/api/*` bearer-gated when `DAXTER_WEB_BEARER_TOKEN` is set |
| **Web `/api/health`** | follows the Web bind | always open (no secrets — version / tenant id / store stats) |

## Web console — the important one

The Web console holds the signed-in token and can refresh models, edit models over XMLA,
run T-SQL against Fabric (`/api/sql/export`), and read/write the artifact + context store.
Treat it like a privileged admin tool.

- **Default is localhost-only.** `daxter web` binds `127.0.0.1`. Safe on a shared network.
- **To expose it** (a remote box, a reverse proxy): bind wider with `--bind 0.0.0.0` (or
  `DAXTER_WEB_BIND`) **and set `DAXTER_WEB_BEARER_TOKEN`** so the dangerous `/api/*`
  endpoints require a bearer. Put a TLS-terminating reverse proxy in front. DAXter logs a
  loud warning if you bind wide without a token.
- **The container deploy** maps `-p 127.0.0.1:8080:8080` — the host only exposes localhost
  even though the container binds `0.0.0.0` internally.

```bash
# Exposed deployment (behind a proxy), gated:
DAXTER_WEB_BEARER_TOKEN=$(openssl rand -hex 24) \
  daxter web --bind 0.0.0.0
```

## The token volume (`~/.daxter`) is a secret store

It holds:
- The **MSAL token cache** — your AAD refresh tokens.
  - **Encrypted at rest (recommended)** when `DAXTER_CACHE_KEY` is set: the cache is written
    as `msal_cache.enc` using AES-256-GCM keyed off your secret. The persisted blob is
    unreadable without the key, so the **volume alone is no longer enough** to impersonate
    the identity. The key must live OUTSIDE the volume (env / secrets manager) — that's the
    whole point.
  - **Unencrypted** when `DAXTER_CACHE_KEY` is unset: macOS uses the keychain and Windows
    uses DPAPI (both encrypted), but on the slim Linux container image MSAL falls back to an
    unprotected file (`msal_cache.bin`) — anyone with read access to the volume can
    impersonate the signed-in identity for the token's lifetime (~90 days). DAXter prints a
    one-time warning on this path.
- Bearer-token files (`mcp-bearer-token`, `web-bearer-token`) — `chmod 600`.
- The artifact + context store (may carry exported report definitions, SQL CSVs).

**Guidance:**
- **Set `DAXTER_CACHE_KEY`** when running in a container / on Linux, supplying the key from a
  secrets manager (not from a file on the volume). Example:
  `DAXTER_CACHE_KEY=$(some-secrets-fetch) daxter web`.
- Restrict access to the volume regardless (it's still sensitive).
- Don't snapshot/back it up to shared storage.
- For unattended / hosted use, prefer a **service principal** (`DAXTER_AUTH_MODE=
  service-principal`) with the secret injected at runtime from a secrets manager (Key
  Vault), rather than a cached device-code token sitting on disk.
- Per-tenant isolation (e.g. Semantix's one-volume-per-client model) limits blast radius —
  a compromise of one volume is one client, not all.

## The write gate is a guardrail, not an injection boundary

`SqlWriteGate` (keyword classification) and the model-edit / refresh `Allow writes` toggle
exist to prevent **accidental** writes. They are not a defense against a hostile operator —
DAXter is a SQL/model client and the operator can run what the gate allows. The real
boundaries are: the network surface (above), the AAD identity's own permissions, and the
read-only/write-allowed workspace patterns.

## Secrets handling

- No secrets in the image, the repo, or `mcp.json`. `.env`/`*.env` are gitignored +
  dockerignored.
- Logs are run through `SecretRedactor` (JWTs, key=value secrets, Bearer headers) before
  hitting the in-memory log buffer the `/logs` page reads.
- Bearer tokens are never logged in full (redacted to `sk_abc12***`).

## Dependency hygiene

CI fails on any known-vulnerable dependency (`dotnet list package --vulnerable
--include-transitive`). Pin/update promptly when a CVE is disclosed.

## Reporting

Found something? Open a private security advisory on the GitHub repo, or contact the
maintainer directly — please don't file a public issue for a vulnerability.
