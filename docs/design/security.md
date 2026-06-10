# Security — threat model & controls

The other docs each carry their own security reasoning ([principles](principles.md),
[auth](auth.md), [storage-and-data](storage-and-data.md), [chat-and-tools](chat-and-tools.md)).
This page consolidates it: the **trust boundaries**, an **asset → threat → control** table, and
the **residual risks we knowingly accept**. It is the home for the cross-cutting controls that
don't belong to a single feature — CSP, SSRF, parser hardening — and the place an auditor starts.

> **One-line posture:** isolation is *structural* (token → folder, no cross-user query exists), so
> the residual attack surface is **content the box renders or fetches** — LLM/user HTML in the
> browser, and URLs/files the server pulls in. Most controls below harden that surface.

---

## 1. Trust boundaries

```
   Browser (untrusted JS runtime, holds a bearer token)
     │  HTTPS, Bearer header, single origin
     ▼
   Gert.Api  ── validates JWT (Pocket ID JWKS) ── derives folder from sub ──┐
     │                                                                       │
     ├─▶ Filesystem  /data/users/{key}/projects/{pid}/…   (the data boundary)│
     ├─▶ vLLM        (chat + embeddings)         server-side only            │
     ├─▶ SearXNG     (web search) ── optional outbound fetch  ◀── SSRF edge  │
     └─▶ gVisor      (run_python) ── code exec   ◀── strongest blast radius  ┘
```

Four boundaries matter:

1. **Browser ↔ Api** — everything from the browser is untrusted, including a *valid* token's
   request body. Bearer-in-header (not cookies) means no CSRF; single-origin means no CORS surface.
2. **User ↔ user / project ↔ project** — enforced by the filesystem, not a filter
   ([principle #2](principles.md)). Nothing below re-litigates this; it's the one boundary that
   can't be "forgotten in a WHERE clause."
3. **Api ↔ external content** — web pages, search results, and uploaded files are untrusted
   *content* the server parses, fetches, or feeds to the model. This is the SSRF / XXE / parser
   surface.
4. **Model output ↔ browser** — LLM text, Markdown, HTML/SVG artifacts are untrusted *output*
   the browser renders. This is the XSS surface.

---

## 2. Asset → threat → control

| Asset | Threat | Control | Where |
|-------|--------|---------|-------|
| A user's data | IDOR — read/write another user | Key derived **only** from token `sub`; isolation is filesystem-structural | [principles #2/#3](principles.md), [auth](auth.md#authorization-matrix) |
| A user's data | Path traversal via `pid` | `pid` validated to UUID/`default`, joined only under the token folder | [configuration §2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe) |
| A user's data | **Identifier reuse** — a recreated/reassigned identity inherits a folder | Anchor on stable `sub` (not email); key = `sha256(iss+sub)`; validate-before-disk | [§3 / F12](#3-findings--remediations), [decisions §3](decisions.md#3-folder-key) |
| **All users' data** | **Path traversal via admin `{key}`** | `{key}` must match `^[0-9a-f]{64}$`; resolved path asserted under `/data/users` before `rm -rf` | [§3 / F6](#3-findings--remediations), [rest-api](rest-api.md#admin-requires-admin-policy) |
| Bearer token | Theft via XSS | CSP + sanitized rendering + sandboxed artifacts; token kept out of long-lived storage where possible | [§3 / F1-F4](#3-findings--remediations) |
| Tool capability | Privilege escalation via UI toggle | `gert_tools` JWT entitlement is the hard ceiling; intersect before advertising | [auth](auth.md#enforcement--the-claim-is-the-ceiling) |
| Internal network | SSRF via web-search fetch | Block private/link-local/loopback + non-HTTP(S); no redirect into them; size/time cap | [§3 / F5](#3-findings--remediations), [chat-and-tools](chat-and-tools.md#web-search-searxng) |
| The host | RCE / escape via `run_python` | gVisor: no `/data` mount, read-only rootfs, **egress off by default**, CPU/mem/PID/wall caps, ephemeral | [chat-and-tools](chat-and-tools.md#sandbox-gvisor--security-critical) |
| Ingestion worker / host | XXE · zip-bomb · **memory-corruption in parsers** | Extraction in an **isolated unprivileged subprocess** (dropped privs, no net, `RLIMIT_*` + timeout); DTD/external-entities **off**; decompressed-size & entry caps | [§3 / F7](#3-findings--remediations), [tech-stack](tech-stack.md) |
| Upload storage | Path traversal / overwrite via filename | Store under server-generated `{doc-id}.{ext}`; reject separators/`..`; extension allowlist | [operations](operations.md#cross-cutting-concerns), [testing §5](testing.md#validation--the-input-security-boundary) |
| Any service input | Malformed/abusive payload | Fail-closed `IValidationProvider` in the service layer (API + Console); reflection meta-test | [principle #6](principles.md), [testing §5](testing.md#validation--the-input-security-boundary) |
| Session | Stolen/leaked token replay | HTTPS/HSTS; ~1h token lifetime (+ IdP deactivation); `RS256`-pinned validation. No denylist — stateless, multi-instance-safe | [§3 / F9,F11](#3-findings--remediations), [decisions §4](decisions.md#4-token-lifetime--revocation) |
| Provider keys / secrets | Leak via committed config | Secrets from env / `dotnet user-secrets` / secret store — never committed | [§3 / F8](#3-findings--remediations), [tech-stack](tech-stack.md) |
| The box | Resource exhaustion (GPU, embeddings, exec) | Per-user concurrency/rate caps on chat, ingestion, sandbox | [§3 / F10](#3-findings--remediations) |

---

## 3. Findings & remediations

### F1 — Content-Security-Policy & security headers
The app renders LLM/user-authored HTML, SVG, and Markdown while holding a bearer token in the
browser, so a strict CSP is the highest-value single control. Gert.Api emits, on every HTML
response:

- `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' <pocket-id-origin>; frame-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`
- `X-Content-Type-Options: nosniff` · `Referrer-Policy: no-referrer` · `X-Frame-Options: DENY`
  (belt-and-braces with `frame-ancestors`) · `Permissions-Policy` minimised.

The no-bundle ESM design already needs **no `unsafe-inline`** for scripts (the import map and
`<script type="module" src>` are external), so `script-src 'self'` holds. `connect-src` is the
exfiltration brake — it must list the API origin and Pocket ID, nothing else. The HTML/SVG
artifact iframe is `srcdoc` with its **own** restrictive CSP (see F3). → [operations](operations.md#http-security-headers--csp).

### F2 — Token storage
Keeping the access token in `localStorage` means any XSS reads it. Posture:

- **Access token in memory only** (a module variable in `services/auth.js`), never `localStorage`.
- **Refresh handled by Pocket ID's session**; if a refresh token must persist client-side, prefer
  an `httpOnly; Secure; SameSite=Strict` cookie over `localStorage`.
- CSP (F1) + sanitized rendering (F4) + sandboxed artifacts (F3) are the compensating controls; the
  short token lifetime (F9) caps the damage of a leak. → [ui-components](ui-components.md#security-token-handling--rendering).

### F3 — SVG/HTML artifact rendering
The HTML artifact already renders in a **sandboxed `<iframe srcdoc>`**. The **SVG artifact must use
the same path** — inline `<svg>` can carry `<script>`/`onload` that executes in the app origin and
steals the token. Both artifact iframes use `sandbox="allow-scripts"` **without `allow-same-origin`**
(the two together defeat the sandbox), plus a restrictive `csp` on the frame. Source-view shows the
raw text, never an injected live node. → [ui-components](ui-components.md#security-token-handling--rendering).

### F4 — Markdown sanitization
Assistant messages and the Markdown artifact render model output. The renderer runs with **raw HTML
disabled** (or output passed through a sanitizer allow-list), `javascript:`/`data:` URLs stripped,
and external links forced to `rel="noopener noreferrer" target="_blank"`. VanJS text bindings escape
by default, but any "render HTML" path is the exception that needs this. → [ui-components](ui-components.md#security-token-handling--rendering).

### F5 — SSRF in web-search fetch
`web_search` may fetch result pages server-side to summarize them. The fetched URL is attacker-
influenceable (search results, or prompt-injected document content steering the model's query), so
the fetcher:

- allows **`http`/`https` only**; rejects `file:`, `gopher:`, etc.;
- resolves the host and **blocks private, loopback, link-local, and unique-local ranges**
  (incl. IPv6 and the cloud metadata IP), re-checking **after each redirect**;
- caps response size, time, and redirect count.

The same egress reasoning makes the sandbox's outbound network **off by default**
([chat-and-tools](chat-and-tools.md#sandbox-gvisor--security-critical)). → [chat-and-tools](chat-and-tools.md#web-search-searxng).

### F6 — Admin `{key}` path validation
`DELETE /api/admin/users/{key}` and `GET …/{key}` feed `{key}` into a `/data/users/{key}` path that
is `rm -rf`'d — the most destructive operation in the system. `{key}` must be validated to
`^[0-9a-f]{64}$` (a sha256 hex) **before** path-joining, and the resolved absolute path asserted to
sit under `/data/users/` before any delete. This is the admin analog of the `pid` rule and is **not**
covered by [configuration §2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe). → [rest-api](rest-api.md#admin-requires-admin-policy), tested in [testing §5/§6](testing.md#validation--the-input-security-boundary).

### F7 — Upload parsing in an isolated, unprivileged subprocess (XXE / zip-bomb / memory-corruption)
PDF and DOCX parsers (PdfPig, OpenXML, and their native/managed internals) are a large attack
surface fed **raw untrusted bytes** — a memory-corruption or resource-exhaustion bug there cannot be
fully neutralised in-process, so it must not run *in* the API/worker process. Extraction runs
out-of-process in a locked-down helper:

- **Separate, short-lived process** per document — not the API/ingestion-worker process.
- **Dropped privileges:** unprivileged uid (`nobody`-class), no ambient capabilities, empty working
  dir; **no network**; read-only access to the single input file; it writes only extracted
  text/JSON to stdout.
- **Hard resource caps:** address space (`RLIMIT_AS`), CPU time (`RLIMIT_CPU`), output/file size,
  and no fork (`RLIMIT_NPROC`), plus a **wall-clock timeout that kills the process**.
- **In-process XML hardening still applies inside it:** DTD + external-entity resolution **off**
  (XXE), and **decompressed-size + zip-entry caps** for the DOCX zip (bombs).

A crash, OOM, or timeout fails **that document** (`status='failed'`), never the host. On Linux this
can reuse the same **gVisor (`runsc`)** isolation as the sandbox tool, or be a plain
`seccomp`+`rlimits` child; the Console may extract in-process for single-user local use. This is the
ingestion analog of [the `run_python` sandbox](chat-and-tools.md#sandbox-gvisor--security-critical).
→ [tech-stack](tech-stack.md), [chat-and-tools](chat-and-tools.md#document-ingestion-pipeline).

### F8 — Secrets handling
vLLM/SearXNG/provider keys are configuration, not source. Real values come from environment
variables / `dotnet user-secrets` (dev) / a secret store (prod); `appsettings.json` carries only
non-secret defaults and placeholders. Mirrors the dev-JWT-key discipline (generated, git-ignored).
→ [tech-stack](tech-stack.md), [operations](operations.md#cross-cutting-concerns).

### F9 — TLS / HSTS required
Bearer tokens and WebAuthn both require a secure context. Gert is deployed behind a TLS-terminating
reverse proxy; the app sets HSTS and assumes HTTPS-only. → [operations](operations.md#cross-cutting-concerns).

### F10 — Resource-exhaustion rate limits
Chat (GPU), ingestion (embeddings), and sandbox (exec) are expensive and otherwise uncapped per
user. Apply per-user concurrency/rate caps (ASP.NET rate limiter) so one client — or one stolen
token — can't saturate the box. Low likelihood at ~20 trusted users; cheap to add. → [operations](operations.md#cross-cutting-concerns).

### F11 — JWT algorithm pinning
Pin `TokenValidationParameters.ValidAlgorithms` to `["RS256"]` so a future JWKS quirk can't enable
alg-confusion or `none`. → [auth](auth.md#aspnet-core-wiring).

### F12 — Folder-root derivation: anti-reuse & validate-before-disk
The user folder is the root of all isolation, so its derivation is itself a control:

- **Anchor on the stable `sub`, namespaced by issuer** — key = `sha256(iss + "\n" + sub)`. `sub` is
  Pocket ID's UUID: never renamed, never recycled. **Email is explicitly rejected as the anchor** —
  it is mutable (rename orphans the folder) and *recycled* (a reassigned address would inherit the
  prior owner's data, which is the very reuse attack we're closing). `iss` namespacing future-proofs
  a second IdP.
- **Collision isn't the threat; reuse is.** `sha256` makes cross-user collision infeasible for *any*
  input — so switching the input doesn't change collision odds. The live risk is an identifier
  recurring for a different human, which the `sub` anchor closes by construction.
- **Validate before touching disk** ([principle #6](principles.md)): provisioning asserts `iss` ==
  configured authority, `aud` == `gert-api`, and a bounded/well-formed `sub` **before** any
  path-derive or `mkdir`. No valid identity → no folder.
- **Past the gate, the validated JWT is trusted.** The folder key derives from the token and
  nothing else, so there is no disk-side state to re-check per request. `user.db`'s `user_meta`
  row is *descriptive* — it records the username so the admin scan can map the opaque hash
  folder to a person (refreshed from the token when it changes in the IdP), and each database's
  `PRAGMA user_version` is its own migration anchor. Nothing on disk is ever used as an identity
  gate (a per-request equality check could only ever fire on a sha256 collision or local file
  tampering — neither is a real threat once the IdP is trusted). → [decisions §3](decisions.md#3-folder-key),
  [storage-and-data](storage-and-data.md#lazy-provisioning--migrations), [auth](auth.md#the-user-context-resolved-per-request).

---

## 4. Residual risks we accept

- **Prompt injection is self-scoped.** A malicious document or web page can steer *this* user's
  model turn, but isolation means it can only touch the user's own data and own-entitlement tools —
  there is no cross-tenant blast radius. The real residual is **exfiltration via outbound channels**
  (web-search fetch, sandbox egress), which F5 and the sandbox egress-off default address, and
  **persistence via memory**, which is why memory ships `manual`-only first
  ([configuration §9](configuration.md#9-open-decisions)) — `auto` waits for a review/undo UI.
- **~1-hour revocation window.** Pocket ID's access-token lifetime isn't shortenable
  ([decisions §4](decisions.md#4-token-lifetime--revocation)); routine off-boarding is effective
  within ~1h. There is no denylist (it would break multi-instance); sub-hour revocation means a shorter IdP token lifetime.
- **No load/perf or pixel-diff testing** — out of scope at ~20 users ([testing §12](testing.md#12-non-goals)).
- **Trusted-operator admin.** The admin surface is two endpoints (enumerate, `rm -rf`) gated by the
  group claim; we trust the operator and rely on F6 to keep even a fat-fingered/forged `{key}` from
  escaping `/data/users`.
