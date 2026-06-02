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
[storage-and-data](storage-and-data.md), and the endpoints in [rest-api](rest-api.md).

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
  Gert.Authentication.Tests/    # JWT claims → IUserContext; sub→key (sha256); denylist
  Gert.Api.Tests/               # integration via GertApiFactory — controllers, SSE, auth, IDOR, admin, SPA fallback
  Gert.Console.Tests/           # drive the Console host with fakes; assert rendered ChatEvent stream
  web/
    harness.html                # import map + __mount helper — Fake host serves it at /tests/ for component units

tools/
  smoke/                        # Python E2E launcher (uv-managed; no npm, no .NET) — drives the Fake host
    run.py                      #   boot Fake host → mint tokens → Playwright matrix → report
    tokens.py                   #   role→claims map; mint(role) RS256 via pyjwt; CLI for local dev
    requirements.txt            #   playwright, pyjwt   (installed via `uv pip install -r`)
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
- **`FakeEmbeddings`** — maps text → a deterministic 1024-dim unit vector (hash-seeded). KNN
  ordering is therefore **stable across runs**, which is what lets us assert exact retrieval
  order in RAG tests instead of "something came back."
- **`FakeWebSearch`** — fixed result set for the web-search tool.
- **`StubSandbox`** — returns scripted stdout/exit without launching a container; a "throws"
  variant covers the sandbox-failure path.

### 4.2 Two faces of the same fake host
The identical fake wiring is exposed two ways, so the browser and the .NET tests can't drift:

| Face | Transport | Used by | How |
|------|-----------|---------|-----|
| **TestServer** | in-process, no socket | `Gert.Api.Tests` | `GertApiFactory : WebApplicationFactory<Program>` → `HttpClient` |
| **Fake Kestrel** | real port on localhost | the Python launcher / browser | `dotnet run --launch-profile Fake` (a launch profile that calls the same `AddGertFakes()`) |

Both resolve through one extension — `AddGertFakes(this IServiceCollection)` — which replaces
the model/search/sandbox registrations, points `DataRoot` at a temp dir, and installs the test
JWT validation. The only difference is whether Kestrel binds a socket.

### 4.3 JWT minting — a Python token harness
No dev-only token *endpoint* on the host. **Python mints the tokens**; the host only *trusts*
a dev key, and validates through the **same** RS256/JWKS path it uses for Pocket ID in prod —
so the dev shortcut can't hide a validation bug.

- **`tools/smoke/tokens.py`** — a small harness with a role→claims map and a `mint(role,
  **overrides)` function. Signs RS256 with a **dev keypair** using `pyjwt`. Roles are just data,
  so adding a privilege set is a one-line edit:

  ```python
  ROLES = {
      "admin": {"sub": "dev-admin", "groups": ["gert-admins"], "gert_tools": "*"},
      "user":  {"sub": "dev-user",  "groups": ["gert-users"],  "gert_tools": "rag,search"},
  }
  # CLI: `python -m tools.smoke.tokens --role admin`  → prints a token (and a paste-ready
  #      localStorage snippet) so a dev can use the app locally with NO Pocket ID setup.
  ```

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
`sha256(sub)` directories exist and neither `rag.db` contains the other's chunks.

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
JWT claims (`sub`, `groups`, `gert_tools`) → `IUserContext`; `sha256(sub)` key derivation
([decisions §3](decisions.md)); the admin policy; and the `sub`-denylist fast cut-off
([decisions §4](decisions.md)).

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
- **Auth middleware**: no token → 401; valid token → 200; denylisted `sub` → 401.
- **Isolation / IDOR** (the headline test): user A uploads a doc; user B requests A's document
  id → 404, and B's `rag.db` never contains A's chunks. The key comes only from the token, so
  there's no parameter to tamper with ([principles](principles.md)).
- **Admin RBAC**: non-admin → `/api/admin/users` 403; admin → 200.
- **Lazy provisioning**: a first authenticated request creates the user's folder + schema
  ([principles](principles.md)).
- **Ingestion `BackgroundService`**: enqueue an upload, poll `GET /api/documents/{id}` until
  `Ready` (mirrors the polling decision in [decisions §6](decisions.md)).
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
1. **Boot the Fake host** — `dotnet run --launch-profile Fake` as a subprocess (or attach to an
   already-running one with `--base-url`); wait for `/healthz`.
2. **Mint tokens** — call `tokens.mint("admin")` / `tokens.mint("user")` in-process
   ([§4.3](#43-jwt-minting--a-python-token-harness)). No HTTP round-trip; the same harness a dev
   runs from the CLI for local testing.
3. **Inject + drive** — for each `(browser, role)` in the matrix, launch the browser, seed the
   token (localStorage, matching how `services/auth.js` stores it — [ui-components](ui-components.md)),
   load the SPA, and run the scenarios.
4. **Report** — pass/fail per scenario; screenshot + trace on failure under `tools/smoke/artifacts/`.

**Matrix:** `{chromium, firefox} × {admin, user}`. Flags:
`--browser chromium|firefox|all`, `--role admin|user|all`, `--headed`, `--keep-open`,
`--base-url <url>`.

**Scenarios** (cover the mockup's interactive surface — [ui-components §7](ui-components.md#7-feature--component-map)):
- New chat → type → send → **streaming** message appears → **tool cards** expand → citations/footnotes render.
- **Knowledge**: drag/upload a file → status pill goes `Processing` → `Ready`; toggle use-in-chat.
- **Canvas**: switch artifact tabs (md/html/svg/py); flip **Rendered/Source**; the HTML
  artifact renders in its sandboxed iframe; the code artifact shows the Problems panel.
- **Chrome**: theme toggle persists; model picker selects a model; responsive drawers open/close
  at mobile widths.
- **RBAC + IDOR**: as **admin**, `/admin/users` loads; as **user**, it's hidden and the API
  returns 403; a user cannot open another user's document.

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

---

## 11. CI

1. **`dotnet test`** — runs `Gert.*.Tests` (unit + DB + API integration + console). Fast,
   hermetic, no network.
2. **Web tests job** — `dotnet run --launch-profile Fake` in the background, then
   `python tools/smoke/run.py --browser all --role all` (component units + full-app E2E).
   Uploads Playwright traces/screenshots on failure.

Both gate merges. The E2E job is the only one that needs browsers installed.

---

## 12. Non-goals

- **Real vLLM / GPU, real Pocket ID, real gVisor** are *not* in unit/integration/E2E — they're
  faked. A thin, separate **staging smoke** (the same `run.py` pointed at a real deployment with
  `--base-url`) is the place to exercise the genuine articles; it is not part of the gating CI.
- **Load/perf testing** is out of scope at ~20 users.
- **Cross-browser pixel-diffing** — we assert behaviour and roles, not screenshots.
- **`Gert.Model.Tests`** — dropped: the models are POCOs; any record/DTO invariant worth
  asserting rides along in the service suite.
