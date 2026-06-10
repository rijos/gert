# Tech stack

| Layer | Choice | Why |
|-------|--------|-----|
| Framework | **ASP.NET Core 10** (.NET 10 LTS) Web API + controllers | Current LTS (Nov 2025), C# 14. MVC-style controllers as specified. |
| Hosts | **Gert.Api** (HTTP) + **Gert.Console** (CLI) over a shared **Gert.Service** | Console bypasses the API and calls services directly (single user, all tools) — clean separation and isolated testing. |
| Static SPA hosting | ASP.NET Core static files (`UseDefaultFiles` + `UseStaticFiles`) + `MapFallbackToFile("index.html")` | Serves the built SPA bundle from the same app/origin as the API — no separate host, no CORS. The fallback routes client-side paths to `index.html` while leaving `/api/*` and `/healthz` to the API. |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` | OIDC JWT validation against Pocket ID JWKS; maps claims to `IUserContext`. |
| Validation | **FluentValidation** (`IValidator<T>`) behind `IValidationProvider` | Validators run in the service layer, so the Console path is validated identically to the API. |
| SQLite | `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` | Extension loading for sqlite-vec; WAL. |
| Vector | **sqlite-vec** (`vec0`) + **FTS5** | Per-user KNN + lexical for hybrid search. |
| Data access | **Dapper** (raw SQL) behind `IUserRepository` / `IChatRepository` / `IRagRepository`, opened via the per-database providers (`IUserDatabaseProvider` / `IChatDatabaseProvider` / `IRagDatabaseProvider`) — contracts in `Gert.Database` | Hand-written SQL suits `vec0`/FTS virtual tables. The **repository interfaces are the engine-portability seam** — the service layer never sees SQL, so another engine drops in as a new project (see [Engine portability](#engine-portability)). |
| Model API | OpenAI-compatible client → **vLLM** | Streaming + function calling out of the box. Lives in **`Gert.External`** behind `IChatModelClient`/`IEmbeddingClient`. |
| HTTP | `IHttpClientFactory` + **Polly** | vLLM / SearXNG calls with resilience (in `Gert.External`); every pipeline is configured **from the bound options**, never stock defaults. The streaming chat client has an **infinite** `HttpClient.Timeout` — the turn budget owns the stream ([turn-budgets](turn-budgets.md)) and Polly bounds only the pre-stream (time-to-headers) phase, so a connect/accept retry never repeats streamed tokens; buffered calls (embeddings, search) keep a finite client timeout sitting just outside their Polly total. The SearXNG fetch is SSRF-guarded ([security F5](security.md#3-findings--remediations)). |
| Background work | `System.Threading.Channels` + `BackgroundService` (Gert.Api) | Ingestion queue — an **Api hosting** concern wrapping `IIngestionService`; the Console ingests inline. |
| Logging | **Serilog** → JSON lines (NDJSON) on stdout | `ts`/`level`-first schema **shared with the Python tooling** so one parser reads every process; never logs tokens/`sub`/content ([operations § Logging format](operations.md#logging-format-shared)). |
| Extraction | **PdfPig** (PDF), **OpenXML** (DOCX) | Text extraction for chunking. Parses **untrusted** bytes, so it runs in an **isolated, unprivileged subprocess** (dropped privs, no net, `RLIMIT_AS`/`CPU`/`NPROC` + timeout) with DTD/external-entity **off** (XXE) and decompressed-size/zip-entry caps (bombs) — may reuse gVisor ([security F7](security.md#3-findings--remediations)). |
| Sandbox | **monty** (Rust Python) *default* · **gVisor (`runsc`)** | Isolated `run_python` behind one `ISandbox` port, operator-picked (`Gert:Sandbox:Backend`): monty has **no syscalls** and runs in an unprivileged sidecar (no container infra); gVisor runs real CPython in an ephemeral container. **Egress off** either way; no `/data` mount. |

## Architecture

The codebase is a **host-agnostic service layer** with two hosts on top of it:

- **`Gert.Service`** holds all business logic and references nothing host-specific — no `HttpContext`, no JWT, no SSE. It depends only on abstractions: `IUserContext` (who is the current user + their tool entitlement), the persistence contracts in **`Gert.Database`** (the per-database providers `IUserDatabaseProvider` / `IChatDatabaseProvider` / `IRagDatabaseProvider` and their repositories — this user's connections + migrations), and the tool/validation interfaces.
- **`Gert.Api`** drives the services over HTTP (controllers, JWT auth, SSE, the ingestion `BackgroundService`, SPA hosting).
- **`Gert.Console`** drives the *same* services directly — a single fixed user (`LocalUserContext`, tools = `"*"`), ingestion run inline. Bypasses the entire API/controller layer; ideal for isolated testing and admin one-offs.

Because the service layer can't see the hosts, the "Console must not need the API" guarantee is **structural** (compiler-enforced reference direction), not a convention. Services that stream — chat — return `IAsyncEnumerable<ChatEvent>`; the Api renders those as SSE, the Console prints them. Transport never leaks into the service.

Services are exposed two ways: **granular interfaces** (`IChatService`, `IConversationService`, `IDocumentService`, …) for normal DI, plus an aggregate **`IGertServices`** hub that surfaces them as properties (`services.Chat`, `services.Documents`) for the Console and for cross-service orchestration. Controllers inject the one granular service they need; only the Console leans on the hub.

### Project dependency tree

Arrows are project references. Everything points inward to `Gert.Service` → `Gert.Model`; nothing the service layer touches depends on a host, which is what lets the Console drive the services without the API.

```
  ── hosts ───────────────────────────────────────────────────────────────────
     Gert.Api                                          Gert.Console
       refs: Service, Authentication,                    refs: Service, Database.Sqlite,
             Database.Sqlite, External                          External
       │                                                 │   (own LocalUserContext —
       │                                                 │    no Authentication ref)
       ▼                                                 ▼
  ── adapters ────────────────────────────────────────────────────────────────
     Gert.Authentication   Gert.Database.Sqlite   Gert.External         Gert.Database.Postgres
     (JWT → IUserContext)   (vec0 + FTS5)          (vLLM · SearXNG ·     (future: pgvector)
                                                    gVisor sandbox)
            └──────────────────────┴──────────────────────┴────────────────────┘
                                    │  all ref ▼
  ── core ────────────────────────────────────────────────────────────────────
                            Gert.Service                refs: Model + Database (contracts)
                                    │
                                    ▼
  ── contracts / model ───────────────────────────────────────────────────────
              Gert.Database (persistence contracts)      refs: Model only
                            Gert.Model                   no dependencies
```

The compiler enforces the inward direction: `Gert.Service` cannot reference `Gert.Api`, `Gert.Authentication`, `Gert.External`, or any `Gert.Database.*` **adapter** (`Gert.Database` itself is the engine-neutral contracts kernel — providers, repositories, the gate refusal — and is the one persistence reference the service layer holds) — so "the Console must not need the API" is structural, not a convention. **`Gert.External`** is the outside-world seam, exactly parallel to the database seam: the service layer talks only to the ports (`IChatModelClient`, `IEmbeddingClient`, `IWebSearch`, `ISandbox`), and the real vLLM/SearXNG/gVisor clients live behind them — so they can be swapped (or pointed at mock upstreams for tests, see [testing](testing.md#41-the-fake-external-world)) with a single DI change.

## Engine portability

The persistence seam is the **repository interfaces** (`IChatRepository`, `IRagRepository`), not a generic connection wrapper — because the RAG SQL is engine-specific and cannot be abstracted into shared SQL. The service layer talks only to those interfaces, so swapping engines means adding a new `Gert.Database.*` project and changing one DI registration; `Gert.Service` is untouched.

Persistence is split along the storage/database line. **`Gert.Storage`** is the storage-backend layer: `IObjectStore` is the seam for the genuine blobs under a user tree — uploads and memory bodies — plus the coarse scope lifecycle (`DeleteScopeAsync` = the `rm -rf`) and the admin footprint listing, with `LocalObjectStore` (local FS) today and an S3/Azure-Blob backend as a sibling `Gert.Storage.*` project + one DI swap tomorrow. `ObjectStoreUserStore` implements `IUserStore` purely over that seam, so it never changes when the backend does. (Structured user state — username, settings, the project registry — is **not** blob territory: it lives in `user.db`, [decisions §9](decisions.md#9-userdb--structured-user-state-is-a-database-not-json-sidecars).) **`Gert.Database`** is the engine-neutral contracts kernel: the per-database providers (`IUserDatabaseProvider` / `IChatDatabaseProvider` / `IRagDatabaseProvider`), the repository interfaces, and the provisioning-gate refusal (`UnauthorizedDatabaseIdentityException`); `SqliteDatabasePaths` (local db-file paths) lives in `Gert.Database.Sqlite` because only a file-backed engine has paths at all (Postgres has a connection string), and key derivation lives in `Gert.Service.Storage.StorageKeys` (core policy, not adapter detail). Database files themselves (`user.db`/`chat.db`/`rag.db`) are *not* objects — engines need real local file handles — so they stay behind the providers, and a remote object backend paired with SQLite is a split deployment (objects remote, dbs local); the full remote-storage payoff arrives with a server database. A future Postgres implementation (`Gert.Database.Postgres`) is therefore a **new project with its own SQL** reusing those shared layers, not a config flag:

- `vec0 … MATCH … ORDER BY distance` → **pgvector** `<=>` / `<->` with an HNSW index; `FLOAT[1024]` → `vector(1024)`.
- FTS5 `bm25()` → `tsvector` + `ts_rank_cd` (no native BM25 without `pg_search`/ParadeDB or `rum`), so the lexical rank and the RRF fusion are re-tuned.

**Tenancy mapping: schema-per-user.** Postgres binds a connection to one database, so *database-per-user* fragments the connection pool and bloats the catalog at scale. The faithful analog of our per-user model is **one schema per user** in a single database: it keeps structural isolation, pools cleanly (`SET search_path` per request), and preserves the one-command delete — `DROP SCHEMA "{key}" CASCADE` is the Postgres `rm -rf`. (Shared-tables + RLS would scale further but is a **non-goal**: it makes isolation a query filter, contradicting [principle #2](principles.md), and turns user deletion into a filtered `DELETE`.) At ~20 users none of this bites; the mapping matters only if Gert ever grows well beyond that.

## Solution layout (projects)

```
Gert.sln
│
├─ Gert.Model/                # POCOs only, no deps — Conversation, Message, ToolCall,
│                             #   Citation, Artifact, Document, Chunk, ChatEvent, DTOs
│
├─ Gert.Service/              # host-agnostic business logic — references Model + Database (contracts)
│  ├─ IGertServices.cs        # aggregate hub: .Chat .Conversations .Documents .Artifacts .Admin
│  ├─ IUserContext.cs         # current user's scope: Sub, AllowedTools (abstraction only)
│  ├─ Chat/                   # the detached turn pipeline: TurnPlanner, TurnRunner, queue, bus,
│  │                          #   ConversationStreamer, MessageStatusRules, SystemPrompts
│  ├─ Conversations/          # IConversationService
│  ├─ Documents/              # IDocumentService
│  ├─ Ingestion/              # IIngestionService.Ingest(doc) — pure pipeline (extract→chunk→embed→write)
│  ├─ Provisioning/           # UserProvisioner — username refresh + default-project seed (user.db)
│  ├─ Tools/                  # ITool + ToolRegistry + ITailReminder; rag/search/sandbox/todo/clock
│  │                          #   + the canvas suite (make/edit/read artifact)
│  └─ Validation/             # IValidationProvider + FluentValidation validators per model
│
├─ Gert.Storage/              # THE storage-backend layer (local today; S3/Azure = sibling project)
│  ├─ LocalObjectStore.cs     #   IObjectStore local backend — atomic PUTs under {DataRoot}/users
│  └─ ObjectStoreUserStore.cs #   IUserStore over IObjectStore — blob lifecycle + admin footprint scan
│
├─ Gert.Database/             # engine-neutral persistence contracts (SQLite today, Postgres tomorrow)
│  ├─ IUserDatabaseProvider.cs / IChatDatabaseProvider.cs / IRagDatabaseProvider.cs
│  ├─ IUserRepository.cs / IChatRepository.cs / IRagRepository.cs
│  └─ UnauthorizedDatabaseIdentityException.cs  # the fail-closed provisioning-gate refusal
│
├─ Gert.Database.Sqlite/      # SQLite impl — references Database, Service, Model (NOT Storage)
│  ├─ SqliteUserDatabaseProvider.cs  # opens THIS user's user.db (self-migrating)
│  ├─ SqliteChatDatabaseProvider.cs  # opens a project's chat.db (WAL, busy_timeout)
│  ├─ SqliteRagDatabaseProvider.cs   # opens a project's rag.db (vec0 loaded)
│  ├─ SqliteDatabasePaths.cs         # LOCAL db-file paths — sqlite-only; Postgres has a connection string
│  ├─ SqliteUserRepository.cs        # user_meta + settings + project registry (Dapper)
│  ├─ SqliteChatRepository.cs        # Dapper
│  ├─ SqliteRagRepository.cs         # Dapper + sqlite-vec/FTS5
│  ├─ SqliteMigrationRunner.cs       # PRAGMA user_version, per database
│  ├─ SqliteHandleReleaser.cs        # IDatabaseHandleReleaser — drop pooled handles before local deletes
│  └─ Migrations/
│     ├─ user/001_init.sql
│     ├─ chat/001_init.sql … 004_attachments.sql
│     └─ rag/001_init.sql
│
├─ Gert.Database.Postgres/    # (future — not yet in the repo) pgvector + tsvector, schema-per-user — same interfaces
│  ├─ PgDatabaseProvider.cs        # schema-per-user; DROP SCHEMA CASCADE = delete user
│  ├─ PgChatRepository.cs
│  ├─ PgRagRepository.cs           # pgvector (<=>) + tsvector/ts_rank_cd
│  └─ Migrations/
│
├─ Gert.Authentication/       # JWT implementation of IUserContext — references Service (+ ASP.NET)
│  ├─ HttpUserContext.cs      # maps JWT claims (sub, groups, gert_tools) → IUserContext
│  ├─ JwtBearer.cs            # JWKS/Authority config, NameClaimType/RoleClaimType
│  └─ Policies.cs             # Admin policy, fallback authenticated-user policy
│
├─ Gert.External/             # outside-world adapters — references Service, Model
│  ├─ Vllm/                   #   IChatModelClient + IEmbeddingClient — OpenAI-compatible (IHttpClientFactory + Polly)
│  ├─ Search/                 #   IWebSearch — SearXNG client + SSRF-guarded fetch (security F5)
│  ├─ Sandbox/                #   ISandbox — monty sidecar (default) or gVisor (runsc); Gert:Sandbox:Backend picks
│  ├─ Isolation/             #   IIsolatedExtractor — unprivileged subprocess for PDF/DOCX parsing (security F7)
│  └─ ServiceCollectionExtensions.cs  # AddGertExternal(cfg): one registration; swap any provider in isolation
│
├─ Gert.Api/                  # HTTP host — references Service, Authentication, Database.Sqlite, External
│  ├─ Program.cs              # DI, JwtBearer, static files + SPA fallback, SSE, BackgroundService
│  ├─ appsettings.json        # NON-secret defaults only: vLLM/SearXNG URLs, embedding dim, DataRoot,
│  │                          #   Auth. Keys/secrets come from env / user-secrets / a secret store
│  │                          #   — never committed (security F8). (No tool-grant config: the JWT
│  │                          #   gert_tools claim is the sole source — auth.md § tool entitlements.)
│  ├─ Controllers/            # thin — Models, Conversations, Messages(SSE), Documents, Artifacts, Admin
│  ├─ Ingestion/              # Channel queue + IngestionWorker (BackgroundService) → IIngestionService
│  └─ wwwroot/                # VanJS SPA source (no .NET ref, no npm) — native ES modules served
│                             #   raw in dev, minified in place on publish (NUglify).
│                             #   Layout & component conventions: docs/design/ui-components.md
│
├─ Gert.Console/              # CLI host — references Service, Database.Sqlite, External (bypasses the Api)
│  ├─ Program.cs              # wires LocalUserContext (single user, tools = "*"), inline ingestion
│  └─ …                       # renders the ChatEvent stream to stdout; isolated-testing entry point
│
├─ tests/                     # test projects — see docs/design/testing.md
│  ├─ Gert.Testing/           #   shared infra: fakes (vLLM/SearXNG/sandbox), GertApiFactory, JWT mint
│  ├─ Gert.Service.Tests/     #   whitebox: tool loop, ingestion, tools, validation
│  ├─ Gert.Database.Sqlite.Tests/ # repositories vs real temp SQLite (vec0 + FTS5); isolation
│  ├─ Gert.Authentication.Tests/  # JWT claims → IUserContext; sub→key; RS256 pin
│  ├─ Gert.External.Tests/    #   adapter units: SSRF guard, sandbox args, extractor hardening, Polly
│  ├─ Gert.Api.Tests/         #   integration (WebApplicationFactory): SSE, auth, IDOR, admin, SPA fallback
│  ├─ Gert.Console.Tests/     #   drive the Console host with fakes; assert rendered stream
│  ├─ Gert.Web.Minify.Tests/  #   the publish-time minifier stays ESM-safe
│  ├─ shared/                 #   ONE source of truth for both fake layers (testing.md Appendix A)
│  └─ web/                    #   harness.html — browser component-unit mount point
│
└─ tools/
   ├─ Gert.Web.Minify/        # NUglify minify-in-place console, run on publish (ui-components §6)
   └─ smoke/                  # Python + Playwright E2E launcher (no npm) — admin+user × Chromium+Firefox
```
