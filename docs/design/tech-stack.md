# Tech stack

| Layer | Choice | Why |
|-------|--------|-----|
| Framework | **ASP.NET Core 10** (.NET 10 LTS) Web API + controllers | Current LTS (Nov 2025), C# 14. MVC-style controllers as specified. |
| Host | **Gert.Api** (HTTP) over a host-agnostic **Gert.Service** | All business logic lives in the service layer and never sees `HttpContext`/JWT/SSE: clean separation and isolated testing, with the host kept swappable by design. |
| Static SPA hosting | ASP.NET Core static files (`UseDefaultFiles` + `UseStaticFiles`) + `MapFallbackToFile("index.html")` | Serves the built SPA bundle from the same app/origin as the API - no separate host, no CORS. The fallback routes client-side paths to `index.html` while leaving `/api/*` and `/healthz` to the API. |
| Auth | `Microsoft.AspNetCore.Authentication.JwtBearer` | OIDC JWT validation against Pocket ID JWKS; maps claims to `IUserContext`. |
| Validation | **FluentValidation** (`IValidator<T>`) behind `IValidationProvider` | Validators run in the service layer, so every caller is validated identically, independent of transport. |
| SQLite | `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` | Extension loading for sqlite-vec; WAL. |
| Vector | **sqlite-vec** (`vec0`) + **FTS5** | Per-user KNN + lexical for hybrid search. |
| Data access | **Dapper** (raw SQL) behind `IUserRepository` / `IChatRepository` (contracts in `Gert.Database`) and `IRagStore` (contracts in the separate `Gert.Rag` capability), opened via the per-database providers (`IUserDatabaseProvider` / `IChatDatabaseProvider`) and `IRagIndexProvider` | Hand-written SQL suits `vec0`/FTS virtual tables. The **repository interfaces are the engine-portability seam** - the service layer never sees SQL, so another engine drops in as a new project (see [Engine portability](#engine-portability)). |
| Model API | **official OpenAI .NET SDK** -> any OpenAI-compatible server (vLLM in the reference deployment) | The SDK owns the wire format + SSE parsing, so requests always match the OpenAI spec (and Claude's OpenAI-compat endpoint works unchanged). vLLM extension fields (`chat_template_kwargs`, `reasoning_content`) ride via `JsonPatch`; the SDK's retry/timeout are disabled - the typed `HttpClient` + Polly stay the only resilience owner. The ports `IChatModelClient`/`IEmbeddingClient` (and the generic provider catalog/factory) live in the **`Gert.Chat`** contracts assembly; the OpenAI implementation is the self-registering **`Gert.Chat.OpenAI`** plugin. |
| HTTP | `IHttpClientFactory` + **Polly** | vLLM (`Gert.Chat`) / SearXNG (`Gert.Tools`) calls with resilience; every pipeline is configured **from the bound options**, never stock defaults. The streaming chat client has an **infinite** `HttpClient.Timeout` - the turn budget owns the stream ([turn-budgets](turn-budgets.md)) and Polly bounds only the pre-stream (time-to-headers) phase, so a connect/accept retry never repeats streamed tokens; buffered calls (embeddings, search) keep a finite client timeout sitting just outside their Polly total. The SearXNG fetch is SSRF-guarded ([security F5](security.md#3-findings--remediations)). |
| Background work | `System.Threading.Channels` + `BackgroundService` (Gert.Api) | Ingestion queue - an **Api hosting** concern wrapping `IIngestionService`. |
| Logging | **Serilog** -> JSON lines (NDJSON) on stdout | `ts`/`level`-first schema **shared with the Python tooling** so one parser reads every process; never logs tokens/`sub`/content ([operations section Logging format](operations.md#logging-format-shared)). |
| Extraction | **PdfPig** (PDF), **OpenXML** (DOCX) | Text extraction for chunking. Parses **untrusted** bytes, so it runs in an **isolated, unprivileged subprocess** (dropped privs, no net, `RLIMIT_AS`/`CPU`/`NPROC` + timeout) with DTD/external-entity **off** (XXE) and decompressed-size/zip-entry caps (bombs) - may reuse gVisor ([security F7](security.md#3-findings--remediations)). |
| Sandbox | **monty** (Rust Python) *default* - **gVisor (`runsc`)** | Isolated `run_python` behind one `IPythonSandbox` port, operator-picked (`Gert:Tools:Sandbox:Type`): monty has **no syscalls** and runs in an unprivileged sidecar (no container infra); gVisor runs real CPython in an ephemeral container. **Egress off** either way; no `/data` mount. |

## Architecture

The codebase is a **host-agnostic service layer** with a single host on top of it, kept
deliberately decoupled so the host stays swappable:

- **`Gert.Service`** holds all business logic and references nothing host-specific - no `HttpContext`, no JWT, no SSE. It depends only on abstractions: `IUserContext` (who is the current user + their tool entitlement), the persistence contracts in **`Gert.Database`** (the per-database providers `IUserDatabaseProvider` / `IChatDatabaseProvider` and their repositories - this user's connections + migrations), the RAG index contracts in **`Gert.Rag`** (`IRagIndexProvider` / `IRagStore`), and the tool/validation interfaces.
- **`Gert.Api`** drives the services over HTTP (controllers, JWT auth, SSE, the ingestion `BackgroundService`, SPA hosting).

Because the service layer can't see the host, its independence from any transport is **structural** (compiler-enforced reference direction), not a convention. Services that stream - chat - return `IAsyncEnumerable<ChatEvent>`; the Api renders those as SSE. Transport never leaks into the service.

Services are exposed two ways: **granular interfaces** (`IChatService`, `IConversationService`, `IDocumentService`, ...) for normal DI, plus an aggregate **`IGertServices`** hub that surfaces them as properties (`services.Chat`, `services.Documents`) for cross-service orchestration. Controllers inject the one granular service they need; the hub's only remaining consumer is the fail-closed validator meta-test, which walks its properties to prove every DTO has a validator.

### Project dependency tree

Arrows are project references. Everything points inward to `Gert.Service` -> `Gert.Model`; nothing the service layer touches depends on a host, which is what keeps the service layer drivable from any host.

```
  ── host ────────────────────────────────────────────────────────────────────
     Gert.Api
       refs: Service, Authentication, Database.Sqlite, Storage(+Local), Chat(+OpenAI), Tools, Ingestion
       │
       ▼
  ── impl adapters (per-impl leaves) ──────────────────────────────────────────
     Gert.Authentication   Gert.Database.Sqlite   Gert.Storage.Local   Gert.Chat.OpenAI   Gert.Tools / Gert.Ingestion
     (JWT -> IUserContext)   (vec0 + FTS5)          (local FS object store)  (OpenAI plugin)   (search+sandbox / extractors)
            └──────────────────────┴──────────────────────┴────────────────────┴─────────────┘
                                    │  all ref ▼
  ── capability contracts ─────────────────────────────────────────────────────
              Gert.Database   Gert.Storage   Gert.Chat        refs: Model only (service may reference these)
                                    │  service refs ▼
  ── core ────────────────────────────────────────────────────────────────────
                            Gert.Service        refs: Model + Database + Storage + Chat (contracts)
                                    │
                                    ▼
  ── model ────────────────────────────────────────────────────────────────────
                            Gert.Model                   no dependencies
```

The compiler enforces the inward direction: `Gert.Service` cannot reference `Gert.Api`, `Gert.Authentication`, the outside-world impl adapters (`Gert.Chat.OpenAI`, `Gert.Tools`, `Gert.Ingestion`, `Gert.Storage.Local`), or any `Gert.Database.*` **adapter** - so the service layer's independence from any host is structural, not a convention. Each capability splits into a **contracts** assembly (`Gert.Database`, `Gert.Storage`, `Gert.Chat`) that depends only on `Gert.Model` and holds the ports (plus any impl-agnostic wiring, e.g. the chat provider catalog/factory) and a **per-impl leaf** that holds the real backend; the service layer references only the contracts. The service layer talks only to the ports (`IChatModelClient`, `IEmbeddingClient`, `IObjectStore`, the database providers, `IWebSearch`, `IPythonSandbox`, `ITextExtractor`), and the real vLLM/SearXNG/gVisor/local-FS clients live behind them - so they can be swapped (or pointed at mock upstreams for tests, see [testing](testing.md#41-the-fake-external-world)) with a single DI change. For a config-selected, multi-impl capability (chat, the database engine, the RAG index engine, web search, the run_python sandbox), each impl is a self-registering plugin keyed by its `Type` token (`ICapabilityPlugin`): a generic factory dispatches by config with no central `switch`, and the composition root registers the plugins it ships (`AddGert<Capability><Impl>`, e.g. `AddGertChatOpenAI` / `AddGertDatabaseSqlite` / `AddGertRagSqlite` / `AddGertSearchSearXNG` / `AddGertSandboxMonty`). The split is by assembly when the impl carries heavy deps (chat: `Gert.Chat` contracts vs `Gert.Chat.OpenAI` leaf; database: `Gert.Database` contracts vs `Gert.Database.Sqlite` leaf; RAG: `Gert.Rag` contracts vs `Gert.Rag.Sqlite` leaf) or by namespace leaf within one adapter when the ports already live upstream in `Gert.Service.External` (search/sandbox inside `Gert.Tools`). `PluginArchitectureTests` asserts the contracts-vs-impl split and the registrar naming convention for all of them.

## Engine portability

The persistence seam is the **repository interfaces** (`IChatRepository`, and `IRagStore` in the separate RAG capability), not a generic connection wrapper - because the RAG retrieval is engine-specific (sqlite-vec + FTS5 + RRF) and cannot be abstracted into shared SQL. The service layer talks only to those interfaces, so swapping engines means adding a new `Gert.Database.*` / `Gert.Rag.*` plugin and selecting it with `Gert:Database:Type` / `Gert:Rag:Type`; `Gert.Service` is untouched.

Persistence is split along the storage/database line, each a contracts-vs-impl pair. **`Gert.Storage`** is the storage CONTRACTS assembly (refs `Gert.Model` only): `IObjectStore` is the seam for the genuine blobs under a user tree - uploads and memory bodies - plus the artifact half of the lifecycle (`DeleteScopeAsync`, the recursive tree delete) and the admin footprint listing; the shared `StorageOptions` data-root + `StorageKeys` key derivation (core policy) live here too. The local-FS implementation - `LocalObjectStore` - lives in the **`Gert.Storage.Local`** impl leaf (an S3/Azure-Blob backend is a sibling `Gert.Storage.*` project + one DI swap, `AddGertStorageLocal` -> that backend's registrar). It implements `IObjectStore` and depends only on the storage contracts - it owns artifact bytes and knows nothing about databases, so it never changes when the backend does and carries **no** reference to `Gert.Database`. (Structured user state - username, settings, the project registry - is **not** blob territory: it lives in `user.db`, [decisions section 9](decisions.md#9-userdb---structured-user-state-is-a-database-not-json-sidecars).) **`Gert.Database`** is the engine-neutral contracts kernel: the per-database providers (`IUserDatabaseProvider` / `IChatDatabaseProvider`), the repository interfaces, and the provisioning-gate refusal (`UnauthorizedDatabaseIdentityException`); `SqliteDatabasePaths` (local db-file paths) lives in `Gert.Database.Sqlite` because only a file-backed engine has paths at all (Postgres has a connection string). Each provider owns destroying its own data (`DeleteUserAsync` / `DeleteProjectAsync`: a file-backed engine drops its pooled handles + unlinks its db files, a server-backed engine deletes the rows), so deleting a user/project is a **service-orchestrated** sequence of database-halves (the structured + RAG engines) then the blob-half, rather than one store reaching into the other. The engine is itself a config-selected keyed plugin (`IDatabaseEngineBuilder` keyed by `Gert:Database:Type`, default `Sqlite`; the generic `DatabaseEngineFactory` builds the providers from the selected engine), so `Gert.Database` holds the contracts + that generic wiring and `Gert.Database.Sqlite` is the impl leaf. **`Gert.Rag`** is the parallel RAG capability - deliberately split out of the database line because a vector index need not be SQL: its `IRagStore` / `IRagIndexProvider` ports + the `IRagEngineBuilder` keyed by `Gert:Rag:Type` (default `Sqlite`) live here, and `Gert.Rag.Sqlite` (sqlite-vec + FTS5, with its own self-contained connection factory + paths so it has no dependency on `Gert.Database.Sqlite`) is the impl leaf; a dedicated vector store (Qdrant, pgvector) is a sibling `Gert.Rag.*` plugin. Database files themselves (`user.db`/`chat.db`/`rag.db`) are *not* objects - engines need real local file handles - so they stay behind the providers, and a remote object backend paired with SQLite is a split deployment (objects remote, dbs local); the full remote-storage payoff arrives with a server database. A future Postgres implementation (`Gert.Database.Postgres`) is therefore a **new plugin with its own SQL** reusing those shared layers, selected by `Gert:Database:Type=Postgres`:

- `vec0 ... MATCH ... ORDER BY distance` -> **pgvector** `<=>` / `<->` with an HNSW index; `FLOAT[1024]` -> `vector(1024)`.
- FTS5 `bm25()` -> `tsvector` + `ts_rank_cd` (no native BM25 without `pg_search`/ParadeDB or `rum`), so the lexical rank and the RRF fusion are re-tuned.

**Tenancy mapping: schema-per-user.** Postgres binds a connection to one database, so *database-per-user* fragments the connection pool and bloats the catalog at scale. The faithful analog of our per-user model is **one schema per user** in a single database: it keeps structural isolation, pools cleanly (`SET search_path` per request), and preserves the one-command delete - `DROP SCHEMA "{key}" CASCADE`. (Shared-tables + RLS would scale further but is a **non-goal**: it makes isolation a query filter, contradicting [principle #2](principles.md), and turns user deletion into a filtered `DELETE`.) At ~20 users none of this bites; the mapping matters only if Gert ever grows well beyond that.

## Solution layout (projects)

```
Gert.sln
│
├─ Gert.Model/                # POCOs only, no deps - Conversation, Message, ToolCall,
│                             #   Citation, Artifact, Document, Chunk, ChatEvent, DTOs
│
├─ Gert.Service/              # host-agnostic business logic - references Model + Database (contracts)
│  ├─ IGertServices.cs        # aggregate hub: .Chat .Conversations .Documents .Artifacts .Admin
│  ├─ IUserContext.cs         # current user's scope: Sub, AllowedTools (abstraction only)
│  ├─ Chat/                   # the detached turn pipeline: TurnPlanner, TurnRunner, queue, bus,
│  │                          #   ConversationStreamer, MessageStatusRules, SystemPrompts
│  ├─ Conversations/          # IConversationService
│  ├─ Documents/              # IDocumentService
│  ├─ Ingestion/              # IIngestionService.Ingest(doc) - pure pipeline (extract->chunk->embed->write)
│  ├─ Provisioning/           # UserProvisioner - username refresh + default-project seed (user.db)
│  ├─ Tools/                  # ITool + ToolRegistry + ITailReminder; rag/search/sandbox/todo/clock
│  │                          #   + the canvas suite (make/edit/read artifact)
│  └─ Validation/             # IValidationProvider + FluentValidation validators per model
│
├─ Gert.Storage/              # storage CONTRACTS - references Model only (service may reference it)
│  ├─ IObjectStore.cs         # the artifact-store port the service layer drives (blobs + footprint scan)
│  ├─ ObjectScope.cs / ObjectEntry.cs / StorageKeys.cs  # value types in the port signatures + key derivation
│  └─ StorageOptions.cs       #   the shared "Storage" data-root - object store + the SQLite engines' default root (each engine may override via its Parameters)
│
├─ Gert.Storage.Local/        # THE local-filesystem storage IMPL leaf (S3/Azure = sibling Gert.Storage.* project)
│  ├─ LocalObjectStore.cs     #   IObjectStore local backend - atomic PUTs under {DataRoot}/users; pure recursive tree deletes
│  └─ ServiceCollectionExtensions.cs  # AddGertStorageLocal(cfg): the IObjectStore backend (no Gert.Database ref)
│
├─ Gert.Database/             # engine-neutral persistence CONTRACTS + generic wiring (SQLite today, Postgres tomorrow)
│  ├─ IUserDatabaseProvider.cs / IChatDatabaseProvider.cs
│  ├─ IUserRepository.cs / IChatRepository.cs
│  ├─ DatabaseOptions.cs / IDatabaseEngineBuilder.cs / DatabaseEngineFactory.cs  # the Gert:Database:Type keyed-engine plugin seam
│  ├─ ServiceCollectionExtensions.cs  # AddGertDatabase(cfg): the GENERIC engine selector + the user/chat provider ports
│  └─ UnauthorizedDatabaseIdentityException.cs  # the fail-closed provisioning-gate refusal
│
├─ Gert.Database.Sqlite/      # SQLite engine IMPL leaf - references Database, Service, Storage (contracts), Model
│  ├─ SqliteDatabaseEngineBuilder.cs # IDatabaseEngineBuilder (Type=Sqlite) - builds the user/chat providers; AddGertDatabaseSqlite registers it keyed
│  ├─ SqliteUserDatabaseProvider.cs  # opens THIS user's user.db (self-migrating)
│  ├─ SqliteChatDatabaseProvider.cs  # opens a project's chat.db (WAL, busy_timeout)
│  ├─ SqliteDatabasePaths.cs         # LOCAL db-file paths - sqlite-only; Postgres has a connection string
│  ├─ SqliteUserRepository.cs        # user_meta + settings + project registry (Dapper)
│  ├─ SqliteChatRepository.cs        # Dapper
│  ├─ SqliteMigrationRunner.cs       # PRAGMA user_version, per database
│  ├─ SqliteConnectionFactory.cs     # open + migrate-on-open; DeleteDatabaseFiles (drop pools + unlink) for deletes
│  └─ Migrations/
│     ├─ user/001_init.sql
│     └─ chat/001_init.sql
│
├─ Gert.Rag/                  # RAG capability CONTRACTS + generic wiring - vector index, decoupled from the SQL engine
│  ├─ IRagIndexProvider.cs / IRagStore.cs        # the per-project index port (open + delete) the ingestion/retrieval paths drive
│  ├─ RetrievedChunk.cs / ChunkInsert.cs         # row DTOs in the port signatures
│  └─ RagOptions.cs / IRagEngineBuilder.cs / RagEngineFactory.cs / ServiceCollectionExtensions.cs  # the Gert:Rag:Type keyed-engine plugin seam + AddGertRag
│
├─ Gert.Rag.Sqlite/          # SQLite RAG engine IMPL leaf (sqlite-vec + FTS5) - self-contained, no Gert.Database.Sqlite dep
│  ├─ SqliteRagEngineBuilder.cs      # IRagEngineBuilder (Type=Sqlite); AddGertRagSqlite registers it keyed
│  ├─ SqliteRagIndexProvider.cs      # opens a project's rag.db (vec0 loaded)
│  ├─ SqliteRagStore.cs              # Dapper + sqlite-vec/FTS5 hybrid retrieval (RRF)
│  ├─ SqliteRagConnectionFactory.cs / SqliteRagMigrationRunner.cs / SqliteRagPaths.cs / RagDapperBootstrap.cs  # self-contained plumbing
│  ├─ SqliteRagParameters.cs         # Gert:Rag:Parameters - optional DataRoot override + the vec0 extension location
│  ├─ native/linux-x64/vec0.so       # vendored sqlite-vec, copied to output as vec0.so
│  └─ Migrations/rag/001_init.sql
│
├─ Gert.Database.Postgres/    # (future - not yet in the repo) pgvector + tsvector, schema-per-user - same interfaces
│  ├─ PgDatabaseProvider.cs        # schema-per-user; DROP SCHEMA CASCADE = delete user
│  ├─ PgChatRepository.cs
│  ├─ PgRagRepository.cs           # pgvector (<=>) + tsvector/ts_rank_cd
│  └─ Migrations/
│
├─ Gert.Authentication/       # JWT implementation of IUserContext - references Service (+ ASP.NET)
│  ├─ HttpUserContext.cs      # maps JWT claims (sub, groups, gert_tools) -> IUserContext
│  ├─ JwtBearer.cs            # JWKS/Authority config, NameClaimType/RoleClaimType
│  └─ Policies.cs             # Admin policy, fallback authenticated-user policy
│
├─ Gert.Chat/                 # chat CONTRACTS + generic wiring - references Model only (service may reference it)
│  ├─ IChatModelClient.cs / IEmbeddingClient.cs / IChatClientFactory.cs / IChatProviderCatalog.cs  # the ports
│  ├─ ConfigChatProviderCatalog.cs / ChatClientFactory.cs  # impl-agnostic catalog + keyed-plugin factory
│  ├─ IChatModelClientBuilder.cs / IDefaultChatProvider.cs / ChatProviderOptions.cs  # the plugin seams + metadata
│  └─ ServiceCollectionExtensions.cs  # AddGertChat(cfg): the generic catalog + factory (no impl)
│
├─ Gert.Chat.OpenAI/          # the OpenAI chat IMPL plugin leaf - references Chat, Model
│  ├─ OpenAIChatModelClient.cs / OpenAIEmbeddingClient.cs  # OpenAI-compatible (IHttpClientFactory + Polly)
│  ├─ OpenAIChatModelClientBuilder.cs / ChatProviderParameters.cs  # the plugin (keyed by Type) + its sampling
│  └─ ServiceCollectionExtensions.cs  # AddGertChatOpenAI(cfg): the keyed builder + per-provider transports
│
├─ Gert.Tools/                # tool backends + built-in tools adapter - references Service, Database, Model
│  ├─ Builtin/                #   the 12 built-in ITool impls (rag, search, sandbox, fetch, todo, clock, artifact suite, ask_user, memory, sub_agent)
│  ├─ Fetch/                  #   SSRF-guarded fetcher + the web_fetch IWebFetcher port (security F5)
│  ├─ Search/                 #   IWebSearch keyed-plugin selector (WebSearchFactory, Gert:Tools:Search:Type)
│  │  └─ SearXNG/             #     the SearXNG plugin leaf - AddGertSearchSearXNG (keyed by Type)
│  ├─ Sandbox/                #   IPythonSandbox keyed-plugin selector (PythonSandboxFactory, Gert:Tools:Sandbox:Type)
│  │  ├─ Monty/               #     the monty sidecar plugin leaf (default) - AddGertSandboxMonty
│  │  └─ GVisor/              #     the gVisor (runsc) plugin leaf - AddGertSandboxGVisor
│  └─ ServiceCollectionExtensions.cs  # AddGertTools(cfg): the generic search/sandbox selectors + fetch + AddBuiltinTools (no impl)
│
├─ Gert.Ingestion/            # text-extractor adapter - references Service, Model
│  ├─ PlainText/              #   PlainTextExtractor - the md/txt ITextExtractor leaf
│  ├─ Subprocess/             #   IsolatedTextExtractor - unprivileged subprocess for PDF/DOCX parsing (security F7)
│  └─ ServiceCollectionExtensions.cs  # AddGertIngestion(cfg): both keyed ITextExtractor leaves (md/txt + pdf/docx)
│
├─ Gert.Api/                  # HTTP host - refs Service, Authentication, Database.Sqlite, Storage(+Local), Chat(+OpenAI), Tools, Ingestion
│  ├─ Program.cs              # DI, JwtBearer, static files + SPA fallback, SSE, BackgroundService
│  ├─ appsettings.json        # NON-secret defaults only: vLLM/SearXNG URLs, embedding dim, DataRoot,
│  │                          #   Auth. Keys/secrets come from env / user-secrets / a secret store
│  │                          #   - never committed (security F8). (No tool-grant config: the JWT
│  │                          #   gert_tools claim is the sole source - auth.md section tool entitlements.)
│  ├─ Controllers/            # thin - Models, Conversations, Messages(SSE), Documents, Artifacts, Admin
│  ├─ Ingestion/              # Channel queue + IngestionWorker (BackgroundService) -> IIngestionService
│  └─ wwwroot/                # VanJS SPA source (no .NET ref, no npm) - native ES modules served
│                             #   raw in dev, minified in place on publish (NUglify).
│                             #   Layout & component conventions: docs/design/ui-components.md
│
├─ tests/                     # test projects - see docs/design/testing.md
│  ├─ Gert.Testing/           #   shared infra: fakes (vLLM/SearXNG/sandbox), GertApiFactory, JWT mint
│  ├─ Gert.Service.Tests/     #   whitebox: tool loop + turn orchestration, ingestion pipeline, validation
│  ├─ Gert.Database.Sqlite.Tests/ # repositories vs real temp SQLite (vec0 + FTS5); isolation
│  ├─ Gert.Authentication.Tests/  # JWT claims -> IUserContext; sub->key; RS256 pin
│  ├─ Gert.Chat.Tests/        #   chat/embeddings adapter units: OpenAI client, catalog, Polly wiring
│  ├─ Gert.Tools.Tests/       #   tool adapter units: built-in tools, SSRF guard, sandbox args, backend selection
│  ├─ Gert.Ingestion.Tests/   #   extractor hardening units (XXE, zip-bomb, helper output)
│  ├─ Gert.Api.Tests/         #   integration (WebApplicationFactory): SSE, auth, IDOR, admin, SPA fallback
│  ├─ Gert.Web.Minify.Tests/  #   the publish-time minifier stays ESM-safe
│  ├─ shared/                 #   ONE source of truth for both fake layers (testing.md Appendix A)
│  └─ web/                    #   harness.html - browser component-unit mount point
│
└─ tools/
   ├─ Gert.Web.Minify/        # NUglify minify-in-place console, run on publish (ui-components section 6)
   └─ smoke/                  # Python + Playwright E2E launcher (no npm) - admin+user x Chromium+Firefox
```
