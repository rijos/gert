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
| U4a | SQLite provider + chat repo | ⬜ | |
| U4b | RAG repo (vec0+FTS5+RRF) | ⬜ | |
| U5 | Paths, provisioning gate, isolation | ⬜ | F12 |
| U6 | Validation layer | ⬜ | F6, principle #6 |
| U7a | CRUD + minimal ChatService | ⬜ | M1 slice |
| U7b | Full tool-loop orchestrator | ⬜ | |
| U7c | Tools (rag/search/sandbox) | ⬜ | |
| U7d | Ingestion pipeline | ⬜ | |
| U8 | Gert.Authentication | ⬜ | F11 |
| U9a | API walking skeleton | ⬜ | **M1 gate** |
| U9b | API breadth + RBAC/IDOR + headers | ⬜ | F1,F6,F10,F9 |
| U10 | Gert.External real adapters | ⬜ | F5,F7,F8 |
| U11 | Gert.Console | ⬜ | |
| U12 | Gert.Web SPA | ⬜ | F2,F3,F4 |
| U13 | Python smoke/E2E + mocks | ⬜ | |
| U14 | Release pipeline + ops | ⬜ | logging, NUglify |
| U15 | CI | ⬜ | |

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
