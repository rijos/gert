# Implementation plan (agentic)

A build plan structured for execution by coding agents. It turns the design set into an
**dependency-ordered sequence of small, independently-verifiable units**, each with explicit
inputs, the files it touches, the design sections it implements, and an **acceptance gate** (the
tests that must pass before it's done). The architecture's inward-only reference direction
([tech-stack](tech-stack.md#architecture)) *is* the build order: `Model → Service abstractions →
adapters → hosts → web → tooling`, with the testing pyramid ([testing](testing.md)) as the gate at
every step.

> **One-line strategy:** stand up the **seams and fakes first**, drive a **thin end-to-end slice**
> (auth → provision → conversation → streamed message → persisted) to de-risk integration, *then*
> fan out feature-by-feature — each unit landing with its tests green and its security control
> ([security](security.md)) built in, not bolted on.

---

## How an agent runs a unit

Every unit is sized for one focused agent session. The contract:

1. **Pick the lowest-numbered unit whose `Depends` are all done.** Respect the dependency graph
   ([§ Parallelization](#parallelization-map)); independent units may run concurrently in separate
   worktrees.
2. **Work on a branch/worktree** named for the unit (`feat/u07-chat-orchestrator`). Greenfield repo
   → branch off `main`.
3. **Test-first where the gate is behavioural.** The unit's **Acceptance** is the definition of
   done — write/extend those tests, make them pass, keep the rest of the suite green.
4. **Build the security control named in the unit as part of it**, not later. A unit that "works"
   but skips its `Hardens:` item is *not* done.
5. **Stop and surface** if a design doc is ambiguous or contradicts another — do not invent
   behaviour. The [open items](#open-items-to-confirm-before-or-during-build) list known gaps.
6. **Definition of done:** target tests pass, full suite stays green, no new analyzer/build
   warnings, design-doc links in code comments where a non-obvious choice traces to a decision.

**Conventions:** xUnit + FluentAssertions + NSubstitute + Verify ([testing §10](testing.md#10-tooling-summary));
conventional-commit messages; secrets only from env / `dotnet user-secrets` ([security F8](security.md#3-findings--remediations));
nothing under `.dev/` ever committed.

---

## Milestones

| # | Milestone | Units | Proves |
|---|-----------|-------|--------|
| **M0** | Skeleton compiles | U0–U3 | Solution builds; reference direction is compiler-enforced; fakes exist. |
| **M1** | **Walking skeleton** (vertical slice) | U4a, U5, U7a, U8, U9a | A minted JWT → provisioned folder → create conversation → POST message → **SSE stream from `FakeChatModel`** → persisted in real SQLite. End-to-end, one path, fully tested. |
| **M2** | Feature-complete service + API | U4b, U6, U7b–d, U9b | Every `/api/*` endpoint, the full tool loop, ingestion, validation, RBAC/IDOR all green in `Gert.Api.Tests`. |
| **M3** | Hosts + SPA wired | U10, U11, U12 | Console drives the same services; the real SPA loads and streams in a browser. |
| **M4** | Hardened + E2E gating | U13, U14, U15 | Python E2E matrix green; minified release; CI gates merges; all F1–F12 controls present and tested. |

Reaching **M1 is the priority** — it collapses the integration risk (JWT validation, SSE framing,
real-SQLite isolation, lazy provisioning) into one proven thread before any feature breadth.

---

## Phase 0 — Scaffolding (M0)

### U0 — Solution & project skeleton
- **Goal:** Create `Gert.sln` and every project from [tech-stack § Solution layout](tech-stack.md#solution-layout-projects) with the **correct reference direction** and nothing else.
- **Depends:** —
- **Touches:** all `Gert.*` csproj, `tests/*`, `Directory.Build.props` (shared `net10.0`, nullable, warnings-as-errors), `.gitignore` (`.dev/`, `bin/`, `obj/`, secrets), `.editorconfig`.
- **Design:** [tech-stack § Architecture](tech-stack.md#architecture).
- **Hardens:** F8 (gitignore secrets + `.dev/`).
- **Acceptance:** `dotnet build` succeeds on empty projects. An **architecture test** (e.g. NetArchTest in `Gert.Service.Tests`) asserts `Gert.Service` references neither `Gert.Api`, `Gert.Authentication`, nor any `Gert.Database.*` — the structural guarantee, enforced from day one.

### U1 — `Gert.Model`
- **Goal:** POCOs + DTOs: `Conversation, Message, ToolCall, Citation, Artifact, Document, Chunk, ChatEvent` (+ event subtypes), and request DTOs for every endpoint body in [rest-api](rest-api.md).
- **Depends:** U0
- **Touches:** `Gert.Model/`
- **Design:** [storage-and-data § Data model](storage-and-data.md#data-model), [rest-api](rest-api.md).
- **Acceptance:** compiles; `ChatEvent` is a discriminated shape the SSE layer and Console can both render. (No `Gert.Model.Tests` — [testing §12](testing.md#12-non-goals).)

### U2 — Service seams (interfaces only)
- **Goal:** Define every abstraction with **no implementations**: `IUserContext` (incl. `Iss`, `Sub`, `IsAdmin`, `AllowedTools`), `IGertServices` + granular `IChatService/IConversationService/IDocumentService/IArtifactService/IProjectService/ISettingsService/IMemoryService/IAdminService`, `IDatabaseProvider`, `IChatRepository`, `IRagRepository`, `IValidationProvider`, `ITool` + `ToolRegistry`, and the **external-world** ports `IChatModelClient`, `IEmbeddingClient`, `IWebSearch`, `ISandbox`.
- **Depends:** U1
- **Touches:** `Gert.Service/` (interface files + `ToolRegistry`).
- **Design:** [tech-stack § Architecture](tech-stack.md#architecture), [auth § user context](auth.md#the-user-context-resolved-per-request), [chat-and-tools](chat-and-tools.md).
- **Note:** the external-world ports are *defined* here in `Gert.Service`; their **real** implementations live in **`Gert.External`** (built in U10), exactly parallel to the database seam ([tech-stack § Architecture](tech-stack.md#architecture)).
- **Acceptance:** compiles; interfaces match the method shapes the design implies (streaming chat returns `IAsyncEnumerable<ChatEvent>`).

### U3 — `Gert.Testing` shared infra
- **Goal:** The fakes + fixtures every later unit tests against — built early so nothing drifts.
- **Depends:** U2
- **Touches:** `tests/shared/` (`fixtures.json`, generate `embeddings_golden.json`); `tests/Gert.Testing/`: `FakeChatModel`, `FakeEmbeddings` (hash→1024-dim deterministic), `FakeWebSearch`, `StubSandbox`, `TempDataRoot`, `TestTokens` (ephemeral RSA + JWKS), `TestData/NaughtyStrings.cs`. `GertApiFactory` is stubbed now, fleshed out at U9a.
- **Design:** [testing §4](testing.md#4-shared-test-infrastructure--gerttesting) and **[Appendix A — the shared fake spec](testing.md#appendix-a--the-shared-fake-spec)** (the embedding algorithm, fixture schema, and `tests/shared/` location both fake layers implement).
- **Acceptance:** fakes implement the U2 ports; `FakeEmbeddings` matches the [A.2](testing.md#a2-deterministic-embeddings-hash--1024-dim-unit-vector) golden file; chat/search read `tests/shared/fixtures.json`. (The matching Python conformance test lands in U13.)

---

## Phase 1 — Persistence & isolation core (M1 foundations)

### U4a — SQLite provider + chat repository + migrations
- **Goal:** `SqliteDatabaseProvider` (WAL, `busy_timeout`, `foreign_keys`, vec0 extension load, per-project DB open), `SqliteChatRepository` (Dapper), migrations `chat/001_init.sql`.
- **Depends:** U2
- **Touches:** `Gert.Database.Sqlite/`, `tests/Gert.Database.Sqlite.Tests/`
- **Design:** [storage-and-data § connection management / chat.db](storage-and-data.md#chatdb), [tech-stack](tech-stack.md).
- **Acceptance (real temp SQLite):** migrations apply from empty; conversation/message/tool_call/citation/artifact round-trips with correct ordering and cascade deletes.

### U4b — RAG repository (vec0 + FTS5 + RRF) *(M2)*
- **Goal:** `SqliteRagRepository` — insert chunks, KNN (`vec0 MATCH … ORDER BY distance`), FTS5 (`bm25`), **RRF fusion**; migrations `rag/001_init.sql` incl. `documents.kind/pinned`.
- **Depends:** U4a, U3
- **Touches:** `Gert.Database.Sqlite/`, tests.
- **Design:** [chat-and-tools § hybrid retrieval](chat-and-tools.md#rag-hybrid-retrieval), [storage-and-data § rag.db](storage-and-data.md#ragdb-sqlite-vec).
- **Acceptance:** **deterministic RRF order** asserted (thanks to `FakeEmbeddings`); memory rows (`kind='memory'`) retrieved by the same query; this is the riskiest SQL in the system, so coverage is thorough.

### U5 — Paths, provisioning gate & isolation *(security-critical)*
- **Goal:** `SqliteDatabasePaths` (`sha256(iss + "\n" + sub)`, project-scoped methods), `EnsureProvisioned(iss, sub)` with the **fail-closed validate-before-disk gate** and a descriptive **`meta.json` sidecar** (healed when unreadable), `EnsureProject` + lazy `default` project.
- **Depends:** U4a
- **Touches:** `Gert.Database.Sqlite/` (or a small `Provisioning` service in `Gert.Service` over `IDatabaseProvider`), tests in `Gert.Database.Sqlite.Tests`.
- **Design:** [storage-and-data § lazy provisioning](storage-and-data.md#lazy-provisioning--migrations), [decisions §3](decisions.md#3-folder-key).
- **Hardens:** **F12** (anti-reuse via the `sub` anchor, validate-before-disk); foundation for F6.
- **Acceptance:** malformed/unexpected-`iss` identity rejected **before any folder is created** (assert no dir on disk); a truncated `meta.json` is healed on the next touch; two-user run yields two `sha256(iss+sub)` dirs, neither `rag.db` sees the other's rows; a `../`/non-UUID `pid` is rejected and never escapes `Root`.

---

## Phase 2 — Validation & services (M1 slice → M2 breadth)

### U6 — Validation layer
- **Goal:** A FluentValidation `IValidator<T>` for **every** request DTO, `IValidationProvider` with a consistent error shape, and the **fail-closed reflection meta-test**.
- **Depends:** U2, U3
- **Touches:** `Gert.Service/Validation/`, `tests/Gert.Service.Tests/`.
- **Design:** [testing § Validation](testing.md#validation--the-input-security-boundary), [principle #6](principles.md).
- **Hardens:** **F6** (admin `{key}` `^[0-9a-f]{64}$`; `pid` shape), principle #6, validation rows for filename allowlist, sizes, `model_id` allowlist, `k` bounds, FTS-as-data.
- **Acceptance:** every DTO has a validator (reflection meta-test green); `NaughtyStrings` `[Theory]` over every string field — each input **rejected or safely accepted, never crashes, never persists**; positive/negative/boundary per validator.

### U7a — Conversation/Document/Artifact services + minimal ChatService *(M1)*
- **Goal:** CRUD services with ownership semantics, plus a **minimal `ChatService`** that streams `FakeChatModel` output as `ChatEvent`s and persists — *no tools yet*. This is the slice's service layer.
- **Depends:** U4a, U5, U6
- **Touches:** `Gert.Service/Conversations|Documents|Artifacts|Chat/`, `tests/Gert.Service.Tests/`.
- **Design:** [chat-and-tools § tool loop](chat-and-tools.md#chat-orchestration-the-tool-loop) (no-tool path), [rest-api](rest-api.md).
- **Acceptance:** Verify-snapshot the no-tool `ChatEvent` sequence (`message_start → delta… → message_end`); assistant message + token count persisted to `chat.db`.

### U7b — Full chat orchestrator (the tool loop) *(M2)*
- **Goal:** Extend `ChatService` to the full loop: pinned-memory/instructions prepend (step 0), tool advertisement with the **entitlement ∩ conversation ∩ request** intersection, tool-call/result events, feedback round-trips, citation/artifact extraction.
- **Depends:** U7a, U7c
- **Touches:** `Gert.Service/Chat/`, tests.
- **Design:** [chat-and-tools § tool loop](chat-and-tools.md#chat-orchestration-the-tool-loop), [auth § entitlement ceiling](auth.md#enforcement--the-claim-is-the-ceiling).
- **Acceptance:** scripted `FakeChatModel` tool call drives a full `tool_call→tool_result→delta` snapshot; a user lacking `sandbox` **never** has `run_python` advertised regardless of toggles.

### U7c — Tools: RAG / web-search / sandbox *(M2, partly hardened in U10)*
- **Goal:** `RagTool` (hybrid retrieve → citations), `WebSearchTool`, `SandboxTool`, registered in `ToolRegistry`.
- **Depends:** U4b, U2
- **Touches:** `Gert.Service/Tools/`, tests. (Tools call the U2 ports; the real `Gert.External` clients behind them arrive in U10 — until then, fakes.)
- **Design:** [chat-and-tools § tools detail](chat-and-tools.md#tools-detail).
- **Acceptance:** each tool exercised via fakes incl. the `StubSandbox` failure variant; entitlement honoured at the tool boundary. (SSRF + real sandbox land in U10.)

### U7d — Ingestion pipeline *(M2)*
- **Goal:** `IngestionService.Ingest` — extract → chunk (token-aware + overlap, record page/§) → embed (batch) → write, with progress; "no extractable text → Failed".
- **Depends:** U4b
- **Touches:** `Gert.Service/Ingestion/`, tests.
- **Design:** [chat-and-tools § ingestion](chat-and-tools.md#document-ingestion-pipeline), [decisions §5](decisions.md#5-ocr).
- **Acceptance:** inline run with fakes asserts chunk counts, vectors land in the repo, failure path sets `status='failed'`. (Parser **subprocess isolation** is U10.)

---

## Phase 3 — Auth & API host (M1 slice → M2 breadth)

### U8 — `Gert.Authentication` adapter *(M1)*
- **Goal:** `HttpUserContext` (claims → `IUserContext` incl. `Iss`, `AllowedTools` normalization), `JwtBearer` config (Authority/JWKS, `NameClaimType`/`RoleClaimType`, **`ValidAlgorithms=["RS256"]`**), `Policies` (Admin + fallback). **No denylist** — stateless revocation (decisions §4).
- **Depends:** U2
- **Touches:** `Gert.Authentication/`, `tests/Gert.Authentication.Tests/`.
- **Design:** [auth](auth.md), [decisions §4](decisions.md#4-token-lifetime--revocation).
- **Hardens:** **F11** (alg pin); supports F12 (`iss`/`sub` surfaced and validated).
- **Acceptance:** claim→context mapping incl. `iss`; `sha256(iss+sub)` key; expired/wrong-issuer/wrong-alg rejected; `gert_tools` parsing (canonical scope string / `*` / absent→default).

### U9a — API walking skeleton *(M1)*
- **Goal:** `Program.cs` DI wiring, `GertApiFactory` completed (`AddGertFakes`, temp `DataRoot`, test JWT), and the **one vertical path**: Models + Conversations(create/list) + Messages(SSE) controllers + SPA-fallback. SSE renderer for `ChatEvent`.
- **Depends:** U7a, U8, U3
- **Touches:** `Gert.Api/` (`Program.cs`, `Controllers/`, SSE), `tests/Gert.Api.Tests/`.
- **Design:** [rest-api](rest-api.md), [tech-stack](tech-stack.md), [testing §6](testing.md#6-api-integration-tests--gertapitests).
- **Acceptance (the M1 gate):** through `WebApplicationFactory`, a minted JWT → POST message → parse `data:` frames → `ChatEvent` snapshot; no token → 401; **lazy provisioning** creates folder + `default` project on first call; SPA fallback serves `index.html` but not `/api/*`.

### U9b — API breadth: all endpoints + RBAC/IDOR + headers *(M2)*
- **Goal:** Remaining controllers (Settings, Projects, full Conversations/Messages, Documents+poll, Memory, Artifacts, Account export/delete, Admin), `ProblemDetails`, the ingestion `BackgroundService`, **security-headers/CSP middleware**, and **per-user rate limiting**.
- **Depends:** U9a, U4b, U6, U7b, U7d
- **Touches:** `Gert.Api/`, tests.
- **Design:** [rest-api](rest-api.md), [operations § headers/CSP](operations.md#http-security-headers--csp).
- **Hardens:** **F1** (CSP + headers), **F6** (admin `{key}` traversal guard at the route), **F10** (rate limits), **F9** (HSTS).
- **Acceptance:** every endpoint's status/DTO contract; **invalid input → 400 ProblemDetails, never 500, never reaches a repo**; IDOR test incl. **`{pid}` tamper** (resolves only under caller's folder → 404); **admin `{key}` traversal** rejected with no out-of-tree deletion; non-admin→admin 403; CSP/`nosniff`/`Referrer-Policy` present with `connect-src` limited.

---

## Phase 4 — External-I/O hardening, Console, SPA (M3)

### U10 — `Gert.External` real adapters + their controls
- **Goal:** Fill **`Gert.External`** with the real port implementations: `IChatModelClient`/`IEmbeddingClient` (OpenAI-compatible → vLLM, `IHttpClientFactory` + Polly), `IWebSearch` (SearXNG) **with the SSRF guard**, `ISandbox` (gVisor `runsc`, **egress off by default**), and `IIsolatedExtractor` — the **unprivileged subprocess** the ingestion pipeline calls for PDF/DOCX parsing. `AddGertExternal(cfg)` registers them; secrets from env/user-secrets.
- **Depends:** U7c, U7d, U9b
- **Touches:** `Gert.External/` (`Vllm/`, `Search/`, `Sandbox/`, `Isolation/`), `Gert.Service/Ingestion` (calls `IIsolatedExtractor`), `Gert.Api`/`Gert.Console` config.
- **Design:** [chat-and-tools § web search / sandbox / ingestion](chat-and-tools.md#tools-detail), [security F5/F7/F8](security.md#3-findings--remediations).
- **Hardens:** **F5** (SSRF: scheme allowlist, private/loopback/link-local block, redirect re-check, size/time caps; sandbox egress off), **F7** (extraction in unprivileged, `RLIMIT_*`+timeout subprocess; DTD/XXE off; zip-bomb caps), **F8** (secrets).
- **Acceptance:** SSRF fetcher refuses private/loopback/`file:` (never opens the socket); a DOCX with an external entity and an over-cap bomb each fail **the document**, not the host; resilience surfaces upstream failures as `error` SSE, not 500.

### U11 — `Gert.Console` host
- **Goal:** `LocalUserContext` (single user, tools `*`), inline ingestion, `ChatEvent`→stdout renderer.
- **Depends:** U7b, U7d, U10
- **Touches:** `Gert.Console/`, `tests/Gert.Console.Tests/`.
- **Design:** [tech-stack § architecture](tech-stack.md#architecture), [testing §7](testing.md#7-console-tests--gertconsoletests).
- **Acceptance:** stream renders to stdout; inline ingestion ends `Ready`; **same invalid input the API rejects is rejected here** (service-layer guarantee); compiles with **no** `Gert.Authentication` reference.

### U12 — Web SPA
- **Goal:** Build the SPA from [`uistyle.html`](../../uistyle.html) into the `wwwroot` layout: vendored `lib/` (van, van-x, router), `state/`, `services/`, `components/`, `pages/`, split `styles/`. Wire auth (PKCE), SSE streaming, project picker, settings.
- **Depends:** U9b (contracts stable)
- **Touches:** `Gert.Api/wwwroot/`.
- **Design:** [ui-components](ui-components.md), [configuration §8](configuration.md#8-impact-on-the-spa).
- **Hardens:** **F2** (access token **in-memory only**), **F3** (HTML **and SVG** artifacts in sandboxed `srcdoc` iframe, no `allow-same-origin`), **F4** (sanitized markdown, safe links).
- **Acceptance:** app loads via import map and streams a message against the Fake host (verified by U13); no token in `localStorage`; SVG/HTML artifacts render sandboxed.

---

## Phase 5 — Tooling, release, CI (M4)

### U13 — Python smoke/E2E + token harness + mock upstreams
- **Goal:** `tools/smoke` (uv-managed): `tokens.py` (RS256 via pyjwt, **generated-on-first-run git-ignored `.dev/jwt/` key**), the **`mocks/` upstreams** (OpenAI-compatible vLLM chat+embeddings, SearXNG) that the host's **real `Gert.External` adapters** point at, the **`FakeE2E` launch profile**, `run.py` launcher, `pages.py`, scenario tests, and **component-unit tests** via `tests/web/harness.html` + `page.evaluate`.
- **Depends:** U12, U9b, U10
- **Touches:** `tools/smoke/` (incl. `mocks/`), `tests/web/harness.html`, `Gert.Api` `FakeE2E` launch profile.
- **Design:** [testing §4.2/§4.3/§8/§9](testing.md#42-two-ways-to-fake-the-outside-world) and **[Appendix A](testing.md#appendix-a--the-shared-fake-spec)** — the mocks read the *same* `tests/shared/fixtures.json` and implement the *same* A.2 embedding algorithm as the .NET fakes.
- **Acceptance:** matrix `{chromium,firefox}×{admin,user}` green driving **real adapters → mock upstreams**; **RBAC + IDOR + entitlement** scenario (admin sees `/admin/users`; user 403 + no cross-user doc; **`limited`** role has Search/Sandbox dropped even when requested — [auth](auth.md#enforcement--the-claim-is-the-ceiling)); an **SSRF E2E** (mock SearXNG returns a private-IP URL → fetch refused, [F5](security.md#3-findings--remediations)); a **Python conformance test asserts `embed(t)` matches `embeddings_golden.json`** (so it can't drift from `FakeEmbeddings`); tooling logs in the shared NDJSON format; dev key generated, never committed.

### U14 — Release pipeline & ops
- **Goal:** NUglify **minify-in-place** MSBuild target on `publish`; `GET /healthz`; **Serilog → shared NDJSON logging** (`ts`/`level`-first, identity by hash only — [operations § Logging format](operations.md#logging-format-shared)); `appsettings.json` non-secret defaults; backup/VACUUM note; HSTS/reverse-proxy assumption.
- **Depends:** U12, U9b
- **Touches:** `Gert.Api.csproj`, `tools/Gert.Web.Minify`, `Gert.Api/Program.cs`, `appsettings.json`.
- **Design:** [ui-components §6](ui-components.md#6-devrelease-pipeline-no-npm), [operations](operations.md#cross-cutting-concerns).
- **Hardens:** F8/F9 (config + TLS), logging hygiene.
- **Acceptance:** `dotnet publish -c Release` emits minified `wwwroot` whose ESM graph still resolves; healthz checks vLLM+SearXNG reachability; minifier spike validated against actual ESM ([ui-components §8](ui-components.md#8-decisions--open-choices)).

### U15 — CI
- **Goal:** Two gating jobs: `dotnet test` (unit+DB+API+console) and the web job (boot `mocks/` + `--launch-profile FakeE2E`, then `python tools/smoke/run.py --browser all --role all`), Playwright traces on failure.
- **Depends:** U13, U14
- **Touches:** CI workflow.
- **Design:** [testing §11](testing.md#11-ci).
- **Acceptance:** both jobs gate merges; hermetic (no network/GPU) for the .NET job.

---

## Parallelization map

```
U0 ─ U1 ─ U2 ─┬─ U3 ───────────────┐
              ├─ U4a ─ U4b          │   (U4b also needs U3)
              └─ U8                 │
                                    │
  M1 slice:  U4a→U5→U7a ─┐         │
             U8 ─────────┼─ U9a  ◀── M1 gate
             U3 ─────────┘
                                    
  After M1, fan out in parallel:
     ├ U6   (validation)        depends U2,U3
     ├ U4b  (RAG repo)          depends U4a,U3
     ├ U7c  (tools)             depends U4b,U2
     ├ U7d  (ingestion)         depends U4b
     └ U7b  (orchestrator)      depends U7a,U7c
  Then U9b (depends U4b,U6,U7b,U7d,U9a)  ◀── M2 gate
  Then U10 ; U11 (depends U10) ; U12 (depends U9b)   ◀── M3
  Then U13,U14 → U15            ◀── M4
```

**Safe concurrent tracks once U2/U3 exist:** persistence (U4a→U4b→U5), auth (U8), and validation
(U6) touch disjoint projects — run them in separate worktrees. Within Phase 2, U7c/U7d/U6 are
independent; U7b joins them. Use `isolation: worktree` for any agents editing in parallel.

---

## Security control → unit traceability

Every [security](security.md#3-findings--remediations) finding has an owning unit, so none can be
silently dropped:

| Finding | Unit(s) |
|---------|---------|
| F1 CSP & headers | U9b |
| F2 token in-memory | U12 |
| F3 sandboxed HTML/SVG artifacts | U12 |
| F4 markdown sanitization | U12 |
| F5 SSRF guard + sandbox egress-off | U10 · U13 (E2E via mock SearXNG) |
| F6 admin `{key}` + `pid` validation | U6 (rule) · U9b (route) |
| F7 parser subprocess isolation | U10 |
| F8 secrets handling | U0 (gitignore) · U10/U14 (sourcing) |
| F9 TLS/HSTS | U14 |
| F10 rate limits | U9b |
| F11 JWT alg pin | U8 |
| F12 folder-root anti-reuse + validate-before-disk | U5 |

---

## Open items to confirm before or during build

These are doc gaps the design set doesn't yet pin — surface, don't guess:

1. ✅ **Resolved — external-world clients live in `Gert.External`.** A dedicated adapter project
   (parallel to `Gert.Database.Sqlite` / `Gert.Authentication`) implements the U2 ports; both hosts
   reference it; `AddGertExternal` registers them. For tests, the in-process .NET fakes serve the
   `dotnet test` tiers and **Python mock upstreams** (`tools/smoke/mocks`) serve the browser E2E
   against the real adapters ([tech-stack](tech-stack.md#solution-layout-projects),
   [testing §4.2](testing.md#42-two-ways-to-fake-the-outside-world)). *(Built in U10 / U13.)*
2. ✅ **Resolved — no identity-binding check** (U5): the per-request `meta.json` `(iss,sub)`
   re-check was dropped — it could only ever fire on a sha256 collision or local tampering, and a
   recycled `sub` (identical `(iss,sub)`) would pass it anyway. The validated JWT is trusted past
   the provisioning gate; `meta.json` is a descriptive sidecar, healed when unreadable.
3. **`gert_tools` claim placement — a prod deploy-time check, not a design gap** (U8): "claim
   source" = *which token* carries `gert_tools`. The API reads the **access token**; tests fully
   control this because Python mints the token, so test coverage can't surface a mismatch. The only
   residual is verifying the **real Pocket ID** emits `gert_tools` *in the access token*; if a build
   only places it in the ID token/userinfo, enable the one-time userinfo fallback already noted in
   [auth](auth.md#the-gert_tools-claim). Verify at deploy, not at build.
4. **Per-conversation param overrides, custom theming, auto-memory, export format** — the
   [configuration §9](configuration.md#9-open-decisions) open decisions; ship the recommended
   defaults unless told otherwise.
5. **Sandbox/parser isolation lever on the target host** (U10): gVisor `runsc` vs. plain
   `seccomp`+`rlimits` depends on the deployment box — confirm availability.

---

## What "done" looks like

All four milestones green: the .NET suite (unit + real-SQLite + API integration + console) and the
Python E2E matrix both gate CI; a `Release` publish ships a minified, import-map SPA; and each
F1–F12 control has a passing test behind it. At that point the design set in this folder is fully
realized and the system matches [principle #1](principles.md) — the IdP owns identity, the
filesystem owns data, the API owns nothing persistent of its own.
