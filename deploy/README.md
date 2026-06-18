# Deploying Gert

This folder holds Gert's deployment options. Today there is one:

- **`compose/`** — a self-contained **Docker Compose + Caddy** stack (Gert, Pocket ID,
  SearXNG, the Monty sandbox), with **automatic HTTPS**.
- **`ansible/`** — an **Ansible** playbook that provisions a fresh Debian/Ubuntu host
  (installs Docker) and deploys that same compose stack via systemd.

> Gert assumes **HTTPS-only** behind a TLS-terminating proxy — bearer tokens and
> passkeys (WebAuthn) both require a secure context ([operations.md](../docs/design/operations.md)).
> This stack provides that with Caddy.

## Architecture

```
                         ┌──────────── Caddy (auto-HTTPS) ────────────┐
   gert.example.com ─────┤  reverse_proxy gert:8080                   │
   artifacts.example.com ┤  reverse_proxy gert:8080  (F3: same app,   │
                         │                            distinct origin)│
   id.example.com ───────┤  reverse_proxy pocket-id:1411              │
                         └────────────────────────────────────────────┘
        gert ── validates JWT against Pocket ID JWKS (Auth:Authority, RS256)
         │
         ├── searxng:8080      (web_search)            [egress network]
         ├── monty:8077        (run_python sandbox)    [internal-only network, no egress]
         ├── EXTERNAL chat      (GERT_CHAT_BASEURL,  OpenAI-compatible)
         └── EXTERNAL embeddings(GERT_EMBED_BASEURL, OpenAI-compatible)
```

Chat + embeddings are **external** — point them at your own vLLM / OpenAI-compatible
endpoints. SearXNG and Monty run in-stack.

## Prerequisites

1. A Linux host with ports **80** and **443** reachable from the internet.
2. **DNS** A/AAAA records for all three hostnames → the host:
   `gert.example.com`, `artifacts.example.com`, `id.example.com`.
3. Docker Engine + compose plugin (the Ansible option installs these for you).
4. External, reachable **chat** and **embeddings** endpoints (OpenAI-compatible).

## Option A — Docker Compose (manual)

```sh
cd deploy/compose
cp .env.example .env
# edit .env: hostnames, ACME_EMAIL, secrets (openssl rand ...), chat/embeddings URLs
docker compose up -d --build
```

The Gert image is **built from this repo** (the `dotnet publish` step also runs the
no-npm web bundler, which fetches a pinned, SHA-512-verified esbuild binary — so the
**build needs network access**).

## Option B — Ansible (provision + deploy)

```sh
cd deploy/ansible
cp inventory.example.ini inventory.ini                 # set your host
cp group_vars/all.example.yml group_vars/all.yml       # set vars + secrets
ansible-vault encrypt group_vars/all.yml               # protect the secrets
ansible-playbook -i inventory.ini site.yml --ask-vault-pass
```

This installs Docker, clones the repo to `{{ gert_dir }}` (default `/opt/gert`),
renders `.env`, installs a `gert.service` systemd unit, and brings the stack up.
Re-running it (with a new `gert_repo_version`) redeploys and rebuilds.

## Pocket ID setup (one-time, manual)

Pocket ID's OIDC client and claims are configured in its **admin UI** — there is no
env-only way to create them. After the stack is up, open `https://id.example.com`,
complete the first-run admin bootstrap, then:

1. **Create an OIDC client** for Gert:
   - Client ID: **`gert`** (must equal `GERT_OIDC_CLIENT_ID` / `Auth:Audience`).
   - **Public client + PKCE** (the SPA holds no secret; tokens live in memory only — F2).
   - Callback / redirect URI: **`https://gert.example.com/`** (the SPA's `redirectUri`
     is `location.origin + "/"`).
   - Allow the web origin `https://gert.example.com` (CORS) — the SPA calls the token
     endpoint cross-origin from the app host to the IdP host.
2. **Groups** for the role claim (Gert maps `groups` → roles): create `gert-admins`
   (the admin surface) and `gert-users`; assign users.
3. **Tool entitlement** — define a custom claim **`gert_tools`** and attach it to users
   or a group, e.g. `rag search todo clock make_artifact edit_artifact read_artifact
   ask_user fetch memory` (and `sandbox` only where you want `run_python`), or `*` for
   all. **Absent ⇒ no tools** (fail-closed). See
   [auth.md → Tool entitlements](../docs/design/auth.md#tool-entitlements-allowed-tools-in-the-jwt).
4. Ensure `gert_tools`, `groups`, and `aud` are emitted **into the access token** the API
   validates (not only the ID token). Users log in with **passkeys** — onboarding is an
   admin "create user + send setup link" step in Pocket ID.

## ⚠ Verify before trusting in production

A few things depend on versions/upstream details this stack can't assert for you:

- **Pocket ID version drift.** The `image: ...:v1` tag, env var names (`APP_URL`,
  `TRUST_PROXY`), the data dir (`/app/data`), and the port (`1411`) track a major
  version. Pin a **digest** and confirm them against your tag's release notes; adjust
  `docker-compose.yml` if they differ.
- **OIDC endpoint paths.** The SPA calls `<authority>/authorize` and `<authority>/token`
  with `authority = https://id.example.com`. Confirm Pocket ID serves those paths (check
  `https://id.example.com/.well-known/openid-configuration`); if it namespaces them
  differently, adjust the SPA config or add a Caddy path rewrite.
- **Gert → IdP reachability.** The API fetches JWKS from `https://id.example.com`. The
  `gert` container must be able to resolve and reach that public name (or add an internal
  DNS / `extra_hosts` entry).
- **Forwarded headers.** The stack sets `ASPNETCORE_FORWARDEDHEADERS_ENABLED=true` so the
  app reads Caddy's `X-Forwarded-Proto`. ASP.NET's default trusts only loopback proxies; if
  HTTPS isn't detected (HSTS missing, redirect loops), the app's `ForwardedHeadersOptions`
  must clear `KnownNetworks`/`KnownProxies` (or trust the Docker network) — confirm against
  a live login.
- **PDF/DOCX ingestion is currently a no-op.** The `gert-extract` helper isn't shipped
  yet (`Gert:Extractor`), so md/txt ingest works but PDF/DOCX extraction returns nothing
  until the helper exists. Plain-text knowledge and everything else are unaffected.

## SPA OIDC config injection

The SPA reads `window.GERT_AUTH` for its issuer + client id. The Gert image's entrypoint
([compose/entrypoint-gert.sh](compose/entrypoint-gert.sh)) splices a small inline script
into `index.html` at startup from `GERT_AUTH_AUTHORITY` / `GERT_AUTH_CLIENT_ID` (set in
the compose env), so no source edit is needed.

## Backups

Back up these Docker volumes:

- **`gert-data`** — all per-user data (each user's `chat.db` / `rag.db` + object store
  under `/data`). This *is* Gert's data ([principles.md](../docs/design/principles.md)).
- **`pocket-id-data`** — users, passkeys, OIDC clients.
- **`caddy-data`** — issued TLS certs (avoids ACME rate limits on rebuild).

## Operations

- Logs: `docker compose -p gert logs -f gert`
- Restart: `systemctl restart gert` (Ansible) or `docker compose up -d` (manual).
- Health: the API exposes `GET /healthz` and `GET /readyz` (open, unauthenticated).
