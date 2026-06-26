# AGENTS.md

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
  host (`Gert.Api`) -> `Gert.Agent` (the turn/agent EXECUTION engine) -> adapters -> capability
  CONTRACTS -> `Gert.Service` (all business logic, host-agnostic) -> `Gert.Model` (POCOs, no deps).
  A capability splits into a
  **contracts** assembly (the ports + any generic, impl-agnostic wiring; depends only on
  `Gert.Model`, so the service layer may reference it) and one or more **per-impl leaf**
  adapters: `Gert.Chat` (chat/embeddings ports + the generic provider catalog/factory) with
  impl `Gert.Chat.OpenAI`; `Gert.Storage` (`IObjectStore` port + `StorageOptions`)
  with impl `Gert.Storage.Local`; `Gert.Database` (`user.db`/`chat.db` ports, keyed by
  `Gert:Database:Type`) with impl `Gert.Database.Sqlite`; `Gert.Rag` (the vector/RAG index
  port `IRagStore`/`IRagIndexProvider`, keyed by `Gert:Rag:Type` - decoupled from the SQL
  engine) with impl `Gert.Rag.Sqlite` (sqlite-vec + FTS5); and `Gert.Tools` (the
  core tool contracts at the root - `ITool`/`ToolRegistry`/`ToolResult`/`ToolInvocation`/
  `ToolType` - organized into folder-matched sub-namespaces: `Gert.Tools.Args` (the typed
  tool-arg records), `Gert.Tools.Results` (the typed result-payload POCOs), `Gert.Tools.Resources`
  (the `IToolResources`/`IObjectResource`/`IRagResource` surfaces), `Gert.Tools.Ui` (the `IToolUi`
  ask-user contracts), `Gert.Tools.Hosting` (the `IToolHost`/`IToolDelegate`/`ToolLimits` seams),
  and `Gert.Tools.Ports` (the `IWebSearch`/`IWebFetcher`/`IPythonSandbox` external ports)) with
  impl `Gert.Tools.Builtin` (web search + sandbox backends, the built-in `ITool` implementations
  under `Builtin/`, and the id-only `ToolRegistry` derived from them); and `Gert.TurnControl` (the
  turn control-plane port `ITurnControlBus`/`ITurnControlSubscription` + `ControlScope`/`AnswerValidation`,
  the cancel + ask_user pub/sub seam) with impl `Gert.TurnControl.Local` (the in-process broker;
  a networked Kafka/NATS impl is the seam for splitting the agent host from the chat API). The
  remaining adapters: `Gert.Ingestion` (the md/txt + isolated pdf/docx text extractors), `Gert.Authentication`.
- `Gert.Agent` - the turn/agent EXECUTION engine, a layer between the host and `Gert.Service`
  (host -> `Gert.Agent` -> `Gert.Service`): the `TurnLauncher` (an `IHostedService` that launches
  each planned turn on a detached task, bounds concurrency at `Gert:Turn:MaxConcurrentTurns`, and
  cancels/drains in-flight runners on shutdown), the
  `TurnPlanner`/`TurnRunner`, the reusable `IAgentLoop`, the turn control plane (cancel + ask_user
  over the `ITurnControlBus` port the runner subscribes to per turn), the worker-scope
  `DetachedUserContext`, and the chat
  tool-host wiring (`Gert.Agent.Hosting`: `ChatToolHost`, `ProjectRagResource`, `ChatToolDelegate`
  over the shared loop). Registered by `AddGertAgent` (the host calls it right after
  `AddGertServices`). It drives `ITool` through the `Gert.Tools` contracts, not the impls.
  `Gert.Service` keeps the request-facing read side - the conversation bus + reader/streamer
  (`Gert.Service.Chat`) plus the shared turn vocabulary (`TurnOptions`, `MessageStatusRules`,
  `PromptOptions`, `TurnInProgressException`) the read side also consumes - and must NOT reference
  `Gert.Agent` back (an architecture test enforces both this missing edge and that `Gert.Agent`
  never references the host or an adapter impl leaf). `Gert.Tools.Builtin` references neither
  `Gert.Service` nor any capability impl: every tool reaches RAG, objects, the UI, and delegation
  through the `IToolHost` seams (`IRagResource`/`IObjectResource`/`IToolUi`/`IToolDelegate`) handed
  at call time - the chat driver (in `Gert.Agent`) supplies the impls. An architecture test
  enforces the missing `Gert.Service` edge.
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
  reflection meta-test goes red if one is missing. The validation sub-layer is its own assembly
  `Gert.Validation` (the `IValidationProvider` port + `Validated<T>` proof + per-DTO validators;
  depends only on `Gert.Model` + `Gert.Tools`); `AddGertServices` calls its `AddGertValidation`.
- **No npm.** The SPA is no-build ESM with vendored libs; on publish a .NET target drives a
  pinned, SHA-512-verified esbuild Go binary (fetched from the npm-registry tarball - no npm,
  no Node) to bundle it into one `app.js` + `app.css` (`tools/Gert.Web.Bundle`).
- **No secrets in `appsettings.json`** - env vars / `dotnet user-secrets` only. Nothing under
  `.dev/` is ever committed.
- **Security findings F1-F12** ([docs/design/security.md](docs/design/security.md)) each have
  tests - don't weaken a control without reading its finding first.
