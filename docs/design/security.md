# Security - threat model & controls

The other docs each carry their own security reasoning ([principles](principles.md),
[auth](auth.md), [storage-and-data](storage-and-data.md), [chat-and-tools](chat-and-tools.md)).
This page consolidates it: the **trust boundaries**, an **asset -> threat -> control** table, and
the **residual risks we knowingly accept**. It is the home for the cross-cutting controls that
don't belong to a single feature - CSP, SSRF, parser hardening - and the place an auditor starts.

> **One-line posture:** isolation is *structural* (the store is opened from the token, so no
> application cross-user query exists), so
> the residual attack surface is **content the box renders or fetches** - LLM/user HTML in the
> browser, and URLs/files the server pulls in. Most controls below harden that surface.

---

## 1. Trust boundaries

```
   Browser (untrusted JS runtime, holds a bearer token)
     │  HTTPS, Bearer header, single origin
     ▼
   Gert.Api  ── validates JWT (Pocket ID JWKS) ── derives folder from sub ──┐
     │                                                                       │
     ├─▶ Filesystem  /data/users/{key}/projects/{pid}/...   (the data boundary)│
     ├─▶ vLLM        (chat + embeddings)         server-side only            │
     ├─▶ SearXNG     (web search) ── optional outbound fetch  ◀── SSRF edge  │
     └─▶ Sandbox     (run_python) ── code exec   ◀── strongest blast radius  ┘
```

Four boundaries matter:

1. **Browser <-> Api** - everything from the browser is untrusted, including a *valid* token's
   request body. Bearer-in-header (not cookies) means no CSRF; single-origin means no CORS surface.
2. **User <-> user / project <-> project** - enforced by the data layer (a per-user database
   opened from the token), not an application filter ([principle #2](principles.md)). Nothing
   below re-litigates this; it's the one boundary that can't be "forgotten in a WHERE clause."
3. **Api <-> external content** - web pages, search results, and uploaded files are untrusted
   *content* the server parses, fetches, or feeds to the model. This is the SSRF / XXE / parser
   surface.
4. **Model output <-> browser** - LLM text, Markdown, HTML/SVG artifacts are untrusted *output*
   the browser renders. This is the XSS surface.

---

## 2. Asset -> threat -> control

| Asset | Threat | Control | Where |
|-------|--------|---------|-------|
| A user's data | IDOR - read/write another user | Key derived **only** from token `sub`; isolation is database-structural (a per-user store opened by token, no application query filter) | [principles #2/#3](principles.md), [auth](auth.md#authorization-matrix) |
| A user's data | Path traversal via `pid` | `pid` validated to UUID/`default`, joined only under the token folder | [configuration section 2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe) |
| A user's data | **Identifier reuse** - a recreated/reassigned identity inherits a folder | Anchor on stable `sub` (not email); key = `sha256(iss+sub)`; validate-before-disk | [section 3 / F12](#3-findings--remediations), [decisions section 3](decisions.md#3-user-key) |
| **All users' data** | **Path traversal via admin `{key}`** | `{key}` must match `^[0-9a-f]{64}$`; resolved path asserted under `/data/users` before any delete | [section 3 / F6](#3-findings--remediations), [rest-api](rest-api.md#admin-requires-admin-policy) |
| Bearer token | Theft via XSS | CSP + sanitized rendering + sandboxed artifacts; token kept out of long-lived storage where possible | [section 3 / F1-F4](#3-findings--remediations) |
| Tool capability | Privilege escalation via UI toggle | `gert_tools` JWT entitlement is the hard ceiling; intersect before advertising | [auth](auth.md#enforcement---the-claim-is-the-ceiling) |
| Internal network | SSRF via web-search fetch | Block private/link-local/loopback + non-HTTP(S); no redirect into them; size/time cap | [section 3 / F5](#3-findings--remediations), [chat-and-tools](chat-and-tools.md#web-search-searxng) |
| The host | RCE / escape via `run_python` | Behind `IPythonSandbox`: **monty** (Rust Python, no syscalls) in an unprivileged, no-`/data`, egress-off sidecar by default - or **gVisor** (ephemeral container); both with mem/wall caps | [chat-and-tools](chat-and-tools.md#sandbox---security-critical) |
| Ingestion worker / host | XXE - zip-bomb - **memory-corruption in parsers** | Extraction in an **isolated unprivileged subprocess** (dropped privs, no net, `RLIMIT_*` + timeout); DTD/external-entities **off**; decompressed-size & entry caps | [section 3 / F7](#3-findings--remediations), [tech-stack](tech-stack.md) |
| Upload storage | Path traversal / overwrite via filename | Store under a fully server-generated `files/{doc-id}` key (a UUID, **no extension**) - the caller's filename never reaches a storage path; it is base64 DB metadata only. Extension allowlist gates the file *type* | [operations](operations.md#cross-cutting-concerns), [testing section 5](testing.md#validation---the-input-security-boundary) |
| Any service input | Malformed/abusive payload | Fail-closed `IValidationProvider` in the service layer (every caller); reflection meta-test | [principle #6](principles.md), [testing section 5](testing.md#validation---the-input-security-boundary) |
| Session | Stolen/leaked token replay | HTTPS/HSTS; ~1h token lifetime (+ IdP deactivation); `RS256`-pinned validation. No denylist - stateless, multi-instance-safe | [section 3 / F9,F11](#3-findings--remediations), [decisions section 4](decisions.md#4-token-lifetime--revocation) |
| Provider keys / secrets | Leak via committed config | Secrets from env / `dotnet user-secrets` / secret store - never committed | [section 3 / F8](#3-findings--remediations), [tech-stack](tech-stack.md) |
| The box | Resource exhaustion (GPU, embeddings, exec) | Per-user concurrency/rate caps on chat, ingestion, sandbox | [section 3 / F10](#3-findings--remediations) |

---

## 3. Findings & remediations

### F1 - Content-Security-Policy & security headers
The app renders LLM/user-authored HTML, SVG, and Markdown while holding a bearer token in the
browser, so a strict CSP is the highest-value single control. Gert.Api emits, on every HTML
response:

- `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data:; connect-src 'self' <pocket-id-origin>; frame-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'`
- `X-Content-Type-Options: nosniff` - `Referrer-Policy: no-referrer` - `X-Frame-Options: DENY`
  (belt-and-braces with `frame-ancestors`) - `Permissions-Policy` minimised.

The no-bundle ESM design already needs **no `unsafe-inline`** for scripts: every import is an
absolute same-origin path (e.g. `/lib/van.js`), so there are no bare specifiers and **no inline
`<script type="importmap">`** - just external `<script type="module" src>`. `script-src 'self'`
therefore needs **no `sha256` hash** (there is no inline script to whitelist), which is why
`SecurityHeadersMiddleware.cs` carries no import-map-hash constant and the publish bundler (which
leaves `index.html` with just the one external module script) has nothing to invalidate. `connect-src` is the
exfiltration brake - it must list the API origin and Pocket ID, nothing else. The HTML/SVG
artifact iframe is `srcdoc` with its **own** restrictive CSP (see F3). -> [operations](operations.md#http-security-headers--csp).

### F2 - Token storage
Keeping the access token in `localStorage` means any XSS reads it. Posture:

- **Access token in memory only** (a module variable in `services/auth.js`), never `localStorage`.
- **Refresh handled by Pocket ID's session**; if a refresh token must persist client-side, prefer
  an `httpOnly; Secure; SameSite=Strict` cookie over `localStorage`.
- CSP (F1) + sanitized rendering (F4) + sandboxed artifacts (F3) are the compensating controls; the
  short token lifetime (F9) caps the damage of a leak. -> [ui-components](ui-components.md#security-token-handling--rendering).

### F3 - SVG/HTML artifact rendering
The HTML artifact already renders in a **sandboxed `<iframe srcdoc>`**. The **SVG artifact must use
the same path** - inline `<svg>` can carry `<script>`/`onload` that executes in the app origin and
steals the token. Both artifact iframes use `sandbox="allow-scripts"` **without `allow-same-origin`**
(the two together defeat the sandbox), plus a restrictive `csp` on the frame. Source-view shows the
raw text, never an injected live node. -> [ui-components](ui-components.md#security-token-handling--rendering).

### F4 - Markdown sanitization
Assistant messages and the Markdown artifact render model output through `lib/markdown.js`, an
**in-house renderer** (no third-party Markdown engine). `markdown.js` is a thin facade that wires
`parse -> render -> assignHeadingIds` inside one `try/catch` (any fault degrades to literal source, so
`renderMarkdown` is **total**) and re-exports the public surface; the engine lives under `lib/render/`.
Parsing is bounded: `render/lines.js` classifies each line **once** (the declarative `LINE_KINDS` table,
which feeds both the block dispatcher and the paragraph-interrupt) into a **bounded** block parse
(`MAX_NEST` = 32; past the cap a would-be container is plain text), and `render/inline.js` runs an O(n)
left-to-right inline scan bounded by `MAX_INLINE`/`MAX_DEST`/`MAX_TITLE` - so adversarial nesting can't
blow the stack. Math and code are **opaque leaves** in the AST (raw latex/code, never re-parsed as markup).

The structural renderer (`render/dom.js`) emits **every** markdown element through **one guarded
`createEl(ns, tag, attrs)` chokepoint** over a **closed per-`(ns, tag)` allow-list**: each tag's permitted
attribute set is pinned (`href` only on `<a>`, `src` only on `<img>`; td/th alignment is a CSSOM
`el.style.textAlign` write, **not** an attribute), and any unknown `(ns, tag)` or attribute is a
**fail-closed throw** (caught by the facade's literal-source fallback). The producible node set is the
**closed `NODE_TYPES`** set (the renderer's `switch` has a `default: throw`). So the output is a fixed
allow-list and `innerHTML` is **never** used; there is no "raw HTML" path to disable and no sanitizer to
trust. URLs funnel through **one `sanitizeUrl()` source** (`render/url.js`, shared by the renderer and the
external-link UI gate): `javascript:`/`data:`/`vbscript:`, plus control-char and `&colon;` smuggling -> `#`;
external links are forced to `rel="noopener noreferrer" target="_blank"` (applied locally at the link node).
**Image sources are restricted to inline `data:image/(png|jpe?g|gif|webp|avif|bmp|x-icon);base64`;
EVERY url-shaped src - cross-origin, same-origin, *and* relative - collapses to `#`** *in the renderer*
(`sanitizeImgUrl`), not relying on CSP `img-src` (F1) alone. Images are the one markdown construct that
auto-fetches, and the model can't author a working app asset URL, so a prompt-injected
`![](https://attacker/x)` (IP-beacon) or `![](/c/assets/x.png)` (same-origin probe / 401 noise) only ever
fires a doomed request with zero legitimate use - the app never renders trusted markdown that points at its
own images by URL. Inline `data:image` (a generated chart) is the only real image case and is kept; CSP
`img-src` is the backstop, not the gate. The renderer returns a `DocumentFragment` of allow-listed nodes
that the app appends directly, so model text never flows through an HTML-parsing sink.
-> [ui-components](ui-components.md#security-token-handling--rendering).

**Code and math leaves** are VanJS components (`md-code.js` / `md-math.js`) the structural renderer
**calls** and inserts the returned DOM from; each builds nodes with `createElement`/`createElementNS`
(**never** `van.tags` - it has no allow-list - and never `innerHTML`):
- **`MdCode`** wraps `lib/highlight.js`: `<pre data-lang><code>…</code></pre>` where `<code>` holds **only**
  inert `tok-*` spans + text (tinted from `textContent`; no attribute but `class`, no class outside `tok-*`).
  `data-lang` stays guarded to `/^[\w+#.-]{1,16}$/` and only ever lands in `dataset`.
- **`MdMath`** wraps `lib/smath.js`: it keeps smath's **closed `MML_ELEMENTS` allow-list** and its
  **per-formula `try/catch`**, so bad TeX degrades to literal text **per formula**, never document-wide.

**Math (`$...$` / `\(...\)` inline, `$$...$$` / `\[...\]` display)** keeps the same stance with **no
third-party engine**: `lib/smath.js` is an in-house, zero-dependency TeX -> native MathML converter
(not Temml, not KaTeX). A **linear lexer** (O(n), no ReDoS) feeds a **bounded recursive descent**
(`MAX_DEPTH` = 32, `MAX_NODES` = 6000, `MAX_TEX` = 8192; past any bound it degrades to the literal
source, so adversarial input can't recurse or balloon the node count) that is **total** over a
**closed `MML_ELEMENTS` allow-list** built with `createElementNS` - `innerHTML` is never used, and an
unknown control word degrades to a visible `<mtext>` rather than failing or emitting anything live.
The browser renders the resulting `<math>` **natively** (MathML Core: Firefox, Chromium 109+, WebKit).
The **only** attributes the converter ever sets are inert MathML presentation hints
(`mathvariant`/`stretchy`/`fence`/`accent`/`displaystyle`/`movablelimits`/`width`/`mathcolor`) - there is
**no `href`/`src`/`style` sink**, so a model formula cannot navigate, fetch, or script, and it emits **no
inline `style`** (so nothing relies on `'unsafe-inline'` and `style-src 'self'` holds). `\color`/`\textcolor`
set `mathcolor` - a presentation *attribute*, charset-validated to a `#hex`/colour-name before it lands,
never a `style` - and `\ce`/`\pu` (an mhchem subset) lower chemistry onto the **same** closed allow-list,
so neither escapes it. No third-party code touches model output. Verified in the markdown gallery (`test_markdown_gallery_all_self_checks_pass`
in `tools/smoke/tests/test_components.py`), which renders a battery of inputs through the real
`lib/markdown.js` + `lib/smath.js` in a browser and self-checks the F4 stance, anchors, and native math.

### F5 - SSRF in web-search fetch
`web_search` may fetch result pages server-side to summarize them. The fetched URL is attacker-
influenceable (search results, or prompt-injected document content steering the model's query), so
the fetcher:

- allows **`http`/`https` only**; rejects `file:`, `gopher:`, etc.;
- is **IPv4-only for now** - any non-IPv4 destination (IPv6 in every form, including
  IPv4-mapped and NAT64-embedded) is refused outright, which also removes the
  IPv4-mapped / NAT64 unwrap bypasses by construction;
- resolves the host and **blocks private, loopback, link-local, CGNAT, multicast,
  reserved, broadcast, and documentation/benchmark ranges** (incl. the cloud metadata IP
  `169.254.169.254`), re-checking **after each redirect**;
- restricts the destination port to **80 / 443** (the only ports it ever speaks HTTP(S)
  over), so the fetch can't be turned into a probe of non-web services (Redis, Postgres,
  an internal admin API) on an otherwise-public host;
- caps response size, time, and redirect count.

The same egress reasoning keeps the sandbox's outbound network closed - **absent entirely**
under monty (no network exists in the language), **off by default** under gVisor
([chat-and-tools](chat-and-tools.md#sandbox---security-critical)). -> [chat-and-tools](chat-and-tools.md#web-search-searxng).

Tested in `tests/Gert.Tools.Tests`: `SsrfGuardTests` (the pure URL/IP policy),
`SafeHttpFetcherTests` (pre-socket vetting: scheme, malformed, private literal host), and
`SafeHttpFetcherRedirectTests` (the live-socket controls - per-hop redirect re-vet and the
connect-time DNS pin - against loopback listeners). The latter works through an **internal**
constructor seam on `SafeHttpFetcher` that injects the DNS resolver and per-address check;
the production wiring (real DNS + `SsrfGuard.IsIpAllowed`) is the public constructor's only
behaviour and is deliberately **not** a configuration knob, so the guard cannot be bypassed
by an operator setting.

### F6 - Admin `{key}` path validation
`DELETE /api/admin/users/{key}` and `GET .../{key}` feed `{key}` into a `/data/users/{key}` path used to
delete that user's data - the most destructive operation in the system. `{key}` must be validated to
`^[0-9a-f]{64}$` (a sha256 hex) **before** path-joining, and the resolved absolute path asserted to
sit under `/data/users/` before any delete. This is the admin analog of the `pid` rule and is **not**
covered by [configuration section 2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe). -> [rest-api](rest-api.md#admin-requires-admin-policy), tested in [testing section 5/section 6](testing.md#validation---the-input-security-boundary).

### F7 - Upload parsing in an isolated, unprivileged subprocess (XXE / zip-bomb / memory-corruption)
PDF, DOCX, and XLSX parsers (PdfPig, OpenXML, and their native/managed internals) are a large attack
surface fed **raw untrusted bytes** - a memory-corruption or resource-exhaustion bug there cannot be
fully neutralised in-process, so it must not run *in* the API/worker process. Extraction runs
out-of-process in a locked-down helper.

> **Upload posture (no type allowlist).** Gert accepts any file: the upload gate no longer
> allowlists by extension/MIME (only non-empty, size cap, and filename length remain). The
> bounding invariant is that **no in-process binary parser ever runs on an upload** - any type
> that is not a known binary document format (`pdf`/`docx`/`xlsx`) is only UTF-8-**decoded**
> in-process (cheap, safe; rejected as "not a text file" if the bytes are not text), and those
> three binary formats are parsed **only** inside this isolated helper. So widening accepted types
> does not widen the in-process attack surface. (`xlsx` is wired through this helper but, like
> `pdf`/`docx`, only extracts once the `gert-extract` helper binary ships.)

- **Separate, short-lived process** per document - not the API/ingestion-worker process.
- **Dropped privileges:** unprivileged uid (`nobody`-class), no ambient capabilities, empty working
  dir; **no network**; read-only access to the single input file; it writes only extracted
  text/JSON to stdout.
- **Hard resource caps:** address space (`RLIMIT_AS`), CPU time (`RLIMIT_CPU`), output/file size,
  and no fork (`RLIMIT_NPROC`), plus a **wall-clock timeout that kills the process**.
- **In-process XML hardening still applies inside it:** DTD + external-entity resolution **off**
  (XXE), and **decompressed-size + zip-entry caps** for the DOCX/XLSX zip (bombs).

A crash, OOM, or timeout fails **that document** (`status='failed'`), never the host. On Linux this
can reuse the same **gVisor (`runsc`)** isolation as the sandbox tool, or be a plain
`seccomp`+`rlimits` child. This is the
ingestion analog of [the `run_python` sandbox](chat-and-tools.md#sandbox---security-critical).
-> [tech-stack](tech-stack.md), [chat-and-tools](chat-and-tools.md#document-ingestion-pipeline).

### F8 - Secrets handling
vLLM/SearXNG/provider keys are configuration, not source. Real values come from environment
variables / `dotnet user-secrets` (dev) / a secret store (prod); `appsettings.json` carries only
non-secret defaults and placeholders. Mirrors the dev-JWT-key discipline (generated, git-ignored).
-> [tech-stack](tech-stack.md), [operations](operations.md#cross-cutting-concerns).

### F9 - TLS / HSTS required
Bearer tokens and WebAuthn both require a secure context. Gert is deployed behind a TLS-terminating
reverse proxy; the app sets HSTS and assumes HTTPS-only. -> [operations](operations.md#cross-cutting-concerns).

### F10 - Resource-exhaustion rate limits
Chat (GPU), ingestion (embeddings), and sandbox (exec) are expensive and otherwise uncapped per
user. Apply per-user concurrency/rate caps (ASP.NET rate limiter) so one client - or one stolen
token - can't saturate the box. Low likelihood at ~20 trusted users; cheap to add.
Implemented as a fixed-window limiter on the `/api/*` controller surface, partitioned by the
token `(iss, sub)` pair - the same identity anchor as the user folder key, so two IdPs minting
the same `sub` never share a bucket (remote IP for anonymous traffic); the limits bind from
`Gert:RateLimiting` (`PermitLimit`, default 600; `Window`, default 1 minute) and a rejection is
a branded 429 ProblemDetails. Tested in `tests/Gert.Api.Tests/RateLimitingTests.cs`: over-cap ->
branded 429, a throttled user never throttles another user (partition isolation - the actual
per-user semantics, including same-`sub`-different-`iss`), and the liveness probe stays outside
the limited surface. -> [operations](operations.md#cross-cutting-concerns).

### F11 - JWT algorithm pinning
Pin `TokenValidationParameters.ValidAlgorithms` to `["RS256"]` so a future JWKS quirk can't enable
alg-confusion or `none`. -> [auth](auth.md#aspnet-core-wiring).

### F12 - Folder-root derivation: anti-reuse & validate-before-disk
The user folder is the root of all isolation, so its derivation is itself a control:

- **Anchor on the stable `sub`, namespaced by issuer** - key = `sha256(iss + "\n" + sub)`. `sub` is
  Pocket ID's UUID: never renamed, never recycled. **Email is explicitly rejected as the anchor** -
  it is mutable (rename orphans the folder) and *recycled* (a reassigned address would inherit the
  prior owner's data, which is the very reuse attack we're closing). `iss` namespacing future-proofs
  a second IdP.
- **Collision isn't the threat; reuse is.** `sha256` makes cross-user collision infeasible for *any*
  input - so switching the input doesn't change collision odds. The live risk is an identifier
  recurring for a different human, which the `sub` anchor closes by construction.
- **Validate before touching disk** ([principle #6](principles.md)): provisioning asserts `iss` ==
  configured authority, `aud` == `gert-api`, and a bounded/well-formed `sub` **before** any
  path-derive or `mkdir`. No valid identity -> no folder.
- **Past the gate, the validated JWT is trusted.** The folder key derives from the token and
  nothing else, so there is no disk-side state to re-check per request. `user.db`'s `user_meta`
  row is *descriptive* - it records the username so the admin scan can map the opaque hash
  folder to a person (refreshed from the token when it changes in the IdP), and each database's
  `PRAGMA user_version` is its own migration anchor. Nothing on disk is ever used as an identity
  gate (a per-request equality check could only ever fire on a sha256 collision or local file
  tampering - neither is a real threat once the IdP is trusted). -> [decisions section 3](decisions.md#3-user-key),
  [storage-and-data](storage-and-data.md#lazy-provisioning--migrations), [auth](auth.md#the-user-context-resolved-per-request).

---

## 4. Residual risks we accept

These are the gaps we *knowingly* accept at ~20 trusted users today;
[defense-in-depth.md](defense-in-depth.md) is the forward wishlist for closing them (and shrinking
the blast radius of a process compromise) as the deployment grows.

- **Prompt injection is self-scoped.** A malicious document or web page can steer *this* user's
  model turn, but isolation means it can only touch the user's own data and own-entitlement tools -
  there is no cross-tenant blast radius. The real residual is **exfiltration via outbound channels**
  (web-search fetch, sandbox egress), which F5 and the sandbox egress-off default address.
- **~1-hour revocation window.** Pocket ID's access-token lifetime isn't shortenable
  ([decisions section 4](decisions.md#4-token-lifetime--revocation)); routine off-boarding is effective
  within ~1h. There is no denylist (it would break multi-instance); sub-hour revocation means a shorter IdP token lifetime.
- **No load/perf or pixel-diff testing** - out of scope at ~20 users ([testing section 11](testing.md#11-non-goals)).
- **Trusted-operator admin.** The admin surface is two endpoints (enumerate, delete a user) gated by the
  group claim; we trust the operator and rely on F6 to keep even a fat-fingered/forged `{key}` from
  escaping `/data/users`.
