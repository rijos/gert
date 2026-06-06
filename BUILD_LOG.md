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
| U4b | RAG repo (vec0+FTS5+RRF) | ✅ | real vec0 KNN + FTS5 bm25 + RRF (k=60); packed float32 blobs; memory rides same query; cascade delete; project isolation; FTS-injection guard. rag.db un-deferred. 46 DB tests |
| U5 | Paths, provisioning gate, isolation | ✅ | F12: sha256(iss+sub), validate-before-disk, meta.json identity binding, two-user isolation, pid-traversal guard |
| U6 | Validation layer | ✅ | fail-closed FluentValidationProvider + per-DTO validators (threat-model rules) + reflection meta-test + NaughtyStrings theories; F6 route-param validators (admin {key}, pid). 344 service tests, 413 total |
| U7a | CRUD + minimal ChatService | ✅ | ConversationService CRUD + no-tool streaming ChatService + GertServices hub + passthrough validation (TODO U6); 23 service tests. Document/Memory/Project/Settings/Account/Admin stubbed (TODO U4b/U7c/U7d) |
| U7b | Full tool-loop orchestrator | ✅ | step-0 instructions prepend + tool loop in RunAsync (offered = requested∩conv∩entitlement∩registry; ToolCall→exec→ToolResult→feed-back; round cap 5; citations); stateless. Pinned-memory retrieval TODO |
| U7c | Tools (rag/search/sandbox) | ✅ | RagTool (embed→HybridSearch→citations), WebSearchTool, SandboxTool (3 failure shapes); DI-registered; entitlement re-checked at exec. 12 new tests, 425 total |
| U7d | Ingestion pipeline | ✅ | IngestionService (extract→chunk[256/32]→embed[batch16]→write; no-text→failed) via ITextExtractor (md/txt; pdf/docx→U10) + IIngestionQueue (inline; Channel→U9b); DocumentService/MemoryService all blob I/O via IObjectStore, base64 filename; validator relaxed. 440 tests |
| U8 | Gert.Authentication | ✅ | F11: HttpUserContext (3-role claim mapping), RS256-pinned JwtBearer, Admin/fallback policies, sub-denylist; 19 tests |
| U9a | API walking skeleton | ✅ | **M1 GATE GREEN** — Program/controllers/SSE + GertApiFactory (offline JWKS, temp DataRoot, fakes); 6 gate tests: 401, healthz, lazy-provision, CRUD, SSE happy path, SPA fallback |
| U9b | API breadth + RBAC/IDOR + headers | ✅ | all endpoints (settings/projects/documents/memory/artifacts/account/admin) + {pid}/{key} validation; CSP+headers (F1), HSTS (F9), per-user rate limit (F10); Channel ingestion BackgroundService; Project/Settings/Account/Admin services + IUserStore port. IDOR/pid-tamper/admin-key-traversal/RBAC/headers tests. 481 total |
| U10 | Gert.External real adapters | ✅ | vLLM chat(SSE)/embeddings (Polly), SearXNG + SSRF guard (F5: scheme+private-IP block, ConnectCallback, redirect re-vet), gVisor sandbox (egress-off F5), isolated pdf/docx extractor (F7: RLIMIT+XXE+zip-bomb), AddGertExternal (secrets F8). 94 unit tests (security controls); live wire=U13/staging. 575 total |
| U11 | Gert.Console | ✅ | LocalUserContext (single user, all tools), AddGertConsole wiring (inline ingest, no auth/controllers/worker), ChatEvent→stdout renderer, chat/ingest commands. NO Gert.Authentication ref (asserted). 12 tests, 586 total |
| U12 | Gert.Web SPA | ✅ | full VanJS SPA in Gert.Api/wwwroot (68 JS files, all parse; styles split from mockup); F2 in-memory token, F3 sandboxed html+svg iframe (no allow-same-origin), F4 md sanitizer+bidi-isolate; SSE→state→views; van/van-x vendored. Behavioral verification = U13 |
| U13 | Python smoke/E2E + mocks | ✅ | harness + mocks + FakeE2E; ruff+mypy --strict CLEAN; **browser smoke 18/18 GREEN** (chromium+firefox × admin/user/limited) — first real SPA run. Caught+fixed 4 bugs: tracing API, CSP-blocked importmap, fonts-CDN CSP, new-chat FK. Non-gating. embeddings conformance bit-exact |
| U14 | Release pipeline + ops | ✅ | NUglify minify-in-place on publish (ESM-safe, raw-fallback) verified via dotnet publish; Serilog NDJSON (ts/level-first, uid=hash, never tokens/sub/content); /readyz dep-check (/healthz unchanged). 14 tests, 600 total |
| U15 | CI | ✅ | .github/workflows/ci.yml — gating .NET (build+test, vec0 vendored) + gating Python (ruff+mypy+conformance via uv sync/make); GATING browser E2E job (--with-deps, artifact upload). make serve-mock = headed manual view. YAML valid; make targets verified |
| U16 | Console TUI — web-parity + local code editing | ✅ | Terminal.Gui 2.4.4 full-screen app (`gert` / `gert tui` on a TTY): 3-pane shell (sidebar \| chat \| workspace) + status bar w/ context ring equivalent. Streaming transcript (collapsible thinking, tool cards, citations, tok/s), composer (^M model, ^T tools+thinking+auto-apply, ^K knowledge docs+memory, ^S settings incl. per-model params), conversation CRUD + project switch + mid-turn streaming resume (replay-from-seq, mirrors SPA resume()). NO artifact canvas — the right pane is the LOCAL WORKSPACE: console-only file tools (read_file/list_dir/glob/grep + gated write_file/edit_file/shell) confined to launch-CWD (WorkspaceRoot guard); writes gated by an approval dialog showing an in-repo LCS unified diff (deny returns the diff to the model; approve-all toggle); touched files + diffs in the pane. ToolRegistry superset Replace entitles local ids (planner offers = ∩ registry); logs → file sink in TUI mode; DataRoot defaults to ~/.local/share/gert. 93 console tests, 822 total |

## M1.5 — review pass (user feedback before M2)
Decisions: storage = **interface seam + LocalFS only** (no S3 yet); test pyramid = **lean on Python**
(drop .NET fakes/minting — but DEFERRED until U10 real adapters + U13 python E2E exist, else coverage
hole; production .NET already verified fake-free); ChatService = **step-based redesign, stateless**
(StartTurn prep + RunTurn stream in ONE request, no turnId/cross-request state — the turnId/GetEvents
shape would break multi-instance #10).

**Storage seam (confirmed after U4b):** two distinct seams. IRagRepository = SQLite/vec0 query engine (KNN/FTS/RRF — cannot be a blob store). IObjectStore = source files + exports (Local now, S3 later). U7d ingestion/DocumentService MUST route all raw-file read/write/delete through IObjectStore (no direct File.IO). SQLite DB files stay on local FS.

**Filename handling (decided, apply at U7d/U9b/U12):** the upload filename is NEVER a storage path (files stored under server-generated {doc-id}.{ext}), so it is pure display metadata. Preserve the exact original name **base64-encoded** in documents.filename (any exotic byte sequence round-trips); the upload validation gate checks **extension allowlist + size only** (not path-safety). Display is **XSS-safe by construction** — the SPA renders the name via a VanJS text node (`createTextNode`), so no HTML/script parsing and no manual escaping. The ONLY residual is **bidi-override spoofing** (U+202E etc. reorder displayed glyphs — `invoice‮fdp.exe` shows as `invoiceexe.pdf`); handle that at render with `unicode-bidi: isolate` (or strip bidi codepoints) — anti-spoofing, optional, NOT XSS. Caveat: the text-node safety holds only while the filename stays a text node (never `innerHTML`/attribute-concat/`href`). U6's conservative filename gate stays until U7d swaps in this model.

**Known deviation (U7d, reconcile later):** LocalObjectStore roots every key under `files/`, so memory bodies land at `projects/{pid}/files/memory/{id}.md` rather than the design.s sibling `projects/{pid}/memory/`. Functionally isolated + correct; to match storage-and-data.md, give IObjectStore a memory scope (or update the doc). Low priority.

Order (semantic first, file-split enforcement last):
1. ✅ Drop ISubDenylist (#10) — stateless revocation (expiry + IdP deactivation). Code done; docs pending in this commit.
2. ✅ Generic tool toggles (#1: ToolToggles=dict map, ToolKind deleted, id strings) + canonical gert_tools (#9: dropped JSON branch) + ChatEventType enum (#2: discriminator renamed $type to avoid collision)
3. ✅ IObjectStore seam + LocalObjectStore (#3, traversal-guarded); GetThread ordering audited (already ordered) + test (#8); Dapper MatchNamesWithUnderscores + property-record row DTOs — zero casts (#7); ThrowingChatModel reachable-yield (#12). 83 tests.
4. ✅ ChatService step-based stateless redesign (#13: StartTurnAsync→ChatTurn→RunAsync, no turnId; invalid input throws ValidationException→400 before stream) + branded Gert ProblemDetails 400/401/403/404 (#15). 83 tests.
5. ✅ nullable→error explicit (#4); coverage coverlet.collector 6.0.2 + reportgenerator tool, `make coverage` works — 72.6% line/60.2% branch (#5); Makefile (#6).
6. ✅ One-type-per-file enforced via StyleCop SA1402/SA1649 as ERROR (all other SA rules silenced; SA0001 off). Split ~40 files across all projects; nested types exempt. Build judges it. 83 tests.
7. ❌ **#14 NOT done; E2E IS run as a smoke (user decision, revised).** Keep the verified .NET WebApplicationFactory suite + fakes as the gate (no #14 deletion). U13: author the Python E2E harness AND actually **install Playwright browsers + run the full browser smoke** (real adapters → python mocks, FakeE2E host) to prove it works — but it is **non-gating by default** (per spec; the .NET suite gates). First real execution of the SPA + real-adapter-mock stack → expect iteration. U15 CI: .NET gate runs; web/E2E job opt-in.
8. ✅ Re-green full suite done per unit; M2 complete (eb06d1a).

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
- U16 ✅ — **Console TUI.** Built in 12 sub-units, each build+test green. API-risk handled up front: probed the installed Terminal.Gui 2.4.4 assembly via reflection BEFORE writing views — the legacy static `Application` is `[Obsolete]` (use `Application.Create()` → `IApplication` instance: `Init/Run(IRunnable)/Invoke/RequestStop`), custom draw = `OnDrawingContent(DrawContext)` + `Move/AddStr/SetAttribute`, `SetNeedsDraw()`, `TextView` obsolete-but-functional (pragma'd; gui-cs/Editor not worth the dep for a 5-line composer). Key architecture moves: (1) approval gate lives INSIDE each gated tool's ExecuteAsync behind a console-local IToolApprover seam — TurnRunner/Gert.Service untouched; TUI bridges via TaskCompletionSource + IApplication.Invoke, turn-stop cancels an open dialog (CancelledEvent added to the terminal set — CLI's UntilTerminal only broke on end/error). (2) Stop goes through ITurnCancellation.Cancel(TurnKey.From(job)), never the stream CT (that would finalize error, not cancelled). (3) AddGertServices' id-only ToolRegistry is TryAdd'd → AddGertConsoleTui Replaces it with the superset (built-ins ∪ local ids) or the planner silently drops the file tools (wiring test pins this). (4) All transcript/presenter logic is headless (RenderLine projection, scripted ChatEvent tests incl. a fixtures-driven write_file landing on disk through the real planner/runner/streamer). Verified: PTY smoke (launch, Ctrl+Q, zero-arg default, lazy provisioning at ~/.local/share/gert), 822 total tests green.
