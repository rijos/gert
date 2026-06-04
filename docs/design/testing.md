# Testing plan

How Gert is tested top to bottom: a **fake in-memory world** the whole stack runs against,
**.NET whitebox tests** for every service/repository/validator, **Console** coverage, and a
**Python + headless-browser** smoke launcher that clicks through the real SPA as both an
admin and a normal user across **Chromium and Firefox**.

> **One-line strategy:** the service layer is host-agnostic, so we test logic without HTTP;
> the repositories are the only code that touches SQL, so we test them against **real**
> SQLite; and the two things that can only break in a browser — the SPA and the JWT/SSE
> wiring — get a headless end-to-end pass. One set of **fakes** (vLLM, SearXNG, sandbox)
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
| `Gert.Service` references **only** `Gert.Model` — no `HttpContext`, JWT, or SSE ([tech-stack](tech-stack.md)) | The entire tool loop, ingestion pipeline, and orchestration are unit-testable with plain objects — no web host needed. |
| Streaming is `IAsyncEnumerable<ChatEvent>`; transport renders it ([tech-stack](tech-stack.md)) | We assert on the **event stream** directly in `Gert.Service.Tests`; SSE framing is tested once, separately, in `Gert.Api.Tests`. |
| Repository interfaces are the **only** code that sees SQL | We test `Gert.Database.Sqlite` against a **real** temp SQLite (vec0 + FTS5) — the only place SQL correctness can be proven. |
| Isolation is a **filesystem** property; the user key comes only from the token ([principles](principles.md)) | Isolation and IDOR become concrete assertions: mint two tokens, prove user B physically cannot reach user A's folder. |
| `Gert.Console` drives the **same** services as the API | One service test suite covers both hosts; the Console suite only tests rendering + wiring. |

### Why the database stays real (the one asymmetry)
Everything behind `IGertServices` and the repository interfaces *can* be swapped for an
in-memory double — that's the whole point of the seam, and it's exactly what we do for the
**outside world** (vLLM, SearXNG, sandbox). But persistence is deliberately **not** faked:

- The RAG SQL is **engine-specific and cannot be abstracted into shared SQL**
  ([tech-stack → Engine portability](tech-stack.md#engine-portability)). An in-memory repo
  would *reimplement* ranking in C# — testing the fake's ranking, not the real `vec0` + FTS5 +
  RRF retrieval, which is the riskiest code in the system.
- Isolation is a **filesystem** property ([principles #2](principles.md)), not a repository
  property — an in-memory store has no per-user `rag.db` to prove it with.

So the rule is: **fake the outside world; keep persistence real but temporary** — real SQLite
(vec0 + FTS5) in a throwaway `DataRoot` ([§4.4](#44-per-user-temp-dataroot)). It's nearly as
fast as in-memory and loses none of the SQL or isolation coverage. In-memory repositories may
*complement* this for fast service-only tests, but never *replace* the real-SQLite tier.

---

## 2. The pyramid

```
        ┌───────────────────────────────────────────────┐
   E2E  │  Python launcher → Playwright (Chromium+Firefox)│   slow, few
        │  admin + non-admin JWTs, real SPA, fake host    │
        ├───────────────────────────────────────────────┤
  HTTP  │  Gert.Api.Tests — WebApplicationFactory          │
        │  controllers · SSE · auth · IDOR · admin RBAC    │
        ├───────────────────────────────────────────────┤
   DB   │  Gert.Database.Sqlite.Tests — real temp SQLite   │
        │  vec0 + FTS5 · hybrid rank · migrations · isolation│
        ├───────────────────────────────────────────────┤
  Unit  │  Gert.Service.Tests · Authentication · Validation │   fast, many
        │  tool loop · ingestion · claim mapping · rules    │
        └───────────────────────────────────────────────┘
              all tiers share one set of fakes (Gert.Testing)
```

Most assertions live at the bottom two tiers (fast, deterministic). The browser tier proves
the wiring a unit test can't: that a minted JWT flows through the SPA, that SSE renders as a
streaming message, and that an admin sees `/admin/users` while a normal user gets a 403.

---

## 3. Test project layout

New projects extend the solution from [tech-stack → Solution layout](tech-stack.md#solution-layout-projects):

```
tests/
  Gert.Testing/                 # shared infra, NO test cases — fakes, fixtures, factory, JWT mint
    Fakes/
      FakeChatModel.cs          #   OpenAI-compatible vLLM double: canned streaming + tool calls
      FakeEmbeddings.cs         #   deterministic vectors (hash → 1024-dim) for stable KNN
      FakeWebSearch.cs          #   SearXNG double
      StubSandbox.cs            #   gVisor double — no container, scripted stdout/exit
    GertApiFactory.cs           #   WebApplicationFactory<Program> with all fakes + test JWT
    TempDataRoot.cs             #   per-test user-folder root under a temp dir; auto-cleanup
    TestTokens.cs               #   RSA dev key + JWKS; mint admin / user JWTs
    TestData/
      NaughtyStrings.cs         #   adversarial input corpus — fed across every string field (§5)

  Gert.Service.Tests/           # whitebox — chat orchestrator/tool loop, conversations,
                                #   documents, ingestion pipeline, tools, validation
  Gert.Database.Sqlite.Tests/   # repositories vs real temp SQLite (vec0 + FTS5); migrations; isolation
  Gert.Authentication.Tests/    # JWT claims → IUserContext; sub→key (sha256); RS256 pin
  Gert.Api.Tests/               # integration via GertApiFactory — controllers, SSE, auth, IDOR, admin, SPA fallback
  Gert.Console.Tests/           # drive the Console host with fakes; assert rendered ChatEvent stream
  web/
    harness.html                # import map + __mount helper — Fake host serves it at /tests/ for component units
  shared/                       # ONE source of truth for both fake layers (Appendix A)
    fixtures.json               #   canned chat completions + web-search results
    embeddings_golden.json      #   text → expected vector — the deterministic-embedding conformance check

tools/
  smoke/                        # Python E2E launcher (uv-managed; no npm, no .NET) — drives the Fake host
    run.py                      #   boot mocks + host (FakeE2E) → mint tokens → Playwright matrix → report
    tokens.py                   #   role→claims map; mint(role) RS256 via pyjwt; CLI for local dev
    mocks/                      #   mock upstreams for E2E — the real Gert.External adapters point here
      __main__.py               #     boots all mocks on localhost ports (one process); shared specs
      vllm.py                   #     OpenAI-compatible: /v1/chat/completions (streaming + tool calls), /v1/embeddings
      searxng.py                #     SearXNG JSON; can emit a private-IP result URL to test the SSRF guard
      specs.py                  #     canned completions + deterministic hash→1024-dim embedding (matches FakeEmbeddings)
    requirements.txt            #   playwright, pyjwt (+ a tiny ASGI server for streaming) — via `uv pip install -r`
    pages.py                    #   page objects for the SPA regions (sidebar, composer, canvas)
    tests/
      test_components.py        #   component units — mount real modules via page.evaluate (§8)
      test_chat.py              #   new chat → send → streaming → tool cards → citations
      test_knowledge.py         #   upload → status pills → use-in-chat toggle
      test_canvas.py            #   artifact tabs · rendered/source · html iframe · code problems
      test_rbac.py              #   admin sees /admin/users; user gets 403; IDOR is blocked
      test_chrome.py            #   theme toggle · responsive drawers · model picker

.dev/                           # git-ignored — generated on first run, NEVER committed
  jwt/                          #   dev RSA keypair + dev-jwks.json (trusted only in Dev/Test)
```

`Gert.Web` is exercised by the browser tier (`tools/smoke`) rather than a JS unit runner —
see [§8](#8-web-tests). Each `*.Tests` project references its target plus `Gert.Testing`.

---

## 4. Shared test infrastructure — `Gert.Testing`

One project owns the fakes and fixtures so every tier sees identical behaviour. It holds no
test cases — only the scaffolding the others consume.

### 4.1 The fake external world
The three things we can't run in CI — a GPU model server, a search engine, a gVisor sandbox —
get deterministic doubles. They implement the **same service-layer interfaces** the real
adapters do, so swapping them is a single DI registration ([tech-stack](tech-stack.md)).

- **`FakeChatModel`** — OpenAI-compatible double. Returns canned completions keyed by the
  last user message (fixture map; echo fallback), and can emit a scripted **tool call** so the
  orchestrator's tool loop is exercised end to end. Streams token-by-token so SSE and the
  typewriter caret have something real to render.
- **`FakeEmbeddings`** — maps text → a deterministic 1024-dim unit vector (hash-seeded, per the
  exact algorithm in [Appendix A.2](#a2-deterministic-embeddings-hash--1024-dim-unit-vector)). KNN
  ordering is therefore **stable across runs** *and identical to the Python mock*, which is what lets
  us assert exact retrieval order in RAG tests instead of "something came back."
- **`FakeWebSearch`** — fixed result set for the web-search tool.
- **`StubSandbox`** — returns scripted stdout/exit without launching a container; a "throws"
  variant covers the sandbox-failure path.

### 4.2 Two ways to fake the outside world
The external world is doubled at **two fidelities**, chosen per tier — but both speak the *same*
scripted behaviour, so a result proven in a unit test is the result the browser sees:

| Tier | External world | Transport | How |
|------|----------------|-----------|-----|
| **.NET unit / integration** (`Gert.Service.Tests`, `Gert.Api.Tests`) | **in-process .NET fakes** (`AddGertFakes` swaps the `Gert.External` ports) | TestServer, no socket | `GertApiFactory : WebApplicationFactory<Program>` → `HttpClient`. Fastest, fully deterministic. |
| **Browser E2E** (the Python launcher) | **real `Gert.External` adapters → Python mock upstreams** (HTTP); sandbox stays a .NET stub | real Kestrel on localhost | `dotnet run --launch-profile FakeE2E`: the host wires its **real** vLLM/SearXNG clients but points them at the mock URLs `tools/smoke/mocks` serves. |

**Why two.** The in-process fakes give speed + determinism for the bulk of the suite. The Python
mocks give **wire-level fidelity** for the few browser runs: pointing the *real* adapters at a fake
upstream exercises the adapter code `AddGertFakes` skips — `IHttpClientFactory`/Polly, OpenAI request
shaping, **streaming SSE parsing of the upstream**, and the **SSRF guard** (a mock SearXNG can return
a private-IP result URL and assert the fetch is refused — [security F5](security.md#3-findings--remediations)).
The **sandbox is local process-exec, not HTTP**, so it has no wire protocol to mock in Python — it
stays a .NET `StubSandbox` for E2E; real gVisor is exercised only in the staging smoke
([§12](#12-non-goals)).

**No drift.** The Python `mocks/` and the .NET fakes share one documented spec
([Appendix A](#appendix-a--the-shared-fake-spec)) — the same canned completions keyed by
last-user-message, and the **same deterministic hash→1024-dim embedding algorithm** — so KNN/RRF
order and citations assert identically in `Gert.Database.Sqlite.Tests` and in the browser. Both shortcuts also share the dev JWT key path ([§4.3](#43-jwt-minting--a-python-token-harness)),
point `DataRoot` at a temp dir, and install the test JWT validation; the only differences are the
socket and which fidelity of external world is wired.

### 4.3 JWT minting — a Python token harness
No dev-only token *endpoint* on the host. **Python mints the tokens**; the host only *trusts*
a dev key, and validates through the **same** RS256/JWKS path it uses for Pocket ID in prod —
so the dev shortcut can't hide a validation bug.

- **`tools/smoke/tokens.py`** — a small harness with a role→claims map and a `mint(role,
  **overrides)` function. Signs RS256 with a **dev keypair** using `pyjwt`. Roles are just data,
  so adding a privilege set is a one-line edit:

  ```python
  # role → the claims that distinguish it; mint() adds iss/aud/exp/iat/nbf to match the dev authority
  # (iss matters: the folder key is sha256(iss+sub) and the provisioning gate checks iss — §4.4 / F12).
  ROLES = {
      "admin":   {"sub": "dev-admin",   "groups": ["gert-admins"], "gert_tools": "*"},          # admin surface + every tool
      "user":    {"sub": "dev-user",    "groups": ["gert-users"],  "gert_tools": "rag search"}, # standard non-admin; sandbox denied
      "limited": {"sub": "dev-limited", "groups": ["gert-users"],  "gert_tools": "rag"},         # restricted: search + sandbox denied
  }
  # CLI: `python -m tools.smoke.tokens --role admin`  → prints a token (and a paste-ready
  #      localStorage snippet) so a dev can use the app locally with NO Pocket ID setup.
  # mint(role, **overrides) tweaks any claim without a new role — e.g. grant sandbox to a
  # non-admin (gert_tools="rag search sandbox") to prove the positive entitlement path.
  ```

  The three roles cover the authorization axes the app actually has: **admin vs non-admin** (the
  `/admin/*` surface) and the **tool-entitlement ceiling** ([auth](auth.md#enforcement--the-claim-is-the-ceiling)).
  `admin` has everything; `user` is the common case that proves **sandbox is dropped** despite any UI
  toggle; `limited` proves a tightly-scoped grant (**only `rag`** — search *and* sandbox denied). Any
  other shape (a non-admin *with* sandbox, an absent claim falling back to the default grant) is a
  one-line `mint()` override in the test that needs it, not a standing role.

- **The key is generated on first run, never committed.** The first invocation of `tokens.py`
  (or the Fake host) creates an RSA keypair under a **git-ignored** path (e.g.
  `.dev/jwt/`) and writes the matching `dev-jwks.json` beside it; subsequent runs reuse it.
  Because nothing is committed, **a dev key cannot leak into a production image or repo** — there
  is simply no key to misplace.
- **The host trusts that key only in Dev/Test.** The Fake/Dev profile points `JwtBearer` at the
  generated `dev-jwks.json`. Tokens travel the real middleware, claim mapping, and `sub`→key
  derivation ([auth](auth.md)) — only the *key source* differs from prod. This wiring is
  environment-gated and **never** active in Production, which always validates against Pocket ID's
  JWKS. Two guards, then: the key isn't in the repo, and even if present it's only trusted under
  Dev/Test.
- **The launcher** ([§9](#9-the-python-dev-launcher)) calls `tokens.mint(...)` directly and
  injects the result into `localStorage` — no HTTP round-trip.
- **`.NET` tests** stay self-contained: `GertApiFactory` generates an ephemeral RSA key, uses it
  to both configure validation and mint via `TestTokens.Mint(sub, admin, tools)` — nothing shared
  with Python, nothing committed.

> `tokens.py` and the host agree on the key path, so whichever runs first generates it and the
> other reuses it. Add `.dev/` to `.gitignore`.
>
> HS256 shared-secret minting would be marginally simpler but skips RS256/JWKS — the exact path
> prod uses — so we keep RS256.

### 4.4 Per-user temp DataRoot
`TempDataRoot` creates a throwaway root, points the host's `DataRoot` at it, and recursively
deletes it on dispose. Because a user is just a folder ([principles](principles.md)), this also
gives us the cleanest possible isolation assertion: after a two-user test, two sibling
`sha256(iss + sub)` directories exist and neither `rag.db` contains the other's chunks.

---

## 5. .NET whitebox tests

**Stack:** xUnit · FluentAssertions · NSubstitute (mocks) · `FluentValidation.TestHelper` ·
[Verify](https://github.com/VerifyTests/Verify) for snapshotting `ChatEvent` streams.

### `Gert.Service.Tests` — the heart of the suite
- **Chat orchestrator / tool loop** ([chat-and-tools](chat-and-tools.md)): drive `IChatService`
  with `FakeChatModel` scripted to request a tool, assert the emitted `ChatEvent` sequence
  (assistant text → tool call → tool result → final text) via a Verify snapshot. Covers the
  no-tool path, single tool, and a model that loops/recovers.
- **Tools**: `RagTool` (hybrid retrieve → citations), `WebSearchTool`, `SandboxTool` (incl. the
  `StubSandbox` failure variant). Assert tool entitlement is honoured — a user whose
  `AllowedTools` excludes `sandbox` can't invoke it.
- **Ingestion pipeline**: extract → chunk → embed → write, run inline with fakes. Assert chunk
  counts, that `FakeEmbeddings` vectors land in the repo, and the "no extractable text → Failed"
  decision from [decisions §5](decisions.md).
- **Conversations / Documents / Artifacts** services: CRUD + ownership semantics.
- **Validation** — its own security-focused subsection below ([§5 Validation](#validation--the-input-security-boundary)).

### `Gert.Database.Sqlite.Tests` — the only place SQL is proven
Runs against a **real** temp SQLite with the extension loaded (vec0 + FTS5) — an in-memory or
temp-file DB created per test by `TempDataRoot`.
- **Migrations** apply cleanly from empty (`chat/001`, `rag/001`).
- **`SqliteRagRepository`**: insert chunks, run KNN (`vec0 MATCH … ORDER BY distance`) and FTS5
  (`bm25`), and assert the **RRF hybrid fusion order** — deterministic thanks to `FakeEmbeddings`.
- **`SqliteChatRepository`**: conversation/message round-trips, ordering, deletes.
- **Isolation**: open user A's `rag.db`, write; open user B's provider, prove the query surface
  cannot see A's rows (separate connections, separate files — [principles](principles.md)).

### `Gert.Authentication.Tests`
JWT claims (`iss`, `sub`, `groups`, `gert_tools`) → `IUserContext`; `sha256(iss + sub)` key
derivation and the **anti-reuse** guarantees ([decisions §3](decisions.md#3-folder-key),
[security F12](security.md#3-findings--remediations)): the provisioning gate rejects a malformed/
unexpected-issuer identity **before** any folder is created, and a missing/truncated `meta.json`
sidecar is **healed** from the token on the next touch (never a 500, never a gate).
Plus the admin policy. (No denylist — revocation is stateless via token expiry, [decisions §4](decisions.md#4-token-lifetime--revocation).)

### Validation — the input-security boundary
Validation is where untrusted user **content** first meets the system, so we test it as a
**security control**, not a forms-niceties check. It complements — never replaces — the
structural defences: isolation is the token→folder derivation ([principles](principles.md)),
SQL-safety is Dapper parameterization. Validation's job is to reject malformed or abusive
*payloads* before they reach a service or the disk. Because validators sit behind
`IValidationProvider` in the **service layer**, the API and the Console enforce the **same**
rules — there is no unguarded back door ([principle #6](principles.md), [tech-stack](tech-stack.md)).

Four things get tested:

1. **Per-validator, positive *and* negative** — every `IValidator<T>` via
   `FluentValidation.TestHelper`: `ShouldHaveValidationErrorFor` for each reject rule,
   `ShouldNotHaveValidationErrorFor` for the valid case, plus **boundary** cases (at the limit,
   one over, one under).

2. **Adversarial corpus, data-driven** — one shared "naughty strings" set
   (`Gert.Testing/TestData/NaughtyStrings.cs`: `../` traversal, null bytes, control/RTL-override
   chars, oversized blobs, SQL/FTS metacharacters, HTML/script, homoglyphs) fed through **every
   string field** via `[Theory]`. Each input must be **rejected or safely accepted — never crash,
   never slip through** to persistence. The concrete threat model:

   | User input | Threat | Rule under test |
   |------------|--------|-----------------|
   | Upload filename | path traversal / overwrite | reject separators & `..`; **extension allowlist** (pdf/docx/md/txt) |
   | Upload bytes / type | DoS, oversized payload | max size; content-type allowlist; reject empty |
   | Message / title text | DoS via huge payload; control chars | max length; reject null/whitespace-only; refuse control & bidi-override chars |
   | Model id | steering to an unintended model | must be in the **known-model allowlist** |
   | Tool name / toggles | invoking an unknown tool | must be a **registered** tool name (entitlement itself is authz — `Gert.Authentication.Tests` + [§6](#6-api-integration-tests--gertapitests)) |
   | Conversation / document id | tampered id (IDOR is structural; this is defence-in-depth) | well-formed id (e.g. GUID) **before** it reaches a repo |
   | Admin user `{key}` | path traversal → `rm -rf` of an arbitrary dir | must match `^[0-9a-f]{64}$`; resolved path asserted **under `/data/users/`** ([security F6](security.md#3-findings--remediations)) |
   | Web-search fetch URL | SSRF to internal services / metadata IP | scheme allowlist; private/loopback/link-local blocked; re-checked after redirects ([security F5](security.md#3-findings--remediations)) |
   | Pagination / `k` | negative/absurd values | positive, bounded |
   | RAG query text | FTS5 query-syntax abuse | carried as **data, not operators** (also a query-construction concern) |

3. **Fail-closed meta-test (the strongest guarantee)** — a reflection test over the assembly
   asserts **every request DTO a service accepts has a registered `IValidator<T>`**. A new input
   type therefore **cannot ship unvalidated** — the test goes red until a validator exists.
   Validation can't be silently forgotten. This is the executable form of
   [principle #6](principles.md).

4. **The provider contract** — `IValidationProvider` resolves the right validator and surfaces a
   **consistent error shape**, so the API renders a 400 `ProblemDetails` and the Console prints
   the same failure. Tested once here.

The cross-tier proofs live where the behaviour does:
- **Integration ([§6](#6-api-integration-tests--gertapitests))** — invalid input → **400
  `ProblemDetails`, never 500**, and it **never reaches the repository** (no partial write). A
  500 would mean a validator was bypassed and something deeper threw — a failing test by design.
- **Both hosts ([§7](#7-console-tests--gertconsoletests))** — the same invalid input is rejected
  on the **Console** path, proving the guarantee is service-layer-structural, not an API
  convenience.

---

## 6. API integration tests — `Gert.Api.Tests`

Through `GertApiFactory` (TestServer + fakes), over `HttpClient`. This tier proves the things
that only exist once you add HTTP:

- **Controllers / contracts**: each `/api/*` endpoint from [rest-api](rest-api.md) — status
  codes, DTO shapes, and **validation → 400 `ProblemDetails`, never 500**, with the invalid
  payload never reaching a repository (the integration half of [§5 Validation](#validation--the-input-security-boundary)).
- **SSE**: `POST` a message, read the response stream, parse `data:` frames back into
  `ChatEvent`s, and snapshot the sequence — the framing the service tests deliberately skip.
- **Auth middleware**: no token → 401; valid token → 200; expired/wrong-issuer/wrong-alg → 401.
- **Isolation / IDOR** (the headline test): user A uploads a doc into a project; user B requests
  it → 404, and B's project `rag.db` never contains A's chunks. The user key comes only from the
  token. The **one** request-supplied selector is `{pid}` — so a dedicated test tampers with it:
  pointing at another user's project id still resolves only under B's own folder (404, never A's
  data), and a non-UUID/`..` value is rejected by validation
  ([configuration §2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe), [principles](principles.md)).
- **Project isolation**: a query in project X cannot see project Y's rows (separate folders,
  separate DBs — the per-project case of [principle #2](principles.md)).
- **Admin RBAC**: non-admin → `/api/admin/users` 403; admin → 200.
- **Admin `{key}` traversal** ([security F6](security.md#3-findings--remediations)): `DELETE /api/admin/users/{key}`
  with a non-hex / `..` / absolute `{key}` is rejected (400/404) and **no directory outside
  `/data/users/{valid-key}` is touched** — asserted against a temp `DataRoot` with sentinel siblings.
- **Security headers / CSP** ([security F1](security.md#3-findings--remediations)): an HTML response
  carries the CSP and `X-Content-Type-Options: nosniff` / `Referrer-Policy` / `X-Frame-Options`
  headers; `connect-src` lists only the API origin + Pocket ID.
- **SSRF guard** ([security F5](security.md#3-findings--remediations)): the web-search fetcher,
  pointed at a private/loopback/`file:` URL (via the `FakeWebSearch` result set), refuses it — it
  never opens the connection.
- **Upload parser hardening** ([security F7](security.md#3-findings--remediations)): a DOCX carrying
  an external-entity reference (XXE) and an over-cap decompression-bomb each fail the *document*
  (`status='failed'`) without hanging or reading host files.
- **Lazy provisioning**: a first authenticated request creates the user's folder + the `default`
  project + schema ([principles](principles.md)).
- **Ingestion `BackgroundService`**: enqueue an upload, poll `GET /api/projects/{pid}/documents/{id}`
  until `Ready` (mirrors the polling decision in [decisions §6](decisions.md)).
- **SPA fallback**: `GET /some/client/route` → `index.html`; `GET /api/...` and `/healthz`
  are **not** swallowed by the fallback ([tech-stack](tech-stack.md)).

---

## 7. Console tests — `Gert.Console.Tests`

The Console drives the same services with `LocalUserContext` (single user, tools `"*"`) and
inline ingestion ([tech-stack](tech-stack.md)). Tests wire those services to `Gert.Testing`
fakes, redirect `Console.Out`, and assert:
- the `ChatEvent` stream **renders** correctly to stdout (text, tool-call lines, citations) —
  the Console's analog of the API's SSE rendering;
- inline ingestion of a sample file reports progress and ends `Ready`;
- **the same invalid input the API rejects is rejected here too** — proving validation is a
  service-layer guarantee, not an API convenience ([§5 Validation](#validation--the-input-security-boundary));
- the Console needs no API/auth (it builds with no reference to `Gert.Authentication`) — a
  structural guarantee we assert by it simply compiling and running headless.

---

## 8. Web tests

`Gert.Web` is no-build native ESM ([ui-components](ui-components.md)); we keep the **no-npm**
rule into testing too. Both web tiers run on the **same Python + Playwright** stack — no Node,
no jsdom, no test-runner package. The browser is the DOM/JS engine; Python is the runner.

- **Component units** (`tools/smoke/tests/test_components.py`). A VanJS component is a function
  returning a real DOM node, and its reactivity needs a real DOM — so we mount the **actual,
  unmocked** module in a browser and assert. Python drives it via `page.evaluate()` against a
  tiny `tests/harness.html` (import map + a `__mount` helper) served on the same origin so
  `/components/...` and `/state/...` imports resolve:

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
  launch overhead — reuse one context across tests; and VanJS batches DOM updates on a
  microtask, so `await` a tick before asserting.

- **Full-app E2E** ([§9](#9-the-python-dev-launcher)). Loading the whole SPA in a real browser
  is the truest test of an ESM/import-map app — it catches a broken `import` or import map that
  a component-isolated test would miss.

The Fake host serves the harness: the Fake profile maps `tests/web/` at `/tests/` (dev-only),
so the harness imports the real app modules on the same origin.

---

## 9. The Python dev launcher

`tools/smoke/run.py` — the "create JWTs, then click" launcher. Pure Python + Playwright; no
npm, no .NET SDK needed beyond running the host.

**What it does, in order:**
1. **Boot the mock upstreams** — start `tools/smoke/mocks` (vLLM + SearXNG) on localhost ports, then
   **boot the host** with `dotnet run --launch-profile FakeE2E`, whose config points the **real**
   `Gert.External` clients at those mock URLs (sandbox = stub). Or attach to an already-running pair
   with `--base-url`; wait for `/healthz`.
2. **Mint tokens** — call `tokens.mint("admin")` / `tokens.mint("user")` in-process
   ([§4.3](#43-jwt-minting--a-python-token-harness)). No HTTP round-trip; the same harness a dev
   runs from the CLI for local testing.
3. **Inject + drive** — for each `(browser, role)` in the matrix, launch the browser, seed the
   token (localStorage, matching how `services/auth.js` stores it — [ui-components](ui-components.md)),
   load the SPA, and run the scenarios.
4. **Report** — pass/fail per scenario; screenshot + trace on failure under `tools/smoke/artifacts/`.

**Matrix:** `{chromium, firefox} × {admin, user}` for the full click-through, plus the **`limited`**
role in the RBAC/entitlement scenario (below). Flags:
`--browser chromium|firefox|all`, `--role admin|user|limited|all`, `--headed`, `--keep-open`,
`--base-url <url>`. The host and the mock upstreams emit the shared NDJSON logs
([operations § Logging format](operations.md#logging-format-shared)), so a failed run's interleaved
output parses with one reader.

**Scenarios** (cover the mockup's interactive surface — [ui-components §7](ui-components.md#7-feature--component-map)):
- New chat → type → send → **streaming** message appears → **tool cards** expand → citations/footnotes render.
- **Knowledge**: drag/upload a file → status pill goes `Processing` → `Ready`; toggle use-in-chat.
- **Canvas**: switch artifact tabs (md/html/svg/py); flip **Rendered/Source**; the HTML
  artifact renders in its sandboxed iframe; the code artifact shows the Problems panel.
- **Chrome**: theme toggle persists; model picker selects a model; responsive drawers open/close
  at mobile widths.
- **RBAC + IDOR + entitlement**: as **admin**, `/admin/users` loads; as **user**, it's hidden and
  the API returns 403; a user cannot open another user's document. As **`limited`**, the Search and
  Sandbox tool chips are unavailable and the API drops those tools even if the request asks for them
  (the entitlement ceiling, [auth](auth.md#enforcement--the-claim-is-the-ceiling)).

**Setup** (via **[uv](https://github.com/astral-sh/uv)** — the project's Python env manager):
`uv venv && uv pip install -r requirements.txt && uv run playwright install chromium firefox`.
Run the suite with `uv run python -m tools.smoke.run`, and mint a local token with
`uv run python -m tools.smoke.tokens --role admin`.

---

## 10. Tooling summary

| Concern | Choice | Notes |
|---------|--------|-------|
| .NET test runner | **xUnit** | De-facto for ASP.NET Core. |
| Assertions | **FluentValidation.TestHelper** + **FluentAssertions** | Readable failures. |
| Mocks | **NSubstitute** | Fakes for the external world live in `Gert.Testing`, not ad-hoc mocks. |
| Snapshots | **Verify** | `ChatEvent` streams + SSE frames. |
| API integration | **`WebApplicationFactory<Program>`** | Real pipeline, fake externals. |
| SQLite | **`Microsoft.Data.Sqlite`** temp DB + vec0/FTS5 | Real SQL, no mocking the database. |
| Web tests (component units + E2E) | **Playwright (Python)** | Browser as the DOM/JS engine; Chromium + Firefox; no npm, no Node. |
| Python env | **uv** | Manages the venv + deps for `tools/smoke` (`uv venv`, `uv run`). |
| JWT (tests) | **RS256 key generated on first run** (git-ignored) + JWKS | Exercises the real RS256/JWKS path; no key ever committed. |
| External world (.NET tiers) | **in-process fakes** (`AddGertFakes`) | Swap the `Gert.External` ports; fast, deterministic, no sockets. |
| External world (E2E) | **Python mock upstreams** (`tools/smoke/mocks`) | Real adapters → mock vLLM/SearXNG; exercises adapter HTTP + SSRF guard. Sandbox stays a .NET stub. |

---

## 11. CI

1. **`dotnet test`** — runs `Gert.*.Tests` (unit + DB + API integration + console). Fast,
   hermetic, no network.
2. **Web tests job** — boot `tools/smoke/mocks` + `dotnet run --launch-profile FakeE2E` in the
   background, then `python tools/smoke/run.py --browser all --role all` (component units + full-app
   E2E). Uploads Playwright traces/screenshots on failure.

Both gate merges. The E2E job is the only one that needs browsers installed.

---

## 12. Non-goals

- **Real vLLM / GPU, real Pocket ID, real gVisor** are *not* in unit/integration/E2E — they're
  faked (the E2E exercises the real **adapter** code against Python mock upstreams, but never a real
  model server or sandbox). A thin, separate **staging smoke** (the same `run.py` pointed at a real
  deployment with `--base-url`) is the place to exercise the genuine articles; it is not part of the
  gating CI.
- **Load/perf testing** is out of scope at ~20 users.
- **Cross-browser pixel-diffing** — we assert behaviour and roles, not screenshots.
- **`Gert.Model.Tests`** — dropped: the models are POCOs; any record/DTO invariant worth
  asserting rides along in the service suite.

---

## Appendix A — The shared fake spec

The in-process .NET fakes ([§4.1](#41-the-fake-external-world)) and the Python mock upstreams
([§4.2](#42-two-ways-to-fake-the-outside-world)) only stay drift-free if they implement **one**
definition of behaviour. This appendix is that definition. The split:

- **Algorithms are code** — each side implements A.1/A.2 from this spec, kept honest by a committed
  **golden file** both assert against.
- **Canned data is data** — the chat/search fixtures live **once** in `tests/shared/fixtures.json`;
  the .NET side links it as an embedded resource, the Python side reads the same file. No second copy.

```
tests/shared/                  # one source of truth for both fake layers
  fixtures.json                #   canned chat completions + web-search results (schema: A.3 / A.4)
  embeddings_golden.json       #   text → expected vector samples — the A.2 conformance check
```

### A.1 Determinism contract
Equal input ⇒ **identical** output on both sides, so KNN/RRF order and citations assert the same in
`Gert.Database.Sqlite.Tests` and in the browser E2E. Everything below is specified to the byte so C#
and Python agree without coordination.

### A.2 Deterministic embeddings (`hash → 1024-dim unit vector`)
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
  end. `norm` is never zero in practice; if it were, return the canonical basis vector `e₀`.
- **Why it works for tests:** distinct texts map to near-orthogonal directions in 1024-dim, so cosine
  distances are well-separated — no fragile ties. A query embedded with the *same* function is its
  own nearest neighbour, which is exactly what lets RAG tests assert an exact hit order.
- **Conformance:** the .NET impl generates `embeddings_golden.json` once (a handful of texts →
  vectors); thereafter a `Gert.Testing` test **and** a Python test both assert `embed(t)` matches the
  golden to float32 equality. If either drifts, both go red — that's the anti-drift guarantee, made
  executable.

### A.3 Canned chat completions
Both the `FakeChatModel` and the mock `/v1/chat/completions` resolve a reply from
`fixtures.json` by the **last user message**, and play it as a **token-by-token stream**. The
fixture is at the **model wire layer** — assistant content deltas and (optionally) a tool call,
nothing higher: citations and artifacts are the *orchestrator's* job downstream, never scripted here.

```jsonc
// fixtures.json → "completions": [ … ]
{
  "match": "exact",                       // "exact" | "contains" against the trimmed last user message
  "when":  "should I use Qdrant or sqlite-vec?",
  "deltas": ["Short version: ", "use ", "sqlite-vec."],   // streamed one chunk per element
  "finish": "stop",                       // finish_reason; "tool_calls" when tool_call is present
  "usage":  { "completion_tokens": 12 }
}
```

A tool-exercising fixture emits a tool call first, then — on the **follow-up** model call (whose
messages now carry the tool result) — matches the *same* `when` and plays `after_tool.deltas`:

```jsonc
{
  "when": "search my docs about qdrant",
  "tool_call": { "name": "search_documents",
                 "arguments": "{\"query\":\"qdrant\",\"k\":5}" },   // OpenAI: arguments is a JSON string
  "finish": "tool_calls",
  "after_tool": { "deltas": ["Based on your docs, sqlite-vec wins [1]."], "finish": "stop" }
}
```

- **Fallback:** no match ⇒ `"fallback": "echo"` streams `Echo: <message>`, tokenised on word
  boundaries (spaces preserved) so the typewriter caret and SSE framing have real chunks to render.
- **One asymmetry, same data:** the **Python mock emits OpenAI-compatible SSE** (`data: {chunk}\n\n`,
  `delta.content` / `delta.tool_calls`, terminal `[DONE]`) because the *real* `Gert.External` adapter
  parses that wire format ([§4.2](#42-two-ways-to-fake-the-outside-world)); the **.NET fake yields the
  `IChatModelClient` port's types directly** (no socket). Both are driven by the identical fixture
  entry, so the resulting `ChatEvent` stream is the same.

### A.4 Canned web search (SearXNG)
`FakeWebSearch` and the mock SearXNG resolve results by query from the same file, in SearXNG's JSON
shape. At least one fixture is **adversarial by design** for the SSRF test:

```jsonc
// fixtures.json → "search": { "<query>": { "results": [ … ] } }
"results": [
  { "title": "Qdrant vs sqlite-vec", "url": "https://example.test/bench", "content": "…" },
  { "title": "internal",            "url": "http://169.254.169.254/latest/meta-data/", "content": "…" }
]
```

The second result drives [F5](security.md#3-findings--remediations): the real adapter's summarize
step must **refuse** that URL (private/link-local), proving the SSRF guard end-to-end. The sandbox is
not represented here — it is process-exec, not HTTP, so it stays a .NET `StubSandbox`
([§4.2](#42-two-ways-to-fake-the-outside-world)).

### A.5 Ownership
`tests/shared/` is the seam's contract. Changing a fixture or the embedding algorithm is a
deliberate edit to **one** place; the golden-file conformance tests on both sides fail loudly if the
two implementations ever diverge.
