# CLAUDE.md

Gert - a privacy-first, self-hosted LLM chat server. ASP.NET Core 10 Web API (+ vendored
VanJS SPA) over **per-user SQLite**: there is no central database - every user is a folder
keyed by `sha256(iss + sub)` from the validated JWT, every project a subfolder with its own
`chat.db` + `rag.db`.

## Docs first

`docs/design/` is the source of truth for design and rationale. **Start at
[docs/design/README.md](docs/design/README.md)** - its "which doc for which change" table
routes you. When you change behaviour a design doc covers, update the doc in the same change;
code comments cite docs by section, so keep both ends accurate.

## Repo map

- `src/` - inward-only references (enforced by an architecture test):
  host (`Gert.Api`) -> adapters -> capability CONTRACTS -> `Gert.Service` (all business
  logic, host-agnostic) -> `Gert.Model` (POCOs, no deps). A capability splits into a
  **contracts** assembly (the ports + any generic, impl-agnostic wiring; depends only on
  `Gert.Model`, so the service layer may reference it) and one or more **per-impl leaf**
  adapters: `Gert.Chat` (chat/embeddings ports + the generic provider catalog/factory) with
  impl `Gert.Chat.OpenAI`; `Gert.Storage` (`IObjectStore` port + `StorageOptions`)
  with impl `Gert.Storage.Local`; `Gert.Database` (`user.db`/`chat.db` ports, keyed by
  `Gert:Database:Type`) with impl `Gert.Database.Sqlite`; `Gert.Rag` (the vector/RAG index
  port `IRagStore`/`IRagIndexProvider`, keyed by `Gert:Rag:Type` - decoupled from the SQL
  engine) with impl `Gert.Rag.Sqlite` (sqlite-vec + FTS5). The
  remaining adapters: `Gert.Tools` (web search + sandbox backends **and** the built-in `ITool`
  implementations under `Builtin/`; the `IWebSearch`/`IWebFetcher`/`IPythonSandbox` ports stay
  in `Gert.Service.External`), `Gert.Ingestion` (the md/txt + isolated pdf/docx text
  extractors), `Gert.Authentication`. `Gert.Service` keeps the tool *contracts*
  (`ITool`/`ToolRegistry`) + the turn orchestration, not the tool impls.
- **Capability-plugin pattern**: a config-selected capability (chat, the database engine, the
  RAG engine, web search, the sandbox) is a self-registering plugin keyed by its `Type` token
  (`Gert.Model.Plugins.ICapabilityPlugin`). Each impl exposes an `AddGert<Capability><Impl>`
  registrar (e.g. `AddGertChatOpenAI` / `AddGertDatabaseSqlite` / `AddGertRagSqlite`) that registers its
  builder keyed by `Type`; the generic factory dispatches by config with no central `switch`.
  `Gert.Api/Program.cs` is the composition root that registers the plugins it wants available.
  The contracts-vs-impl split is enforced by `PluginArchitectureTests` (plugins live only in
  the impl leaf; contracts never reference the impl).
- `src/Gert.Api/wwwroot/` - the SPA source: no npm, no build step, native ES modules.
- `tests/` - xUnit suites + shared fakes (`Gert.Testing`); real temp SQLite for repository tests.
- `tools/smoke/` - Python + Playwright E2E and mock upstreams (uv-managed).
- `docs/installation/configuration.md` - every operator knob.

## Commands

- `make build` / `make test` - .NET build (warnings are errors) / full test suite.
- `make lint` - ruff + mypy `--strict` over `tools/smoke`.
- `make check-links` - every relative link/anchor in tracked markdown resolves (CI gate;
  run after editing docs).
- `make smoke-auth` - boot Python mocks + the FakeE2E host, run the API auth smoke (no browsers).
- `make serve-mock [ROLE=admin|user|limited]` - run the real app against mock upstreams and
  open it in your own browser (mints a dev JWT; no Pocket ID needed).

## Rules that bite

- **The user key comes only from the validated token.** Never derive a user from a path,
  query, or body. The one request-supplied selector, `pid`, must only ever be joined *under*
  the token-derived folder ([docs/design/principles.md](docs/design/principles.md)).
- **Validation is fail-closed.** Every request DTO needs a registered `IValidator<T>`; a
  reflection meta-test goes red if one is missing.
- **No npm.** The SPA is no-build ESM with vendored libs; minification is .NET-only on publish.
- **No secrets in `appsettings.json`** - env vars / `dotnet user-secrets` only. Nothing under
  `.dev/` is ever committed.
- **Security findings F1-F12** ([docs/design/security.md](docs/design/security.md)) each have
  tests - don't weaken a control without reading its finding first.
