# Operations

## User lifecycle - "remove a user = remove a folder"

**Yes - with two caveats.**

Deleting a user's *data* is exactly:

```bash
rm -rf /data/users/{key}        # key = sha256(iss + sub)
```

This works cleanly because **nothing outside that folder references the user** - no rows in a shared DB, no foreign keys, no orphaned blobs. Chats, documents, embeddings, uploaded files, and artifacts all live under that one directory.

Caveats:

1. **Identity lives in Pocket ID.** Removing the folder deletes their data but not their *account*. To fully off-board, also delete/deactivate the user in Pocket ID; otherwise they can log back in and a fresh (empty) folder is lazily re-provisioned on their next request.
2. **JWTs are stateless.** A user removed in the IdP keeps a *valid* access token until it expires. Pocket ID issues **~1-hour access tokens** with no shorter, independently-configurable lifetime ([issue #792](https://github.com/pocket-id/pocket-id/issues/792), closed *not planned*), so deactivating a user in Pocket ID takes effect within **~1 hour**. There is deliberately **no `sub`-denylist**: it would be shared, mutable, per-instance auth state that breaks running multiple GERT instances, so GERT stays stateless and validates every token purely from the JWT + JWKS. For sub-hour revocation, shorten the token lifetime at the IdP (see [decisions.md section 4](decisions.md#4-token-lifetime--revocation)).

The admin endpoint `DELETE /api/admin/users/{key}` performs the `rm -rf` (and can optionally call Pocket ID's API to deactivate in the same step). `GET /api/admin/users` is just a directory scan that opens each folder's `user.db` for the username plus an object-store footprint listing - there is no central user table to keep in sync.

---

> **See also [Security](security.md)** - the consolidated threat model. The cross-cutting
> controls below (headers/CSP, TLS, secrets, rate limits) are the operational half of it.

## Cross-cutting concerns

- **Single origin (no CORS):** Gert.Api serves the SPA bundle as static files, so the SPA and API share one origin and **no CORS configuration is required** for API calls. The only cross-origin hop is the browser -> Pocket ID login/token exchange, configured via Pocket ID's allowed **web origins**.
- **TLS-only + HSTS:** Gert runs behind a TLS-terminating reverse proxy - bearer tokens and WebAuthn (passkeys) both require a secure context. The app assumes HTTPS-only and emits HSTS; plain-HTTP is never a supported deployment ([security F9](security.md#3-findings--remediations)).
- **Secrets are config, not source:** vLLM/SearXNG/provider keys come from environment variables / `dotnet user-secrets` (dev) / a secret store (prod). `appsettings.json` holds only non-secret defaults and placeholders - real keys are never committed, mirroring the generated, git-ignored dev JWT key ([security F8](security.md#3-findings--remediations), [tech-stack](tech-stack.md)).
- **Rate limits:** chat (GPU inference), ingestion (embeddings), and the sandbox (code exec) are expensive; apply per-user concurrency/rate caps (the ASP.NET rate limiter) so a single client - or a stolen token - can't saturate the box. Low-likelihood at ~20 trusted users, cheap to add ([security F10](security.md#3-findings--remediations)).
- **Embeddings:** the embedding model and its **dimension are fixed up front** - baked into the `vec0` table (`FLOAT[1024]`). Changing models later means re-embedding every chunk. **Chosen: bge-m3 (1024-dim), served by vLLM** via `--task embed` on the OpenAI-compatible `/v1/embeddings` endpoint (see [decisions.md section 1](decisions.md#1-embedding-model--dimension)).
- **Resilience:** wrap vLLM/SearXNG calls with timeouts + retry (Polly). Surface upstream failures as `error` SSE events, not 500s mid-stream.
- **Observability:** record per-tool `latency_ms` (the "142ms"/"searxng" tags), structured logs in the shared JSON format below (keyed by identity **hash**, never raw tokens/`sub`/email/content), and two anonymous probes: `GET /healthz` (liveness - the process is up) and `GET /readyz` (readiness - vLLM + SearXNG reachable, else `503` with a per-dependency map).
- **Backups:** because each user is a folder, backup = snapshot `/data/users/`. SQLite WAL means use `VACUUM INTO` or the SQLite backup API for consistent copies rather than `cp` on a live DB.
- **Limits:** enforce max upload size and allowed MIME types (`pdf - docx - md - txt`) on `POST /api/documents`; reject path traversal in filenames.

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
Permissions-Policy: (minimised - camera=(), microphone=(), geolocation=() ...)
```

- The no-bundle ESM design needs **no `unsafe-inline`** for scripts - the import map and
  `<script type="module" src>` are external - so `script-src 'self'` holds as-is
  ([ui-components](ui-components.md#6-devrelease-pipeline-no-npm)).
- **`connect-src` is the exfiltration brake**: it lists only the API origin and Pocket ID. A token
  stolen by injected script has nowhere to be sent.
- The HTML/SVG artifact iframe is `srcdoc` with its **own** restrictive CSP and a `sandbox`
  attribute that omits `allow-same-origin` ([ui-components](ui-components.md#security-token-handling--rendering)).

### Logging format (shared)

The **.NET app and the Python tooling/mocks** emit **one JSON object per line** (NDJSON) to
`stdout`, with **timestamp and level first**, so a single parser handles every process in the
deployment:

```json
{"ts":"2026-06-02T12:34:56.789Z","level":"info","msg":"chat stream complete","comp":"chat","req":"01J8...","uid":"3f9a8c...","dur_ms":142}
```

| Field | Always | Meaning |
|-------|:------:|---------|
| `ts` | yes (1st) | ISO-8601 **UTC**, millisecond precision. |
| `level` | yes (2nd) | `debug` - `info` - `warn` - `error` (lowercase). |
| `msg` | yes | Human-readable event. |
| `comp` | yes | Component/category - `chat` - `ingest` - `auth` - `admin` - `mock.vllm` - `smoke` ... |
| `req` | - | Correlation id for one request/run. |
| `uid` | - | **Identity hash** - a prefix of the folder key `sha256(iss+sub)`. Correlates a user without exposing identity. |
| *(event)* | - | Anything else the event needs: `dur_ms`, `tool`, `status`, `doc_id`, ... |

**Never logged** (security): raw access/refresh **tokens**, raw `sub`, **email**, or message/document
**content**. A user is only ever identified by the `uid` hash - the same key the filesystem uses, so
logs line up with folders and the admin API without leaking identity.

> **The one deliberate exception - the `Debug` wire trace.** At `Debug`, `OpenAIWireLogger`
> traces the actual `/v1/chat/completions` request body Gert sends upstream - so sampling params
> and the tools block can be tuned ([installation/configuration.md section 14](../installation/configuration.md#14-logging---verbosity)).
> That body carries message **content**. The api-key bearer is still redacted; content is not. So
> production runs at `Information`, where the trace is silent - `Debug` is a local tuning mode only.

- **.NET** - **Serilog** with a small `ITextFormatter` (or `Microsoft.Extensions.Logging`'s
  `AddJsonConsole` + a custom `ConsoleFormatter`) that serialises `ts`/`level` first; `comp`/`req`/`uid`
  come from a logging scope set in middleware.
- **Python** - the stdlib `logging` module with a custom `Formatter` that `json.dumps` an ordered
  dict (`ts`, `level`, `msg`, ... - 3.7+ preserves insertion order); used by `run.py`, `tokens.py`, and
  `tools/smoke/mocks` so mock-upstream lines interleave cleanly with the host's.
