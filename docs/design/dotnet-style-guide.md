# .NET style guide

How to **write** C# for Gert (`src/`, `tests/`). The guiding idea: **the conventions are
load-bearing** - sealed-by-default types, fail-closed validation, token-only identity, and
doc-citing comments are what keep a per-user-SQLite system safe, so style here is mostly
security and testability policy wearing a formatting hat.

This is the *conventions* half; the *map* half - projects, layers, the dependency tree - is
[tech-stack.md](tech-stack.md#architecture). The SPA has its own guide
([spa-style-guide.md](spa-style-guide.md)); tests have their own plan
([testing.md](testing.md)) which section 11 builds on.

Most rules below are the codebase's existing dominant convention, written down. Where the
code is split today, the rule is marked **Rule (settles an inconsistency):** - new code
follows it, old code migrates to it.

---

## 1. Project & file layout

Where a new type goes (full map: [tech-stack section solution layout](tech-stack.md#solution-layout-projects)):

| It is... | It goes in... |
|--------|-------------|
| Pure data (DTO, row, wire event) | `Gert.Model` (`Dtos/`, `Events/`, per-store folders) |
| Business logic, a validator, a tool *contract* or turn orchestration | `Gert.Service`, in its feature folder (`Chat/`, `Tools/`, `Ingestion/`, `Validation/`) |
| A built-in tool *implementation* (an `ITool`) | `Gert.Tools/Builtin/` (it talks only to ports + `Gert.Model`, like any adapter) |
| A port to the outside world | interface + record DTOs in `Gert.Service/External/`; the real client in an adapter assembly (`Gert.Chat`/`Gert.Tools`/`Gert.Ingestion`) |
| A persistence contract | `Gert.Database`; the SQL in `Gert.Database.Sqlite` |
| Anything that needs `HttpContext`, JWT, SSE, a `BackgroundService` | the host (`Gert.Api`) |

- **References point inward only** - host -> adapters -> `Gert.Service` -> `Gert.Model` -
  enforced by `tests/Gert.Service.Tests/ArchitectureTests.cs` (NetArchTest) on top of the
  csproj graph. If your change needs an outward reference, the type is in the wrong project.
- **One public type per file**, file named after the type, file-scoped namespace matching the
  folder path. Tiny companions may share a file (`IConversationBus` + `IConversationSubscription`).
- Interface and implementation sit side by side in the same feature folder
  (`Conversations/IConversationService.cs` + `ConversationService.cs`).
- Usings: System first, then Gert, then Microsoft; no global usings in source.

## 2. Types

- **`sealed` by default** - every class and record in `src/` is sealed unless designed for
  inheritance (none currently are). `static class` for pure helpers (`MessageStatusRules`,
  `StorageKeys`, `ValidationRules`).
- **Records for data, classes for behaviour.** Data shapes are `sealed record` with
  `required ... { get; init; }` - no settable state, construction proves completeness:

  ```csharp
  // src/Gert.Service/Chat/TurnJob.cs
  public sealed record TurnJob
  {
      public required string Iss { get; init; }
      public required string Sub { get; init; }
      /// <summary>Entitlement snapshot - the hard ceiling for tool execution.</summary>
      public required IReadOnlySet<string> AllowedToolIds { get; init; }
      // ...
  }
  ```

  Services are `sealed class` with constructor-injected dependencies. Small identity keys are
  `readonly record struct` (`ControlScope`, `ObjectScope`) - that is the *only* sanctioned struct use.
- A deliberate exception gets a justifying comment (`ToolToggles` is a class with hand-written
  equality because it needs dictionary semantics - the comment says so).
- Public surfaces speak `IReadOnlyList<>` / `IReadOnlySet<>` / `IReadOnlyDictionary<>`, never
  `List<>`/`Dictionary<>`. Collection defaults use collection expressions:
  `public IReadOnlyList<Citation> Citations { get; init; } = [];`.
- **NRT is on everywhere; `?` is meaningful** (nullable optionality encodes PATCH "null =
  unchanged" in request DTOs). The null-forgiving `!` is allowed in exactly two shapes:
  the FluentValidation `RuleFor(r => r.X!)....When(r => r.X is not null)` idiom, and a
  just-assigned-non-null property *with a comment*. Any other `!` needs a comment saying why
  it cannot be null - or a refactor to a local.
- Constructor guards: stored dependencies use `_x = x ?? throw new ArgumentNullException(nameof(x));`;
  method arguments use `ArgumentNullException.ThrowIfNull(x)`. Keep the two-form split.

## 3. Async & cancellation

- **`ConfigureAwait(false)` on every await** in `Gert.Service` and the adapters (it is already
  uniform there - keep it that way). Host code follows the same habit; only UI-context code
  (none exists) would be exempt.
- **`CancellationToken cancellationToken = default` is the last parameter** of every public
  async method, and it is **always propagated** to every awaited call. Best-effort cleanup
  paths deliberately pass `CancellationToken.None` *with a comment saying why*
  (`TurnRunner.FinalizeErrorAsync`).
- **Rethrow `OperationCanceledException` before any degrade/fallback catch.** Every
  best-effort `catch (Exception)` is preceded by the OCE guard so cancellation is never
  swallowed into a degraded result:

  ```csharp
  // src/Gert.Tools.Builtin/Builtin/PythonSandboxTool.cs
  catch (OperationCanceledException)
  {
      throw;
  }
  catch (Exception ex)
  {
      // Hard failure (e.g. the container failed to start): degrade to a tool
      // error the model can read, never a torn-down turn.
      return new ToolResult { Success = false, Error = $"sandbox error: {ex.Message}" };
  }
  ```
- **No sync-over-async** (`.Result`, `.Wait()`, `GetAwaiter().GetResult()`) anywhere.
- **No fire-and-forget without an owner.** Detached work has a host owner that tracks it,
  catches, logs, and survives: ingestion rides a `Channel` queue drained by a
  `BackgroundService` (`IngestionWorker`); the `TurnLauncher` (an `IHostedService`) tracks each
  turn's background task and cancels/drains them on shutdown - that owner bounds and finalises
  the work. Never a bare `Task.Run` / discarded task with no owner.
  (`ConversationBus.Publish` is deliberately a non-async `TryWrite` with documented drop
  semantics - the exception that proves the rule.)
- Streams are `IAsyncEnumerable<T>` end to end; hosts render them (SSE / stdout). Argument
  checks for an `async IAsyncEnumerable` iterator go in a non-iterator wrapper method that
  validates then delegates, so they throw at the call site, not on first `MoveNext`.

## 4. DI & composition

- Each layer exposes **one `AddGertX` extension** (`AddGertServices`, `AddGertExternal(cfg)`,
  `AddGertJwtAuth`, `AddGertAuthorization`) and registers with **`TryAdd*`**
  so the host and tests can override any seam with a plain `Add*` - this is the mechanism the
  fakes rely on ([testing section 4.2](testing.md#42-two-ways-to-fake-the-outside-world)). Deliberate
  exceptions: `AddScoped<ITool, ...>` multi-registrations must accumulate.

  **Rule:** *every* adapter gets one, and a capability-impl registrar is named
  `AddGert<Capability><Impl>` (`AddGertChatOpenAI`, `AddGertDatabaseSqlite`,
  `AddGertStorageLocal`, `AddGertSearchSearXNG`, `AddGertSandboxMonty`, `AddGertSandboxGVisor`);
  the generic per-capability registrar keeps the bare capability name (`AddGertChat`,
  `AddGertTools`). The database seam lives in the `AddGertDatabaseSqlite(cfg)` extension in
  `Gert.Database.Sqlite`; the host calls it rather than hand-registering copy-pasted lines.
- **Every registration carries a lifetime-rationale comment.** The rule the comments encode:
  scoped if it (transitively) reads `IUserContext`; singleton for process-wide state. The
  exemplar (`src/Gert.Service/ServiceCollectionExtensions.cs`):

  ```csharp
  // Detached turn pipeline (chat-and-tools.md section detached turns): the bus is a
  // process-wide singleton (live delivery is per-process; the DB is the
  // cross-instance truth); the reader is scoped (per-request IUserContext).
  services.TryAddSingleton<Chat.Bus.IConversationBus, Chat.Bus.ConversationBus>();
  services.TryAddScoped<IConversationReader, ConversationReader>();
  ```
- **Rule (settled):** inject the **granular interface** you need
  (`IConversationService`, `ITurnPlanner`, `IChatProviderCatalog`), not the `IGertServices` hub.
  Every API controller does ([tech-stack section Architecture](tech-stack.md#architecture));
  the hub remains only as the canonical enumeration of host-facing services. Its sole
  consumer is the fail-closed validator meta-test ([testing](testing.md), which walks its
  properties to prove every DTO has a validator). Don't inject it into new code.
- **Rule (settled):** options bind via
  `services.AddOptions<T>().Bind(configuration.GetSection(T.SectionName)).ValidateDataAnnotations().ValidateOnStart()`
  - *the* idiom, modelled on `Gert.Chat/ServiceCollectionExtensions.cs` (which does
  `.Bind(...).ValidateOnStart()` for all five of its option types); add
  `.ValidateDataAnnotations()` only when the type carries annotations
  (`RateLimiting.PolicyOptions` is the exemplar). Each adapter binds its own options in its
  `AddGert*` registrar (e.g. `AddGertRagSqlite` binds `StorageOptions` + `SqliteRagParameters`),
  never a bare `services.Configure<T>(section)` at the host. `ArtifactTicketOptions` is a bound
  option whose >=32-byte-secret fail-fast is an
  `IValidateOptions<ArtifactTicketOptions>` run by `ValidateOnStart` (security F3).
  Computed options that are derived rather than section-bound (`SecurityHeadersOptions`'
  origins) use `AddOptions<T>().Configure(...)` with the derived values. Options classes
  are `sealed`, carry `public const string SectionName`, and xml-doc each property with its
  default and whether it is a secret (security F8: secrets come from env / user-secrets,
  never `appsettings.json`). A bound property with no consumer is a bug - delete dead knobs.

## 5. Time & randomness

- **Rule (settled):** all time in `Gert.Service` and the adapters comes from
  the injected `TimeProvider` - `_time.GetUtcNow()` for timestamps,
  `GetTimestamp()`/`GetElapsedTime()` for intervals. Never `DateTimeOffset.UtcNow` /
  `DateTime.Now`. The sweep is complete: every service that stamps a row
  (`TurnPlanner`, `TurnRunner`, `ConversationService`, `ConversationReader`,
  `DocumentService`, `ProjectService`, `UserProvisioner`,
  `SqliteUserRepository`) takes `TimeProvider` in its constructor; the default
  `TimeProvider.System` is `TryAdd`-registered in `AddGertServices`.
  Why: fakeable time is what lets tests pin the instant and exercise the orphan-horizon and
  expiry rules deterministically ("tests pin the instant" - the registration comment in
  `ServiceCollectionExtensions.cs` states the intent). `Stopwatch`/`Environment.TickCount`
  for pure in-process interval measurement stay as they are.
- Randomness that matters (keys, ids) uses `RandomNumberGenerator`/`Guid.NewGuid()`; anything
  a test must reproduce gets injected or seeded, like `FakeEmbeddings`' hash-derived vectors
  ([testing Appendix A.2](testing.md#a2-deterministic-embeddings-hash---1024-dim-unit-vector)).

## 6. Validation

Fail-closed, in the service layer, so validation runs identically for every caller
([testing section validation](testing.md#validation---the-input-security-boundary)):

- Every request DTO has one `AbstractValidator<TDto>`, registered as both the concrete type
  (for `SetValidator` composition) and `IValidator<T>`, singleton. The reflection meta-test
  (`FailClosedMetaTest`) goes red if one is missing - a new input type cannot ship unvalidated.
- **Rule (settles an inconsistency):** registration is half the contract - **the service must
  invoke the validator** through the standard seam before acting:

  ```csharp
  // src/Gert.Service/Chat/TurnPlanner.cs - the canonical opening
  // 1. Validate (fail-closed at the service boundary, before any disk touch).
  var validation = _validation.Validate(request);
  if (!validation.IsValid)
  {
      throw new ValidationException(validation);
  }
  ```

  This is the uniform shape (`TurnPlanner`, `DocumentService`,
  `SettingsService`, `ProjectService`, `ConversationService`). A registered validator
  that is never invoked is a silent hole - a service-tier test pins the invocation.
- Shared vocabulary lives in `ValidationRules`: the `SafeText`/`OptionalSafeText` extensions
  (length, control chars, bidi overrides) for every human-text field, and pure predicates
  (`IsWellFormedId`, `IsSafeIdentifier`) usable from validators and route
  guards alike. Character checks are hand-rolled loops over allowlisted ranges - no regexes
  at the input boundary, so no ReDoS by construction.
- Every rule carries `.WithMessage` + `.WithErrorCode` with dotted **snake_case** codes:
  `text.too_long`, `attachments.too_many`, `model_id.invalid`, `upload.extension`.
- Route params that feed paths get throwing guards (`RouteParams.RequireValid*` at the top of
  **every** `{pid}`/`{key}` action - no exceptions; a surface that misses it 500s where its
  siblings 400) wired to the same `ValidationException` -> 400 handler.

## 7. Errors & logging

- **Boundary failures throw typed, sealed exceptions** with structured properties; the doc
  comment states the HTTP mapping, and each ships with a host `IExceptionHandler`
  (`ValidationException` -> 400 via `ValidationExceptionHandler`, `TurnInProgressException` ->
  409 via `TurnConflictExceptionHandler`). A new service exception without a handler becomes
  somebody's 500 - register both in the same change. All error payloads are branded
  `ProblemDetails` (`GertProblem`).
- **In-loop failures return result objects, never throw**: `ToolResult { Success, Error }`,
  `ExtractionResult`, `PythonSandboxResult` - an error the model can read and correct. Static
  factories (`ExtractionResult.Failed(...)`) over scattered initializers.
- **Rule:** never serialize a raw `ex.Message` into a user-visible
  payload or persisted event - upstream exception text can echo internal URLs or prompt
  fragments. Emit a generic message; the detail goes to the log.
- **Rule:** every intentional catch-and-continue gets **both** a
  comment naming the degrade decision *and* a log line - `LogWarning` for a swallowed
  best-effort failure, `LogError` for a catch-all converted to a user-visible error. Today
  several best-effort swallows are silent; an unlogged swallow is a defect.
- What gets logged: `ILogger<T>` with structured message templates (named placeholders, no
  interpolation), rendered as NDJSON by the shared formatter. **Never** message/document
  content, tokens, or raw `sub`/`iss` - identity appears only as the `uid` hash
  ([operations section Logging format](operations.md#logging-format-shared)).

## 8. Data access

- **Connections are open-per-use** through the shared `SqliteConnectionFactory`: ensure the
  parent directory, open, apply
  `PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON;`, then
  migrate-on-open (`SqliteMigrationRunner`: `PRAGMA user_version`, embedded
  `Migrations/{family}/NNN_*.sql`, applied under a `BEGIN IMMEDIATE` transaction with a
  re-read inside the lock so racing provisioners no-op). Repositories are `await using` -
  the provider owns the connection, `DisposeAsync` closes it.
- **Dapper with `const string` SQL** and named parameters - the only sanctioned dynamism is
  appending a fixed, parameterized predicate. Identifiers are never built from input. The one
  unparameterizable spot (`PRAGMA user_version = {int}`) interpolates a locally-parsed `int`
  with a comment.
- **Private `sealed record` row types** at the bottom of the repository file
  (`ConversationRow`, `MessageRow`), snake_case columns mapped via Dapper's underscore
  matching, explicit `MapX(row)` functions, exhaustive `switch` enum<->token mappers that throw
  on unknown values, timestamps as ISO-8601 `"o"` UTC text.
- **FTS5: parameterize *and* phrase-quote.** Binding the MATCH operand stops SQL injection
  but not FTS5 *query-language* injection - neutralize operators too:

  ```csharp
  // src/Gert.Rag.Sqlite/SqliteRagStore.cs
  private static string EscapeFtsQuery(string query) =>
      "\"" + query.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
  ```

  vec0 embeddings cross the boundary as packed little-endian float32 BLOBs with an asserted
  dimension - never string-formatted vectors.
- **Multi-statement writes get a transaction** (`await using var transaction = ...BeginTransactionAsync`
  + explicit `CommitAsync`) - chunk inserts, cascading deletes, anything where a partial write
  would be observable. Service methods that span blob + row + index writes must state their
  failure order: compensate or reorder so a midway fault never leaves a half-ingested,
  searchable state.

## 9. External adapters

- **Pure parser / I/O shell split.** Every adapter pairs a network-free, unit-testable core
  (`OpenAIStreamParser` - "Pure, network-free parser...", `SearXngResponseParser`, `SsrfGuard`,
  `SandboxCommandBuilder`, `ZipBombGuard`) with a thin shell that does the I/O. The
  security-relevant decision must be expressible as a pure function so it is testable without
  a socket.
- **Typed options per upstream** (`EmbeddingsOptions`, `SearXngOptions`, ...) bound per section 4; defaults
  *are* the secure posture (egress off, caps on).
- **Resilience and timeout ownership is decided per call semantics, with the decision
  commented at the registration site**: retries on idempotent calls (embeddings, search),
  deliberately none where a retry repeats work unsafely (a sandbox run).
  **Rule:** a *streaming* client's `HttpClient.Timeout` must not
  cap the stream - set it to `Timeout.InfiniteTimeSpan` and let the turn budget
  (`MaxTurnDuration`) own the wall clock; resilience handlers must be configured *from* the
  bound options, not stock defaults (`HttpClientWiringTests` pins the wiring).
- **Never disable TLS validation.** The only `RequireHttpsMetadata = false` in the codebase
  is the doubly-gated dev-JWKS branch; no outbound client ever weakens server-cert checks.
- **Anything URL-shaped that originates from a user or the LLM goes through the SSRF guard**
  ([security F5](security.md#3-findings--remediations)): `SafeHttpFetcher` owns its own
  `SocketsHttpHandler` (deliberately *not* an `IHttpClientFactory` client) so the
  connect-time IP check cannot be bypassed by a pooled handler; redirects are re-vetted;
  bodies are read with an explicit byte cap (`ReadCappedAsync` - make capped reads the
  standard for every external body).
- Adapters degrade to a graceful result (`MapFailure`, "helper absent -> this item failed"),
  never throw infrastructure errors into the tool loop: fail the item, never the host.

## 10. Doc comments

- **Every public type and member carries `///`**, and the type-level summary **cites the
  owning design doc by file + section or finding id** - this is the codebase's most
  distinctive convention:

  ```csharp
  // src/Gert.Service/Storage/StorageKeys.cs
  /// <summary>
  /// The identity -> storage-key policy shared by every storage and database adapter
  /// (decisions section 3, security F12): the user key derivation and the input-shape
  /// guards for project ids and admin-supplied user keys. This is core security
  /// policy - adapters (local FS, S3, SQLite, Postgres) consume it, never redefine it.
  /// </summary>
  ```

  `<see cref>` for code references; `/// <inheritdoc />` on interface implementations; long
  invariant explanations live at one "home" type and other sites point at it.
- **Keep citations fresh.** `make check-links` gates markdown links but cannot see C#
  comments, so a renamed type, doc section, or migrated decision means **grepping for and
  updating its citers in the same change** (the CLAUDE.md rule). Treat a stale citation as a
  review-blocking defect, the same as a broken link.

## 11. Testing

The tiers, the fakes, and the shared spec are defined in [testing.md](testing.md#2-the-pyramid)
- this section is only the *house style* inside them.

- **Names are sentence-style snake_case facts stating the guarantee**, not the method:
  `Delete_with_traversal_key_is_rejected_and_never_escapes_the_tree`,
  `Invalid_input_is_rejected_on_the_console_path_too`. One behaviour per `[Fact]`; hostile
  input tables are `[Theory]` + `[InlineData]`; cross-cutting corpora are `[MemberData]` over
  `Gert.Testing/TestData` (NaughtyStrings).
- **Placement:** service tier = orchestration, event streams, validators (repos may be
  NSubstitute); SQLite tier = anything containing SQL - always **real temp SQLite** (vec0 +
  FTS5), never in-memory stand-ins for ranking; API tier = things that only exist with HTTP
  (status codes, SSE framing, auth middleware, headers); Python E2E = things that only
  exist in a browser, plus wire fidelity through the real adapters.
- Repository tests: `await using var root = new TempDataRoot();` -> provider -> repo; the
  provider self-migrates on open (no setup SQL in tests). Direct-file tests call
  `SqliteConnection.ClearAllPools()` before the temp dir is deleted (`MigrationTests.cs`
  does; suites that skip it leak pooled handles on Windows).
- **Fakes come from `Gert.Testing/Fakes`**, implement the real service-layer ports, and obey
  the two-fidelity rule: in-process .NET fakes for unit/integration, the Python mock
  upstreams for E2E, both driven by the one shared spec
  ([testing Appendix A](testing.md#appendix-a---the-shared-fake-spec)). Never hand-roll a
  second chat/embedding fake; new resolution *semantics* land on both sides plus a
  conformance case. Deterministic-by-construction beats seeded-random.
- Integration: one `GertApiFactory` per class via `IClassFixture`, per-test unique
  `_sub = "user-" + Guid...`; negative paths assert status code, branded `problem+json`, *and*
  that the side effect did not happen. Polling is bounded and throws on timeout - never an
  open-ended `while` + `Task.Delay`.
- **Every security finding F1-F12 has a test** pinning its control
  ([security section 3](security.md#3-findings--remediations)); the test's doc comment cites the
  finding id. Fail-closed guarantees get a reflection meta-test **plus a canary** asserting
  the discovery still finds known members. An intentional carve-out from a documented rule
  updates the doc table in the same change.

---

## Cheat sheet

- New type: data -> `Gert.Model`; logic -> `Gert.Service/<Feature>/`; port interface ->
  `Gert.Service/External/`; client -> an adapter assembly (`Gert.Chat`/`Gert.Tools`/`Gert.Ingestion`); SQL -> `Gert.Database.Sqlite`.
  References point inward only; the architecture test bites.
- `sealed` everything; `sealed record` + `required`/`init` for data, `sealed class` for
  services, `readonly record struct` for tiny keys; `!` only with a comment.
- Every await: `ConfigureAwait(false)`; token last and always forwarded;
  `catch (OperationCanceledException) { throw; }` before any degrade catch; detached work
  has a worker that owns it.
- DI: `TryAdd*` + lifetime-rationale comment; granular interface, not the hub *(settled)*;
  one `AddGertX` per adapter *(settled)*;
  `AddOptions<T>().Bind(...).ValidateDataAnnotations().ValidateOnStart()` *(settled)*.
- Time: injected `TimeProvider`, never `DateTimeOffset.UtcNow` *(settled)*.
- Validation: validator registered **and invoked** in the service *(invocation: settlement)*;
  `SafeText`; snake_case error codes; the meta-test is the net.
- Errors: typed sealed exception + `IExceptionHandler` pair; result objects in-loop; no raw
  `ex.Message` to users *(settlement)*; every swallow logs *(settlement)*; NDJSON, never
  content/tokens/`sub`.
- SQL: shared factory (WAL/busy_timeout/FK, migrate-on-open under IMMEDIATE); `const string`
  + parameters + private row records; FTS5 phrase-quoted; transactions for multi-statement
  writes.
- Adapters: pure core / I/O shell; typed options; timeout ownership commented (streaming =
  infinite client timeout, *settlement*); TLS validation never off; user/LLM URLs only
  through the SSRF guard.
- Docs: every public member cites its design doc; a rename sweeps its citers in the same
  change.
- Tests: snake_case guarantee names; real temp SQLite (+ `ClearAllPools`); fakes from
  `Gert.Testing` on the shared spec; bounded polling; one test per security finding.
