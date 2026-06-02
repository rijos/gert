# Operations

## User lifecycle — "remove a user = remove a folder"

**Yes — with two caveats.**

Deleting a user's *data* is exactly:

```bash
rm -rf /data/users/{key}        # key = sha256(iss + sub)
```

This works cleanly because **nothing outside that folder references the user** — no rows in a shared DB, no foreign keys, no orphaned blobs. Chats, documents, embeddings, uploaded files, and artifacts all live under that one directory.

Caveats:

1. **Identity lives in Pocket ID.** Removing the folder deletes their data but not their *account*. To fully off-board, also delete/deactivate the user in Pocket ID; otherwise they can log back in and a fresh (empty) folder is lazily re-provisioned on their next request.
2. **JWTs are stateless.** A user removed in the IdP keeps a *valid* access token until it expires. Pocket ID issues **~1-hour access tokens** with no shorter, independently-configurable lifetime ([issue #792](https://github.com/pocket-id/pocket-id/issues/792), closed *not planned*), so deactivating a user in Pocket ID takes effect within **~1 hour** — fine for routine off-boarding. For faster cut-off, add a `sub`-denylist check in the JWT middleware that rejects a revoked `sub` on its next request; under Pocket ID this denylist is the practical lever for sub-hour revocation, not just an optional nicety (see [decisions.md §4](decisions.md#4-token-lifetime--revocation)).

The admin endpoint `DELETE /api/admin/users/{key}` performs the `rm -rf` (and can optionally call Pocket ID's API to deactivate in the same step). `GET /api/admin/users` is just a directory scan reading each `meta.json` — there is no user table to keep in sync.

---

> **See also [Security](security.md)** — the consolidated threat model. The cross-cutting
> controls below (headers/CSP, TLS, secrets, rate limits) are the operational half of it.

## Cross-cutting concerns

- **Single origin (no CORS):** Gert.Api serves the SPA bundle as static files, so the SPA and API share one origin and **no CORS configuration is required** for API calls. The only cross-origin hop is the browser → Pocket ID login/token exchange, configured via Pocket ID's allowed **web origins**.
- **TLS-only + HSTS:** Gert runs behind a TLS-terminating reverse proxy — bearer tokens and WebAuthn (passkeys) both require a secure context. The app assumes HTTPS-only and emits HSTS; plain-HTTP is never a supported deployment ([security F9](security.md#3-findings--remediations)).
- **Secrets are config, not source:** vLLM/SearXNG/provider keys come from environment variables / `dotnet user-secrets` (dev) / a secret store (prod). `appsettings.json` holds only non-secret defaults and placeholders — real keys are never committed, mirroring the generated, git-ignored dev JWT key ([security F8](security.md#3-findings--remediations), [tech-stack](tech-stack.md)).
- **Rate limits:** chat (GPU inference), ingestion (embeddings), and the sandbox (code exec) are expensive; apply per-user concurrency/rate caps (the ASP.NET rate limiter) so a single client — or a stolen token — can't saturate the box. Low-likelihood at ~20 trusted users, cheap to add ([security F10](security.md#3-findings--remediations)).
- **Embeddings:** the embedding model and its **dimension are fixed up front** — baked into the `vec0` table (`FLOAT[1024]`). Changing models later means re-embedding every chunk. **Chosen: bge-m3 (1024-dim), served by vLLM** via `--task embed` on the OpenAI-compatible `/v1/embeddings` endpoint (see [decisions.md §1](decisions.md#1-embedding-model--dimension)).
- **Resilience:** wrap vLLM/SearXNG calls with timeouts + retry (Polly). Surface upstream failures as `error` SSE events, not 500s mid-stream.
- **Observability:** record per-tool `latency_ms` (the "142ms"/"searxng" tags), structured logs keyed by `sub`-hash (never log raw tokens), and `GET /healthz` checking vLLM + SearXNG reachability.
- **Backups:** because each user is a folder, backup = snapshot `/data/users/`. SQLite WAL means use `VACUUM INTO` or the SQLite backup API for consistent copies rather than `cp` on a live DB.
- **Limits:** enforce max upload size and allowed MIME types (`pdf · docx · md · txt`) on `POST /api/documents`; reject path traversal in filenames.

### HTTP security headers & CSP

The SPA renders LLM/user-authored HTML, SVG, and Markdown, so a strict **Content-Security-Policy**
is the single highest-value control ([security F1](security.md#3-findings--remediations)). Gert.Api
emits, on every HTML response:

```
Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self';
    img-src 'self' data:; connect-src 'self' <pocket-id-origin>; frame-src 'self';
    object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'
X-Content-Type-Options: nosniff
Referrer-Policy: no-referrer
X-Frame-Options: DENY
Permissions-Policy: (minimised — camera=(), microphone=(), geolocation=() …)
```

- The no-bundle ESM design needs **no `unsafe-inline`** for scripts — the import map and
  `<script type="module" src>` are external — so `script-src 'self'` holds as-is
  ([ui-components](ui-components.md#6-devrelease-pipeline-no-npm)).
- **`connect-src` is the exfiltration brake**: it lists only the API origin and Pocket ID. A token
  stolen by injected script has nowhere to be sent.
- The HTML/SVG artifact iframe is `srcdoc` with its **own** restrictive CSP and a `sandbox`
  attribute that omits `allow-same-origin` ([ui-components](ui-components.md#security-token-handling--rendering)).
