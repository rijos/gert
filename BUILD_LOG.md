# Build log

Orchestrated execution of [docs/design/implementation-plan.md](docs/design/implementation-plan.md)
on branch `feat/gert-build`. Toolchain: .NET 10.0.103 · uv 0.9.27 · Python 3.13.

Status: ⬜ not started · 🟡 in progress · ✅ done · 🔴 blocked

| Unit | Title | Status | Notes |
|------|-------|:------:|-------|
| U0 | Solution & project skeleton | ✅ | build clean (0 warn), 6/6 test projects pass, arch test enforces ref direction |
| U1 | Gert.Model | ✅ | POCOs (schema-mirrored), polymorphic ChatEvent (STJ), DTOs |
| U2 | Service seams (interfaces) | ✅ | granular+aggregate services, repo/validation/tool + External ports; arch test re-anchored on IUserContext |
| U3 | Gert.Testing + shared fake spec | ✅ | fakes + fixtures.json (SSRF entry) + 3-role TestTokens + NaughtyStrings; golden generated, conformance green (6 vectors) |
| U4a | SQLite provider + chat repo | ✅ | provider (WAL/pragmas), Dapper chat repo, migration runner; 27 real-SQLite tests green |
| U4b | RAG repo (vec0+FTS5+RRF) | ⬜ | needs native sqlite-vec; rag.db deferred from M1 |
| U5 | Paths, provisioning gate, isolation | ✅ | F12: sha256(iss+sub), validate-before-disk, meta.json identity binding, two-user isolation, pid-traversal guard |
| U6 | Validation layer | ⬜ | F6, principle #6 |
| U7a | CRUD + minimal ChatService | ✅ | ConversationService CRUD + no-tool streaming ChatService + GertServices hub + passthrough validation (TODO U6); 23 service tests. Document/Memory/Project/Settings/Account/Admin stubbed (TODO U4b/U7c/U7d) |
| U7b | Full tool-loop orchestrator | ⬜ | |
| U7c | Tools (rag/search/sandbox) | ⬜ | |
| U7d | Ingestion pipeline | ⬜ | |
| U8 | Gert.Authentication | ✅ | F11: HttpUserContext (3-role claim mapping), RS256-pinned JwtBearer, Admin/fallback policies, sub-denylist; 19 tests |
| U9a | API walking skeleton | ✅ | **M1 GATE GREEN** — Program/controllers/SSE + GertApiFactory (offline JWKS, temp DataRoot, fakes); 6 gate tests: 401, healthz, lazy-provision, CRUD, SSE happy path, SPA fallback |
| U9b | API breadth + RBAC/IDOR + headers | ⬜ | F1,F6,F10,F9 |
| U10 | Gert.External real adapters | ⬜ | F5,F7,F8 |
| U11 | Gert.Console | ⬜ | |
| U12 | Gert.Web SPA | ⬜ | F2,F3,F4 |
| U13 | Python smoke/E2E + mocks | ⬜ | |
| U14 | Release pipeline + ops | ⬜ | logging, NUglify |
| U15 | CI | ⬜ | |

## M1.5 — review pass (user feedback before M2)
Decisions: storage = **interface seam + LocalFS only** (no S3 yet); test pyramid = **lean on Python**
(drop .NET fakes/minting — but DEFERRED until U10 real adapters + U13 python E2E exist, else coverage
hole; production .NET already verified fake-free); ChatService = **step-based redesign, stateless**
(StartTurn prep + RunTurn stream in ONE request, no turnId/cross-request state — the turnId/GetEvents
shape would break multi-instance #10).

Order (semantic first, file-split enforcement last):
1. ✅ Drop ISubDenylist (#10) — stateless revocation (expiry + IdP deactivation). Code done; docs pending in this commit.
2. ✅ Generic tool toggles (#1: ToolToggles=dict map, ToolKind deleted, id strings) + canonical gert_tools (#9: dropped JSON branch) + ChatEventType enum (#2: discriminator renamed $type to avoid collision)
3. ✅ IObjectStore seam + LocalObjectStore (#3, traversal-guarded); GetThread ordering audited (already ordered) + test (#8); Dapper MatchNamesWithUnderscores + property-record row DTOs — zero casts (#7); ThrowingChatModel reachable-yield (#12). 83 tests.
4. ✅ ChatService step-based stateless redesign (#13: StartTurnAsync→ChatTurn→RunAsync, no turnId; invalid input throws ValidationException→400 before stream) + branded Gert ProblemDetails 400/401/403/404 (#15). 83 tests.
5. ⬜ Config: nullable→error explicit (#4); coverage (coverlet+ReportGenerator) (#5); Makefile test/run/coverage/e2e (#6)
6. ⬜ Enforce one-type-per-file via StyleCop SA1402/SA1649 as error (#11) — LAST, then split files
7. ⬜ DEFERRED to U10+U13 boundary: remove .NET fakes/minting + .NET HTTP test tier → Python E2E (#14)
8. ⬜ Re-green full suite; resume M2 (U4b RAG → U6 → U7b-d → U9b)

## Milestones
- **M0** skeleton compiles — U0–U3
- **M1** walking skeleton — U4a,U5,U7a,U8,U9a
- **M2** feature-complete API — U4b,U6,U7b–d,U9b
- **M3** hosts + SPA — U10,U11,U12
- **M4** hardened + E2E gating — U13,U14,U15

## Process note
Background agents run **sandboxed**: no toolchain (`dotnet`/`uv`) and no network (NuGet). So the
model is **agents author code; the orchestrator (main session) runs `dotnet build`/`test`/restore**
with the sandbox disabled, iterates against compiler output, and checkpoint-commits each unit.

## Activity
- Design set committed (5a189b1). Branch `feat/gert-build` off `master`.
- U0 ✅ — skeleton hand-authored by agent; orchestrator verified `dotnet build` (0 warn/0 err) + `dotnet test` (6/6 projects pass, incl. arch test).
- U1+U2 ✅ — Model + Service seams authored by agent; orchestrator fixed one missing `using Gert.Model;` (DocumentKind), rebuilt clean, arch test green. Accepted agent defaults: added `IAccountService`, instance `ToolRegistry`, unified `ToolResultHit`, stream-`Func` upload/export seams — refinable when consumers land.
- U3 ✅ — **M0 COMPLETE.** Test infra authored by agent; orchestrator: fixed span-across-`yield` in FakeChatModel echo tokenizer; pinned packages (Microsoft.Data Identity 8.x, AspNetCore.Mvc.Testing 10.0) — restored clean; generated `embeddings_golden.json` from the real `FakeEmbeddings.Embed` via a .NET-10 file-based app (Utf8JsonWriter, bit-exact). Conformance theory green on all 6 vectors. Packages confirmed valid: IdentityModel 8.3.0, Mvc.Testing 10.0.0.
- Process: golden generation needs the compiled fake → orchestrator-only step (agents can't run code). Documented for future regen.
- U4a+U5 ✅ — SQLite storage core authored by agent; orchestrator fixes: (1) suppressed xUnit1051 suite-wide in tests/Directory.Build.props (responsiveness nicety, not correctness); (2) Dapper Int64 binding — widened MessageRow.token_count / CitationRow.ordinal / ArtifactRow.version to `long` (SQLite returns Int64) and cast to model `int` at mappers. 27/27 storage tests green, 38 total. Pinned: Microsoft.Data.Sqlite 9.0.0, Dapper 2.1.66, Microsoft.Extensions.Options 9.0.0, FluentAssertions 7.0.0 (free). rag.db/vec0 deferred to U4b (TODOs in SqliteRagRepository/OpenRagAsync/EnsureProjectAsync).
- U8 ✅ — Gert.Authentication authored by agent; built clean first try (JwtBearer 10.0.0, NSubstitute 5.3.0 valid). 19 auth tests green, 56 total. ToolOptions(DefaultGrant) added in Gert.Service/Tools. Denylist + RS256-pin tested via extracted pure statics (no server needed). Note: GertApiFactory still has the U9a TODO (TestTokens JWKS + TempDataRoot wiring).
- U7a ✅ — services slice; orchestrator fixed an `is`-pattern-in-expression-tree (FA NotContain → Any). 23 service tests, 71 total. DI.Abstractions 9.0.0 pinned.
- U9a ✅ — **M1 COMPLETE.** API skeleton authored by agent; orchestrator fixes: (1) `MetadataAddress=null` nullable error → dropped (Authority=null suffices); (2) missing `using Gert.Model;` in test; (3) **the real integration bug — JwtBearer `MapInboundClaims` was renaming `sub`→WS-* URI so HttpUserContext threw "no sub claim"; set `MapInboundClaims=false` in AddGertJwtAuth.** All 6 gate tests green; **76 total**. (U8's unit tests built principals with raw claim names so couldn't catch the inbound-mapping rename — the walking skeleton did, as intended.)
