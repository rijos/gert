# Testing plan

How Gert is tested top to bottom: a **fake in-memory world** the whole stack runs against,
**.NET whitebox tests** for every service/repository/validator, and a
**Python + headless-browser** smoke launcher that clicks through the real SPA as both an
admin and a normal user across **Chromium and Firefox**.

> **One-line strategy:** the service layer is host-agnostic, so we test logic without HTTP;
> the repositories are the only code that touches SQL, so we test them against **real**
> SQLite; and the two things that can only break in a browser - the SPA and the JWT/SSE
> wiring - get a headless end-to-end pass. One set of **fakes** (vLLM, SearXNG, sandbox)
> backs all of it, so a behaviour proven in a unit test is the same behaviour the browser sees.

This plan builds on the architecture in [tech-stack](tech-stack.md), the isolation rules in
[principles](principles.md), the auth model in [auth](auth.md), the folder model in
[storage-and-data](storage-and-data.md), the endpoints in [rest-api](rest-api.md), and the threat
model in [security](security.md).

---

## 1. What the architecture buys us

The design was built to be testable; the plan just cashes that in:

| Architectural fact | Testing consequence |
|--------------------|---------------------|
| `Gert.Service` references **only** `Gert.Model` - no `HttpContext`, JWT, or SSE ([tech-stack](tech-stack.md)) | The entire tool loop, ingestion pipeline, and orchestration are unit-testable with plain objects - no web host needed. |
| Streaming is `IAsyncEnumerable<ChatEvent>`; transport renders it ([tech-stack](tech-stack.md)) | We assert on the **event stream** directly in `Gert.Service.Tests`; SSE framing is tested once, separately, in `Gert.Api.Tests`. |
| Repository interfaces are the **only** code that sees SQL | We test `Gert.Database.Sqlite` against a **real** temp SQLite (vec0 + FTS5) - the only place SQL correctness can be proven. |
| Isolation is enforced by the **data layer** - a per-user store keyed from the token ([principles #2](principles.md)) | Isolation and IDOR become concrete assertions: mint two tokens, prove user B physically cannot reach user A's data. |

### Why the database stays real (the one asymmetry)
Everything behind `IGertServices` and the repository interfaces *can* be swapped for an
in-memory double - that's the whole point of the seam, and it's exactly what we do for the
**outside world** (vLLM, SearXNG, sandbox). But persistence is deliberately **not** faked:

- The RAG SQL is **engine-specific and cannot be abstracted into shared SQL**
  ([tech-stack -> Engine portability](tech-stack.md#engine-portability)). An in-memory repo
  would *reimplement* ranking in C# - testing the fake's ranking, not the real `vec0` + FTS5 +
  RRF retrieval, which is the riskiest code in the system.
- Isolation is a **data-layer** property ([principles #2](principles.md)), not something a fake
  repository can stand in for: an in-memory store has no per-user database to prove it with.

So the rule is: **fake the outside world; keep persistence real but temporary** - real SQLite
(vec0 + FTS5) in a throwaway `DataRoot` ([section 4.4](#44-per-user-temp-dataroot)). It's nearly as
fast as in-memory and loses none of the SQL or isolation coverage. In-memory repositories may
*complement* this for fast service-only tests, but never *replace* the real-SQLite tier.

---

## 2. The pyramid

```
        ┌───────────────────────────────────────────────┐
   E2E  │  Python launcher -> Playwright (Chromium+Firefox)│   slow, few
        │  admin + non-admin JWTs, real SPA, fake host    │
        ├───────────────────────────────────────────────┤
  HTTP  │  Gert.Api.Tests - WebApplicationFactory          │
        │  controllers - SSE - auth - IDOR - admin RBAC    │
        ├───────────────────────────────────────────────┤
   DB   │  Gert.Database.Sqlite.Tests - real temp SQLite   │
        │  vec0 + FTS5 - hybrid rank - migrations - isolation│
        ├───────────────────────────────────────────────┤
  Unit  │  Gert.Service.Tests - Authentication - Validation │   fast, many
        │  tool loop - ingestion - claim mapping - rules    │
        └───────────────────────────────────────────────┘
              all tiers share one set of fakes (Gert.Testing)
```

Most assertions live at the bottom two tiers (fast, deterministic). The browser tier proves
the wiring a unit test can't: that a minted JWT flows through the SPA, that SSE renders as a
streaming message, and that an admin sees `/admin/users` while a normal user gets a 403.

---

## 3. Test project layout

New projects extend the solution from [tech-stack -> Solution layout](tech-stack.md#solution-layout-projects):

```
tests/
  Gert.Testing/                 # shared infra, NO test cases - fakes, fixtures, factory, JWT mint
    Fakes/
      FakeChatModel.cs          #   OpenAI-compatible vLLM double: canned streaming + tool calls
      FakeEmbeddings.cs         #   deterministic vectors (hash -> 1024-dim) for stable KNN
      FakeWebSearch.cs          #   SearXNG double
      StubSandbox.cs            #   gVisor double - no container, scripted stdout/exit
    GertApiFactory.cs           #   WebApplicationFactory<Program> with all fakes + test JWT
    TempDataRoot.cs             #   per-test user-folder root under a temp dir; auto-cleanup
    TestTokens.cs               #   RSA dev key + JWKS; mint admin / user JWTs
    TestData/
      NaughtyStrings.cs         #   adversarial input corpus - fed across every string field (section 5)

  Gert.Service.Tests/           # whitebox - chat orchestrator/tool loop, conversations,
                                #   documents, ingestion pipeline, validation
  Gert.Database.Sqlite.Tests/   # repositories vs real temp SQLite (vec0 + FTS5); migrations; isolation
  Gert.Authentication.Tests/    # JWT claims -> IUserContext; sub->key (sha256); RS256 pin
  Gert.Chat.Tests/              # chat/embeddings adapter units - OpenAI client, provider catalog, Polly wiring
  Gert.Tools.Tests/             # tool adapter units - built-in tools, SSRF guard, sandbox invocation, backend selection
  Gert.Ingestion.Tests/         # extractor hardening units - XXE, zip-bomb, helper output
  Gert.Api.Tests/               # integration via GertApiFactory - controllers, SSE, auth, IDOR, admin, SPA fallback
  Gert.Web.Bundle.Tests/        # publish bundler: pinned esbuild manifest + index.html repoint
  web/
    harness.html                # __mount helper - Fake host serves it at /tests/ for component units (absolute same-origin imports, no import map)
  shared/                       # ONE source of truth for both fake layers (Appendix A)
    fixtures.json               #   canned chat completions + web-search results
    embeddings_golden.json      #   text -> expected vector - the deterministic-embedding conformance check

tools/
  smoke/                        # Python E2E launcher (uv-managed; no npm, no .NET) - drives the Fake host
    run.py                      #   boot mocks + host (FakeE2E) -> mint tokens -> Playwright matrix -> report
    tokens.py                   #   role->claims map; mint(role) RS256 via pyjwt; CLI for local dev
    proxy.py                    #   dev reverse-proxy: view the FakeE2E SPA in YOUR browser (make serve-mock)
    mocks/                      #   mock upstreams for E2E - the real Gert.Chat/Tools/Ingestion adapters point here
      __main__.py               #     boots all mocks on localhost ports (one process); shared specs
      vllm.py                   #     OpenAI-compatible: /v1/chat/completions (streaming + tool calls), /v1/embeddings
      searxng.py                #     SearXNG JSON; can emit a private-IP result URL to test the SSRF guard
      monty.py                  #     monty sandbox sidecar: POST /run, whitelisted-AST calculator
      specs.py                  #     canned completions + deterministic hash->1024-dim embedding (matches FakeEmbeddings)
    pyproject.toml + uv.lock    #   playwright, pyjwt, httpx, ruff, mypy - installed via `uv sync`
    pages.py                    #   page objects for the SPA regions (sidebar, composer, canvas)
    tests/
      test_components.py        #   component units - mount real modules via page.evaluate (section 7)
      test_chat.py              #   new chat -> send -> streaming -> tool cards -> citations
      test_knowledge.py         #   upload -> status pills -> use-in-chat toggle
      test_canvas.py            #   artifact tabs - rendered/source - html iframe - code problems
      test_rbac.py              #   admin sees /admin/users; user gets 403; IDOR blocked;
                                #   entitlement ceiling (limited) + absent-claim -> no tools
      test_chrome.py            #   theme toggle - responsive drawers - model picker
      test_style.py             #   style invariants: uniform artifact-header height, Preview/Source on
                                #   render/raw kinds (code = preview only), ask_user states, tokens, theme, css minify
      test_a11y.py              #   WCAG guards: role=switch, dialog focus, decorative icons, main+skip+title, live toasts
      test_llm_tools.py         #   artifacts, document retrieval, todos, clock through the tool loop
      test_auth_smoke.py        #   API auth smoke (httpx, no browser): invalid/missing tokens rejected
      test_embeddings_conformance.py  # Python embed(t) matches embeddings_golden.json (Appendix A.2)

.dev/                           # git-ignored - generated on first run, NEVER committed
  jwt/                          #   dev RSA keypair + dev-jwks.json (trusted only in Dev/Test)
```

The SPA (`Gert.Api/wwwroot`) is exercised by the browser tier (`tools/smoke`) rather than a JS unit runner -
see [section 7](#7-web-tests). Each `*.Tests` project references its target plus `Gert.Testing`.

---

## 4. Shared test infrastructure - `Gert.Testing`

One project owns the fakes and fixtures so every tier sees identical behaviour. It holds no
test cases - only the scaffolding the others consume.

### 4.1 The fake external world
The three things we can't run in CI - a GPU model server, a search engine, a gVisor sandbox -
get deterministic doubles. They implement the **same service-layer interfaces** the real
adapters do, so swapping them is a single DI registration ([tech-stack](tech-stack.md)).

- **`FakeChatModel`** - OpenAI-compatible double. Returns canned completions keyed by the
  last user message (fixture map; echo fallback), and can emit a scripted **tool call** so the
  orchestrator's tool loop is exercised end to end. Streams token-by-token so SSE and the
  typewriter caret have something real to render.
- **`FakeEmbeddings`** - maps text -> a deterministic 1024-dim unit vector (hash-seeded, per the
  exact algorithm in [Appendix A.2](#a2-deterministic-embeddings-hash---1024-dim-unit-vector)). KNN
  ordering is therefore **stable across runs** *and identical to the Python mock*, which is what lets
  us assert exact retrieval order in RAG tests instead of "something came back."
- **`FakeWebSearch`** - fixed result set for the web-search tool.
- **`StubSandbox`** - returns scripted stdout/exit without launching a container; a "throws"
  variant covers the sandbox-failure path.

### 4.2 Two ways to fake the outside world
The external world is doubled at **two fidelities**, chosen per tier - but both speak the *same*
scripted behaviour, so a result proven in a unit test is the result the browser sees:

| Tier | External world | Transport | How |
|------|----------------|-----------|-----|
| **.NET unit / integration** (`Gert.Service.Tests`, `Gert.Api.Tests`) | **in-process .NET fakes** (`AddGertFakes` swaps the adapter ports) | TestServer, no socket | `GertApiFactory : WebApplicationFactory<Program>` -> `HttpClient`. Fastest, fully deterministic. |
| **Browser E2E** (the Python launcher) | **real `Gert.Chat`/`Gert.Tools`/`Gert.Ingestion` adapters -> Python mock upstreams** (HTTP, incl. the monty sandbox sidecar) | real Kestrel on localhost | `dotnet run --launch-profile FakeE2E`: the host wires its **real** vLLM/SearXNG/monty clients but points them at the mock URLs `tools/smoke/mocks` serves. |

**Why two.** The in-process fakes give speed + determinism for the bulk of the suite. The Python
mocks give **wire-level fidelity** for the few browser runs: pointing the *real* adapters at a fake
upstream exercises the adapter code `AddGertFakes` skips - `IHttpClientFactory`/Polly, OpenAI request
shaping, **streaming SSE parsing of the upstream**, and the **SSRF guard** (a mock SearXNG can return
a private-IP result URL and assert the fetch is refused - [security F5](security.md#3-findings--remediations)).
The default **monty** sandbox backend is an HTTP sidecar, so it gets the same treatment: the real
`MontySandbox` adapter points at `mocks/monty.py`. The **gVisor** backend is local process-exec
with no wire protocol to mock - real gVisor is exercised only in the staging smoke
([section 11](#11-non-goals)).

**No drift.** The Python `mocks/` and the .NET fakes share one documented spec
([Appendix A](#appendix-a---the-shared-fake-spec)) - the same canned completions keyed by
last-user-message, and the **same deterministic hash->1024-dim embedding algorithm** - so KNN/RRF
order and citations assert identically in `Gert.Database.Sqlite.Tests` and in the browser. Both shortcuts also share the dev JWT key path ([section 4.3](#43-jwt-minting---a-python-token-harness)),
point `DataRoot` at a temp dir, and install the test JWT validation; the only differences are the
socket and which fidelity of external world is wired.

### 4.3 JWT minting - a Python token harness
No dev-only token *endpoint* on the host. **Python mints the tokens**; the host only *trusts*
a dev key, and validates through the **same** RS256/JWKS path it uses for Pocket ID in prod -
so the dev shortcut can't hide a validation bug.

- **`tools/smoke/tokens.py`** - a small harness with a role->claims map and a `mint(role,
  **overrides)` function. Signs RS256 with a **dev keypair** using `pyjwt`. Roles are just data,
  so adding a privilege set is a one-line edit:

  ```python
  # role -> the claims that distinguish it; mint() adds iss/aud/exp/iat/nbf to match the dev authority
  # (iss matters: the folder key is sha256(iss+sub) and the provisioning gate checks iss - section 4.4 / F12).
  ROLES = {
      "admin":   {"sub": "dev-admin",   "groups": ["gert-admins"], "gert_tools": "*"},          # admin surface + every tool
      "user":    {"sub": "dev-user",    "groups": ["gert-users"],  "gert_tools": "rag search"}, # standard non-admin; sandbox denied
      "limited": {"sub": "dev-limited", "groups": ["gert-users"],  "gert_tools": "rag"},         # restricted: search + sandbox denied
  }
  # CLI: `python -m tools.smoke.tokens --role admin`  -> prints a token (and a paste-ready
  #      localStorage snippet) so a dev can use the app locally with NO Pocket ID setup.
  # mint(role, **overrides) tweaks any claim without a new role - e.g. grant sandbox to a
  # non-admin (gert_tools="rag search sandbox") to prove the positive entitlement path.
  ```

  The three roles cover the authorization axes the app actually has: **admin vs non-admin** (the
  `/admin/*` surface) and the **tool-entitlement ceiling** ([auth](auth.md#enforcement---the-claim-is-the-ceiling)).
  `admin` has everything; `user` is the common case that proves **sandbox is dropped** despite any UI
  toggle; `limited` proves a tightly-scoped grant (**only `rag`** - search *and* sandbox denied). Any
  other shape (a non-admin *with* sandbox, or an **absent `gert_tools` claim that yields no tools at
  all** - the fail-closed path, since the JWT is the sole grant source) is a one-line `mint()`
  override in the test that needs it, not a standing role.

- **The key is generated on first run, never committed.** The first invocation of `tokens.py`
  (or the Fake host) creates an RSA keypair under a **git-ignored** path (e.g.
  `.dev/jwt/`) and writes the matching `dev-jwks.json` beside it; subsequent runs reuse it.
  Because nothing is committed, **a dev key cannot leak into a production image or repo** - there
  is simply no key to misplace.
- **The host trusts that key only in Dev/Test.** The Fake/Dev profile points `JwtBearer` at the
  generated `dev-jwks.json`. Tokens travel the real middleware, claim mapping, and `sub`->key
  derivation ([auth](auth.md)) - only the *key source* differs from prod. This wiring is
  environment-gated and **never** active in Production, which always validates against Pocket ID's
  JWKS. Two guards, then: the key isn't in the repo, and even if present it's only trusted under
  Dev/Test.
- **The launcher** ([section 8](#8-the-python-dev-launcher)) calls `tokens.mint(...)` directly and
  injects the result into `localStorage` - no HTTP round-trip.
- **`.NET` tests** stay self-contained: `GertApiFactory` generates an ephemeral RSA key, uses it
  to both configure validation and mint via `TestTokens.Mint(sub, admin, tools)` - nothing shared
  with Python, nothing committed.

> `tokens.py` and the host agree on the key path, so whichever runs first generates it and the
> other reuses it. Add `.dev/` to `.gitignore`.
>
> HS256 shared-secret minting would be marginally simpler but skips RS256/JWKS - the exact path
> prod uses - so we keep RS256.

### 4.4 Per-user temp DataRoot
`TempDataRoot` creates a throwaway root, points the host's `DataRoot` at it, and recursively
deletes it on dispose. Because a user is just a folder ([principles](principles.md)), this also
gives us the cleanest possible isolation assertion: after a two-user test, two sibling
`sha256(iss + sub)` directories exist and neither `rag.db` contains the other's chunks.

---

## 5. .NET whitebox tests

**Stack:** xUnit - FluentAssertions - NSubstitute (mocks) - `FluentValidation.TestHelper`.
`ChatEvent` streams are collected and asserted directly with FluentAssertions - no snapshot
library.

### `Gert.Service.Tests` - the heart of the suite
- **Chat orchestrator / tool loop** ([chat-and-tools](chat-and-tools.md)): drive `IChatService`
  with `FakeChatModel` scripted to request a tool, assert the emitted `ChatEvent` sequence
  (assistant text -> tool call -> tool result -> final text) collected from the stream. Covers the
  no-tool path, single tool, and a model that loops/recovers.
- **Tools**: `RagTool` (hybrid retrieve -> citations), `WebSearchTool`, `PythonSandboxTool` (incl. the
  `StubSandbox` failure variant). Assert tool entitlement is honoured - a user whose
  `AllowedTools` excludes `sandbox` can't invoke it.
- **Ingestion pipeline**: extract -> chunk -> embed -> write, run inline with fakes. Assert chunk
  counts, that `FakeEmbeddings` vectors land in the repo, and the "no extractable text -> Failed"
  decision from [decisions section 5](decisions.md).
- **Conversations / Documents / Artifacts** services: CRUD + ownership semantics.
- **Validation** - its own security-focused subsection below ([section 5 Validation](#validation---the-input-security-boundary)).

### `Gert.Database.Sqlite.Tests` - the only place SQL is proven
Runs against a **real** temp SQLite with the extension loaded (vec0 + FTS5) - an in-memory or
temp-file DB created per test by `TempDataRoot`.
- **Migrations** apply cleanly from empty (`chat/001`, `rag/001`).
- **`SqliteRagStore`** (`Gert.Rag.Sqlite`): insert chunks, run KNN (`vec0 MATCH ... ORDER BY distance`) and FTS5
  (`bm25`), and assert the **RRF hybrid fusion order** - deterministic thanks to `FakeEmbeddings`.
- **`SqliteChatRepository`**: conversation/message round-trips, ordering, deletes.
- **Isolation**: open user A's `rag.db`, write; open user B's provider, prove the query surface
  cannot see A's rows (separate connections, separate files - [principles](principles.md)).

### `Gert.Authentication.Tests`
JWT claims (`iss`, `sub`, `groups`, `gert_tools`) -> `IUserContext`; `sha256(iss + sub)` key
derivation and the **anti-reuse** guarantees ([decisions section 3](decisions.md#3-user-key),
[security F12](security.md#3-findings--remediations)): the provisioning gate rejects a malformed/
unexpected-issuer identity **before** any folder is created, and a username change in the IdP
is reflected into `user.db` on the next touch (never a 500, never a gate - the stored row is
descriptive only).
Plus the admin policy. (No denylist - revocation is stateless via token expiry, [decisions section 4](decisions.md#4-token-lifetime--revocation).)

### Validation - the input-security boundary
Validation is where untrusted user **content** first meets the system, so we test it as a
**security control**, not a forms-niceties check. It complements - never replaces - the
structural defences: isolation is the token->store derivation ([principles #2](principles.md)),
SQL-safety is Dapper parameterization. Validation's job is to reject malformed or abusive
*payloads* before they reach a service or the disk. The boundary proves a DTO valid through
`IValidationProvider`, then hands the **service layer** a `Validated<T>` proof instead of the
raw DTO; a service cannot be called without one, so every caller is held to
the **same** rules with no unguarded back door
([principle #6](principles.md), [tech-stack](tech-stack.md)).

Four things get tested:

1. **Per-validator, positive *and* negative** - every `IValidator<T>` via
   `FluentValidation.TestHelper`: `ShouldHaveValidationErrorFor` for each reject rule,
   `ShouldNotHaveValidationErrorFor` for the valid case, plus **boundary** cases (at the limit,
   one over, one under).

2. **Adversarial corpus, data-driven** - one shared "naughty strings" set
   (`Gert.Testing/TestData/NaughtyStrings.cs`: `../` traversal, null bytes, control/RTL-override
   chars, oversized blobs, SQL/FTS metacharacters, HTML/script, homoglyphs) fed through **every
   string field** via `[Theory]`. Each input must be **rejected or safely accepted - never crash,
   never slip through** to persistence. The concrete threat model:

   | User input | Threat | Rule under test |
   |------------|--------|-----------------|
   | Upload filename | path traversal / overwrite | reject separators & `..`; **extension allowlist** (pdf/docx/md/txt) |
   | Upload bytes / type | DoS, oversized payload | max size; content-type allowlist; reject empty |
   | Message / title text | DoS via huge payload; control chars | max length; reject null/whitespace-only; refuse control & bidi-override chars |
   | Model id | steering to an unintended model | must be in the **known-model allowlist** |
   | Tool name / toggles | invoking an unknown tool | must be a **registered** tool name (entitlement itself is authz - `Gert.Authentication.Tests` + [section 6](#6-api-integration-tests---gertapitests)) |
   | Conversation / document id | tampered id (IDOR is structural; this is defence-in-depth) | well-formed id (e.g. GUID) **before** it reaches a repo |
   | Admin user `{key}` | path traversal -> deletion of an arbitrary dir | must match `^[0-9a-f]{64}$`; resolved path asserted **under `/data/users/`** ([security F6](security.md#3-findings--remediations)) |
   | Web-search fetch URL | SSRF to internal services / metadata IP | scheme allowlist; private/loopback/link-local blocked; re-checked after redirects - `SsrfGuardTests` + `SafeHttpFetcherTests` + `SafeHttpFetcherRedirectTests` ([security F5](security.md#3-findings--remediations)) |
   | Pagination / `k` | negative/absurd values | positive, bounded |
   | RAG query text | FTS5 query-syntax abuse | carried as **data, not operators** (also a query-construction concern) |

3. **Fail-closed meta-test (the strongest guarantee)** - a reflection test over the service
   interfaces (`FailClosedMetaTest`) enforces the proof-type contract in two
   parts: **(a) no service method accepts a raw request DTO** - every one crosses the boundary as
   `Validated<T>`, so a method cannot even be *written* to skip validation; and **(b) every
   wrapped `T` has a registered `IValidator<T>`** (else `Validated<T>.From` throws - fail-closed).
   A new input type therefore **cannot ship unvalidated**: it must be wrapped, and wrapping
   resolves a validator. Validation can't be silently forgotten. This is the executable form of
   [principle #6](principles.md).

4. **The provider contract** - `IValidationProvider` resolves the right validator and surfaces a
   **consistent error shape**, so the API renders a 400 `ProblemDetails`. Tested once here.

The cross-tier proofs live where the behaviour does:
- **Integration ([section 6](#6-api-integration-tests---gertapitests))** - invalid input -> **400
  `ProblemDetails`, never 500**, and it **never reaches the repository** (no partial write). A
  500 would mean a validator was bypassed and something deeper threw - a failing test by design.
- Because the check lives in the service layer behind `IValidationProvider`, the guarantee is
  **structural**, not an API convenience - any future host inherits it unchanged.

---

## 6. API integration tests - `Gert.Api.Tests`

Through `GertApiFactory` (TestServer + fakes), over `HttpClient`. This tier proves the things
that only exist once you add HTTP:

- **Controllers / contracts**: each `/api/*` endpoint from [rest-api](rest-api.md) - status
  codes, DTO shapes, and **validation -> 400 `ProblemDetails`, never 500**, with the invalid
  payload never reaching a repository (the integration half of [section 5 Validation](#validation---the-input-security-boundary)).
- **SSE**: `POST` a message, read the response stream, parse `data:` frames back into
  `ChatEvent`s, and snapshot the sequence - the framing the service tests deliberately skip.
- **Auth middleware**: no token -> 401; valid token -> 200; expired/wrong-issuer/wrong-alg -> 401.
- **Isolation / IDOR** (the headline test): user A uploads a doc into a project; user B requests
  it -> 404, and B's project `rag.db` never contains A's chunks. The user key comes only from the
  token. The **one** request-supplied selector is `{pid}` - so a dedicated test tampers with it:
  pointing at another user's project id still resolves only under B's own folder (404, never A's
  data), and a non-UUID/`..` value is rejected by validation
  ([configuration section 2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe), [principles](principles.md)).
- **Project isolation**: a query in project X cannot see project Y's rows (separate folders,
  separate DBs - the per-project case of [principle #2](principles.md)).
- **Admin RBAC**: non-admin -> `/api/admin/users` 403; admin -> 200.
- **Admin `{key}` traversal** ([security F6](security.md#3-findings--remediations)): `DELETE /api/admin/users/{key}`
  with a non-hex / `..` / absolute `{key}` is rejected (400/404) and **no directory outside
  `/data/users/{valid-key}` is touched** - asserted against a temp `DataRoot` with sentinel siblings.
- **Security headers / CSP** ([security F1](security.md#3-findings--remediations)): an HTML response
  carries the CSP and `X-Content-Type-Options: nosniff` / `Referrer-Policy` / `X-Frame-Options`
  headers; `connect-src` lists only the API origin + Pocket ID.
- **SSRF guard** ([security F5](security.md#3-findings--remediations)): the web-search fetcher,
  pointed at a private/loopback/`file:` URL (via the `FakeWebSearch` result set), refuses it - it
  never opens the connection. The live-socket halves of the control - the per-hop redirect
  re-vet and the connect-time DNS pin - are pinned in `Gert.Tools.Tests/SafeHttpFetcherRedirectTests.cs`
  via the fetcher's internal resolver/IP-check seam (loopback listeners; production wiring unchanged).
- **Per-user rate limiting** ([security F10](security.md#3-findings--remediations)):
  `RateLimitingTests` re-enables the limiter (skipped under the Testing environment) and turns
  the `Gert:RateLimiting` knobs down - over-cap requests get the branded 429 `ProblemDetails`,
  a different `sub` is served immediately after another user's 429 (partition isolation, the
  actual per-user semantics), and `/healthz` stays outside the limited surface.
- **Upload parser hardening** ([security F7](security.md#3-findings--remediations)): a DOCX carrying
  an external-entity reference (XXE) and an over-cap decompression-bomb each fail the *document*
  (`status='failed'`) without hanging or reading host files.
- **Lazy provisioning**: a first authenticated request creates the user's folder + the `default`
  project + schema ([principles](principles.md)).
- **Ingestion `BackgroundService`**: enqueue an upload, poll `GET /api/projects/{pid}/documents/{id}`
  until `Ready` (mirrors the polling decision in [decisions section 6](decisions.md)).
- **SPA fallback**: `GET /some/client/route` -> `index.html`; `GET /api/...` and `/healthz`
  are **not** swallowed by the fallback ([tech-stack](tech-stack.md)).

**The on-demand race set** (`ConversationSwitchingRaceTests`, `[Trait("Category","Race")]`):
mid-stream switching dead zones - submit-then-switch across lanes, reload-mid-stream,
drop-and-resubscribe seq continuity, the 409 second send, sibling deletes during a turn. A
paced `IChatModelClient` (real delays) replaces the instant fake, so these are deliberately
slow and timing-coupled: `make test` filters `Category!=Race` (they are **not** part of the CI
gate); run them with `make test-race`.

---

## 7. Web tests

The SPA (`Gert.Api/wwwroot`) is no-build native ESM ([ui-components](ui-components.md)); we keep the **no-npm**
rule into testing too. Both web tiers run on the **same Python + Playwright** stack - no Node,
no jsdom, no test-runner package. The browser is the DOM/JS engine; Python is the runner.

- **Component units** (`tools/smoke/tests/test_components.py`). A VanJS component is a function
  returning a real DOM node, and its reactivity needs a real DOM - so we mount the **actual,
  unmocked** module in a browser and assert. Python drives it via `page.evaluate()` against a
  tiny `tests/harness.html` (a `__mount` helper) served on the same origin so the modules'
  absolute same-origin imports (`/components/...`, `/state/...`) resolve - no import map needed,
  which is the same plain `script-src 'self'` boot the real SPA uses:

  ```python
  def test_convo_item_active(page, base_url):
      page.goto(f"{base_url}/tests/harness.html")
      cls = page.evaluate("""async () => {
          const { ConvoItem } = await import('/components/sidebar/convo-item.js');
          const chat          = await import('/state/chat.js');
          const node = ConvoItem({ id: 'c1', title: 'Hello' });
          document.body.append(node);
          chat.activeId.val = 'c1';
          await new Promise(r => setTimeout(r));   // let van flush its batched update
          return node.className;
      }""")
      assert "active" in cls
  ```

  Assertions can live in-page (return a value to Python, as above) or in Python via Playwright
  locators (`expect(page.locator(".convo")).to_have_text("Hello")`). Caveats: real-browser
  launch overhead - reuse one context across tests; and VanJS batches DOM updates on a
  microtask, so `await` a tick before asserting.

- **Full-app E2E** ([section 8](#8-the-python-dev-launcher)). Loading the whole SPA in a real browser
  is the truest test of an all-absolute-import ESM app - it catches a broken `import` or a stray
  bare specifier that a component-isolated test would miss, and proves the plain `script-src 'self'`
  CSP boots with no import map and no inline `<script>`.

- **Markdown/math renderer gallery** - the in-house renderer (`lib/markdown.js` + the `lib/render/`
  engine, with `lib/smath.js`'s native MathML and `lib/highlight.js`'s tokens behind the `MdMath`/`MdCode`
  leaves) is not just eyeballed: `tests/markdown-gallery.html` renders a battery of CommonMark/GFM/math
  inputs and self-checks the F4 stance - no `innerHTML`, the closed per-`(ns, tag)` `createEl` allow-list,
  the single `sanitizeUrl()` chokepoint, MathML carrying no `href`/`src`/`onerror` sink - plus heading
  anchors and native `<math>` layout in a real browser, exposing a machine-readable verdict on
  `window.__galleryResult`. The battery is bucketed into FEATURE (render-without-throw), FUNCTIONAL, and
  SECURITY cards; the Python gate pins each bucket's count so a check can't silently vanish. The Goal B
  classifier (the single `LINE_KINDS` pass) is pinned by **four new FUNCTIONAL edge-case cards** - bare
  `######` is an empty ATX `<h6>`, a prefix-only `$$` opens a display-math block, a mid-line `\[` stays a
  paragraph escape (not display math), and an over-indented table-shaped line is indented code, not a GFM
  table - each resolving to the stricter/CommonMark-correct single form (an intended behavior change). A
  **new SECURITY card** pins that the MathML allow-list drops a forged `\href{javascript:…}`/`\src` so no
  sink attr or `<a>`/`<img>` is forged from TeX. That verdict is an actual CI gate -
  `tools/smoke/tests/test_components.py::test_markdown_gallery_all_self_checks_pass` asserts every
  self-check passes (and the bucket counts hold) - so a renderer regression fails the build, not a
  screenshot review.

The Fake host serves the harness: the Fake profile maps `tests/web/` at `/tests/` (dev-only),
so the harness imports the real app modules on the same origin.

---

## 8. The Python dev launcher

`tools/smoke/run.py` - the "create JWTs, then click" launcher. Pure Python + Playwright; no
npm, no .NET SDK needed beyond running the host.

**What it does, in order:**
1. **Boot the mock upstreams** - start `tools/smoke/mocks` (vLLM + SearXNG) on localhost ports, then
   **boot the host** with `dotnet run --launch-profile FakeE2E`, whose config points the **real**
   `Gert.Chat`/`Gert.Tools` clients at those mock URLs (sandbox = stub). Or attach to an already-running pair
   with `--base-url`; wait for `/healthz`.
2. **Mint tokens** - call `tokens.mint("admin")` / `tokens.mint("user")` in-process
   ([section 4.3](#43-jwt-minting---a-python-token-harness)). No HTTP round-trip; the same harness a dev
   runs from the CLI for local testing.
3. **Inject + drive** - for each `(browser, role)` in the matrix, launch the browser, seed the
   token (localStorage, matching how `services/auth.js` stores it - [ui-components](ui-components.md)),
   load the SPA, and run the scenarios.
4. **Report** - pass/fail per scenario; screenshot + trace on failure under `tools/smoke/artifacts/`.

**Matrix:** `{chromium, firefox} x {admin, user}` for the full click-through, plus the **`limited`**
role in the RBAC/entitlement scenario (below). Flags:
`--browser chromium|firefox|all`, `--role admin|user|limited|all`, `--headed`, `--keep-open`,
`--base-url <url>`. The host and the mock upstreams emit the shared NDJSON logs
([operations section Logging format](operations.md#logging-format-shared)), so a failed run's interleaved
output parses with one reader.

**Scenarios** (cover the mockup's interactive surface - [ui-components section 7](ui-components.md#7-feature---component-map)):
- New chat -> type -> send -> **streaming** message appears -> **tool cards** expand -> citations/footnotes render.
- **Knowledge**: drag/upload a file -> status pill goes `Processing` -> `Ready`; toggle use-in-chat.
- **Canvas**: switch artifact tabs (md/html/svg/py); flip **Rendered/Source**; the HTML
  artifact renders in its sandboxed iframe; the code artifact shows the Problems panel.
- **Chrome**: theme toggle persists; model picker selects a model; responsive drawers open/close
  at mobile widths.
- **RBAC + IDOR + entitlement**: as **admin**, `/admin/users` loads; as **user**, it's hidden and
  the API returns 403; a user cannot open another user's document. As **`limited`**, the Search and
  Sandbox tool chips are unavailable and the API drops those tools even if the request asks for them
  (the entitlement ceiling, [auth](auth.md#enforcement---the-claim-is-the-ceiling)). With **no
  `gert_tools` claim at all** (a `mint(..., gert_tools=None)` override - the fail-closed floor,
  [decisions section 10](decisions.md#10-tool-entitlement---the-jwt-is-the-sole-source-no-default-grant)), even a
  tool every other role has is refused: the scripted model still calls `make_artifact`, but the
  orchestrator drops it, so no artifact is persisted and the canvas stays empty.

**Setup** (via **[uv](https://github.com/astral-sh/uv)** - the project's Python env manager):
`cd tools/smoke && uv sync && uv run playwright install chromium firefox`.
Run the suite with `uv run python -m tools.smoke.run`, mint a local token with
`uv run python -m tools.smoke.tokens --role admin` - or use the Makefile wrappers
(`make smoke-auth`, `make serve-mock`).

---

## 9. Tooling summary

| Concern | Choice | Notes |
|---------|--------|-------|
| .NET test runner | **xUnit** | De-facto for ASP.NET Core. |
| Assertions | **FluentValidation.TestHelper** + **FluentAssertions** | Readable failures. |
| Mocks | **NSubstitute** | Fakes for the external world live in `Gert.Testing`, not ad-hoc mocks. |
| API integration | **`WebApplicationFactory<Program>`** | Real pipeline, fake externals. |
| SQLite | **`Microsoft.Data.Sqlite`** temp DB + vec0/FTS5 | Real SQL, no mocking the database. |
| Web tests (component units + E2E) | **Playwright (Python)** | Browser as the DOM/JS engine; Chromium + Firefox; no npm, no Node. |
| Python env | **uv** | Manages the venv + deps for `tools/smoke` (`uv venv`, `uv run`). |
| JWT (tests) | **RS256 key generated on first run** (git-ignored) + JWKS | Exercises the real RS256/JWKS path; no key ever committed. |
| External world (.NET tiers) | **in-process fakes** (`AddGertFakes`) | Swap the adapter ports; fast, deterministic, no sockets. |
| External world (E2E) | **Python mock upstreams** (`tools/smoke/mocks`) | Real adapters -> mock vLLM/SearXNG/monty; exercises adapter HTTP + SSRF guard. |

---

## 10. CI

Five jobs in `.github/workflows/ci.yml`, all gating merges:

1. **Docs - link check** - `make check-links` (`tools/check_links.py`, stdlib Python):
   every relative link and `#anchor` in tracked markdown must resolve. The design docs
   cross-link densely and code comments cite them by section, so a renamed heading or
   moved file fails the build instead of silently stranding readers.
2. **.NET - build + test** - `dotnet test` runs every `Gert.*.Tests` project (unit + real-SQLite
   + API integration). Fast, hermetic, no network; warnings are errors.
3. **Python - ruff + mypy + conformance** - `make lint` (ruff lint + format check, mypy
   `--strict`) plus `make smoke-unit` (the [A.2](#a2-deterministic-embeddings-hash---1024-dim-unit-vector)
   embedding-conformance check, no browsers).
4. **API auth smoke** - `make smoke-auth`: boots the Python mocks + the `FakeE2E` host (no
   browsers) and proves every endpoint rejects the bad-token taxonomy - keeps the auth signal
   alive even when browser setup breaks.
5. **Browser E2E smoke** - mocks + `FakeE2E` host, then the Playwright matrix
   `{chromium, firefox} x {admin, user, limited}` plus the full pytest suite (component
   mounts, RBAC/SSRF/IDOR). The only job that installs browsers; uploads traces/screenshots
   on failure.

---

## 11. Non-goals

- **Real vLLM / GPU, real Pocket ID, real gVisor** are *not* in unit/integration/E2E - they're
  faked (the E2E exercises the real **adapter** code against Python mock upstreams, but never a real
  model server or sandbox). A thin, separate **staging smoke** (the same `run.py` pointed at a real
  deployment with `--base-url`) is the place to exercise the genuine articles; it is not part of the
  gating CI.
- **Load/perf testing** is out of scope at ~20 users.
- **Cross-browser pixel-diffing** - we assert behaviour and roles, not screenshots.
- **`Gert.Model.Tests`** - dropped: the models are POCOs; any record/DTO invariant worth
  asserting rides along in the service suite.

---

## Appendix A - The shared fake spec

The in-process .NET fakes ([section 4.1](#41-the-fake-external-world)) and the Python mock upstreams
([section 4.2](#42-two-ways-to-fake-the-outside-world)) only stay drift-free if they implement **one**
definition of behaviour. This appendix is that definition. The split:

- **Algorithms are code** - each side implements A.1/A.2 from this spec, kept honest by a committed
  **golden file** both assert against.
- **Canned data is data** - the chat/search fixtures live **once** in `tests/shared/fixtures.json`;
  the .NET side loads it from disk at run time (`Gert.Testing/SharedPaths` walks up to
  `tests/shared/`), the Python side reads the same file. No second copy.

```
tests/shared/                  # one source of truth for both fake layers
  fixtures.json                #   canned chat completions + web-search results (schema: A.3 / A.4)
  embeddings_golden.json       #   text -> expected vector samples - the A.2 conformance check
```

### A.1 Determinism contract
Equal input => **identical** output on both sides, so KNN/RRF order and citations assert the same in
`Gert.Database.Sqlite.Tests` and in the browser E2E. Everything below is specified to the byte so C#
and Python agree without coordination.

### A.2 Deterministic embeddings (`hash -> 1024-dim unit vector`)
The function the `FakeEmbeddings` adapter and the mock `/v1/embeddings` endpoint both compute:

```
embed(text) -> float32[1024]:
    data = utf8_bytes(text)
    for i in 0 .. 1023:
        h    = SHA256( data ++ uint32_be(i) )      # 32-byte digest; index appended big-endian
        u    = uint32_be( h[0:4] )                 # first 4 bytes as a big-endian uint32
        x[i] = (u / 4294967296.0) * 2.0 - 1.0      # double in [-1, 1)
    norm = sqrt( sum_i x[i]^2 )                     # L2 norm, in double
    return [ float32( x[i] / norm ) for i in 0..1023 ]
```

- **Fixed choices** (the only way both languages match): UTF-8 in; SHA-256; the index suffix and the
  4-byte slice are **big-endian**; arithmetic in IEEE-754 **double**, cast to **float32** only at the
  end. `norm` is never zero in practice; if it were, return the canonical basis vector `e0`.
- **Why it works for tests:** distinct texts map to near-orthogonal directions in 1024-dim, so cosine
  distances are well-separated - no fragile ties. A query embedded with the *same* function is its
  own nearest neighbour, which is exactly what lets RAG tests assert an exact hit order.
- **Conformance:** the .NET impl generates `embeddings_golden.json` once (a handful of texts ->
  vectors); thereafter a `Gert.Testing` test **and** a Python test both assert `embed(t)` matches the
  golden to float32 equality. If either drifts, both go red - that's the anti-drift guarantee, made
  executable.

### A.3 Canned chat completions
Both the `FakeChatModel` and the mock `/v1/chat/completions` resolve a reply from
`fixtures.json` by the **last user message**, and play it as a **token-by-token stream**. The
fixture is at the **model wire layer** - assistant content deltas and (optionally) a tool call,
nothing higher: citations and artifacts are the *orchestrator's* job downstream, never scripted here.

```jsonc
// fixtures.json -> "completions": [ ... ]
{
  "match": "exact",                       // "exact" | "contains" against the trimmed last user message
  "when":  "should I use Qdrant or sqlite-vec?",
  "deltas": ["Short version: ", "use ", "sqlite-vec."],   // streamed one chunk per element
  "finish": "stop",                       // finish_reason; "tool_calls" when tool_call is present
  "usage":  { "completion_tokens": 12 }
}
```

A tool-exercising fixture emits a tool call first, then - on the **follow-up** model call (whose
messages now carry the tool result) - matches the *same* `when` and plays `after_tool.deltas`:

```jsonc
{
  "when": "search my docs about qdrant",
  "tool_call": { "name": "search_documents",
                 "arguments": "{\"query\":\"qdrant\",\"k\":5}" },   // OpenAI: arguments is a JSON string
  "finish": "tool_calls",
  "after_tool": { "deltas": ["Based on your docs, sqlite-vec wins [1]."], "finish": "stop" }
}
```

- **Fallback:** no match => `"fallback": "echo"` streams `Echo: <message>`, tokenised on word
  boundaries (spaces preserved) so the typewriter caret and SSE framing have real chunks to render.
- **One asymmetry, same data:** the **Python mock emits OpenAI-compatible SSE** (`data: {chunk}\n\n`,
  `delta.content` / `delta.tool_calls`, terminal `[DONE]`) because the *real* `Gert.Chat` adapter
  parses that wire format ([section 4.2](#42-two-ways-to-fake-the-outside-world)); the **.NET fake yields the
  `IChatModelClient` port's types directly** (no socket). Both are driven by the identical fixture
  entry, so the resulting `ChatEvent` stream is the same.

### A.4 Canned web search (SearXNG)
`FakeWebSearch` and the mock SearXNG resolve results by query from the same file, in SearXNG's JSON
shape. At least one fixture is **adversarial by design** for the SSRF test:

```jsonc
// fixtures.json -> "search": { "<query>": { "results": [ ... ] } }
"results": [
  { "title": "Qdrant vs sqlite-vec", "url": "https://example.test/bench", "content": "..." },
  { "title": "internal",            "url": "http://169.254.169.254/latest/meta-data/", "content": "..." }
]
```

The second result drives [F5](security.md#3-findings--remediations): the real adapter's summarize
step must **refuse** that URL (private/link-local), proving the SSRF guard end-to-end. The sandbox's
mock (`mocks/monty.py`) keys off the fixtures' sandbox case
([section 4.2](#42-two-ways-to-fake-the-outside-world)).

### A.5 Ownership
`tests/shared/` is the seam's contract. Changing a fixture or the embedding algorithm is a
deliberate edit to **one** place; the golden-file conformance tests on both sides fail loudly if the
two implementations ever diverge.
