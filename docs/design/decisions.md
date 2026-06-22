# Design decisions

The decision record: choices that shaped the system, each with the question it answered, the
**Decision**, and the why - including the alternatives that were rejected and what would have
to change to revisit one. All entries below are settled; new decisions append here. Questions
still open live with their owning doc ([configuration section 9](configuration.md#9-open-decisions),
[turn-budgets](turn-budgets.md)) until they're settled enough to record.

## 1. Embedding model + dimension

Bakes into the `vec0` table. Source: vLLM embeddings endpoint vs. a separate service. See [Operations -> Cross-cutting (Embeddings)](operations.md#cross-cutting-concerns).

- **Decision:** **bge-m3, 1024-dim, served by vLLM** (`--task embed`, OpenAI-compatible `/v1/embeddings`). Chosen for multilingual retrieval (incl. Dutch) and 8k context; keeps the embedding service count at one. `vec_chunks.embedding` is `FLOAT[1024]`.

## 2. One DB vs two per project

Split (recommended) or merged `gert.db`. See [Per-user storage -> Why two databases](storage-and-data.md#why-two-databases-per-project).

- **Decision:** **Two databases - `chat.db` + `rag.db`, one pair per project.** Lets "forget my documents" wipe a project's RAG without touching its chat history, and keeps vector-index rebuilds/`VACUUM` off the chat write path. The extra wiring is negligible at ~20 users. (The pair lives *inside* each project folder - see [decision section 7](#7-project-model--isolation).)

## 3. User key

`sha256(iss + sub)` (recommended) vs. raw `sub` vs. email. The one stable, store-agnostic
identity every backend scopes by - no filesystem, object store, or database knows "this user"
natively. See [Per-user storage -> Resolving paths](storage-and-data.md#resolving-paths).

- **Decision:** **`sha256(iss + "\n" + sub)` lowercase hex**, with a fail-closed provisioning gate; what the store holds is descriptive (the username in `user.db`, [section 9](#9-userdb---structured-user-state-is-a-database-not-json-sidecars)), never a gate.
  - **Anchor on `sub`, not email or username.** `sub` is Pocket ID's stable, opaque UUID - *never renamed, never recycled*. Email is **mutable** (a rename orphans the user's data) and **recycled** (a reassigned address would inherit the prior owner's data - the exact reuse attack we want to avoid); username is renamed. `sub` is also no less trusted: every claim in a signature-validated JWT is equally trusted, `sub` is just the most stable.
  - **Namespace by issuer.** `sub` is only unique *within* an issuer, so the key hashes `iss + "\n" + sub`. With one IdP this is moot; it makes a second IdP collision-proof for free.
  - **Hashing** gives a fixed-length, traversal-proof name that is safe as a path *or* an object key for any value the IdP emits (a directory, an object-key prefix, a namespace); the opaque name is covered by the stored username (`user.db`) and `GET /api/admin/users` for key->user mapping.
  - **Collision is not the threat** - `sha256` makes cross-user collision infeasible regardless of input; the real risk is *identifier reuse*, addressed by anchoring on `sub` (above).
  - **Validate before touching any store** ([principle #6](principles.md)): provisioning asserts `iss` == configured authority, `aud` matches, and `sub` is present + within a bounded charset/length **before** any key is derived or any store is opened. No well-formed identity -> no stored state.
  - **Trust the validated JWT past the gate** ([security F12](security.md#3-findings--remediations)): the user key derives from the token and nothing else, so no per-request store-side re-check exists. The username stored in `user.db` is purely descriptive - key->user mapping for `GET /api/admin/users`, refreshed from the token when it changes - and each database's `PRAGMA user_version` anchors migrations ([section 9](#9-userdb---structured-user-state-is-a-database-not-json-sidecars)).

## 4. Token lifetime / revocation

Strategy for immediate off-boarding. See [Operations -> User lifecycle](operations.md#user-lifecycle---remove-a-user).

- **Decision:** **Accept Pocket ID's ~1-hour access token as the revocation window; no denylist.** Pocket ID does not support a short, independently-configurable access-token lifetime ([issue #792](https://github.com/pocket-id/pocket-id/issues/792), closed *not planned*), so a short fixed lifetime (e.g. 10-15 min) isn't available. Refresh tokens (with rotation) still drive the SPA's silent renewal. Off-boarding = deactivate in Pocket ID, effective within ~1h. **We deliberately do *not* add a `sub`-denylist:** it is shared, mutable, per-instance auth state that would break running **multiple GERT instances** (a denied `sub` on instance A wouldn't be known to instance B without a shared store), defeating horizontal scaling. GERT stays **stateless** - every instance validates a token purely from the JWT + Pocket ID's JWKS, nothing shared. If sub-hour revocation ever becomes a hard requirement, the right answer is a shorter token lifetime at the IdP (or a shared-store check introduced explicitly), not in-process state.

## 5. OCR

Should scanned-only PDFs (`old-scan.pdf`) be OCR'd (e.g. Tesseract) instead of failing, or is "no extractable text -> Failed" the intended behavior?

- **Decision:** **Fail fast - "no extractable text -> Failed".** No OCR dependency; the Failed pill is honest feedback and matches the mockup. OCR (Tesseract `eng`+`nld`) is a clean future enhancement - purely a pipeline change, no schema impact - if scanned docs become part of the corpus.

## 6. Live ingestion progress

SSE (`.../documents/events`) vs. simple polling. See [REST API -> Documents](rest-api.md#documents-knowledge-panel).

- **Decision:** **Polling `GET /api/projects/{pid}/documents/{id}`.** Zero new plumbing, stateless, adequate at ~20 users with infrequent uploads. The SSE `.../documents/events` stream is deferred - a clean additive upgrade if the live counter proves worth the in-process broadcaster.

## 7. Project model & isolation

How a user's data is organised: one flat store, or scoped workspaces? See [Configuration -> projects](configuration.md#2-projects).

- **Decision:** **Per-project isolation, "default" not "global".** Each project is its own folder with its own `chat.db` + `rag.db`, fully isolated - no cross-project search, no shared/global corpus. The initial, always-present project is **`default`** (the landing project); creating a project just makes another isolated folder. This nests [principle #2](principles.md) (filesystem isolation) and [principle #5](principles.md) (deletion drops a store's data, not a row) one level down. *Rejected:* a "global + local" blended scope - it reintroduced cross-corpus query fusion and a `use_global` flag for little gain over simply switching projects.



## 8. Storage backend seam - everything non-database through IObjectStore

Where do the non-database bytes under a user's tree (uploads) live: direct file I/O in the adapter, or one storage-backend seam?

- **Decision:** **`IObjectStore` is the single storage-backend seam - every byte under a user's tree that is not a database file flows through it**: uploads (`files/...`). One seam means an S3/Azure-Blob backend is a drop-in swap. (Structured user state is **not** blob territory - it lives in `user.db`, [section 9](#9-userdb---structured-user-state-is-a-database-not-json-sidecars).)
  - **Scopes:** an `ObjectScope` is the user root or one project root; keys are scope-relative and traversal-guarded. The scope carries only the opaque `sha256(iss+sub)` key (derivation = `StorageKeys`, core policy in `Gert.Service`).
  - **Atomic PUT is a port contract** - a reader never observes a partial object. Cloud backends give it natively; `LocalObjectStore` stages to a temp sibling + renames.
  - **Lifecycle is independent stores, orchestrated by the service.** Deleting a user/project is not one store's job: the **service** drops the database halves (the providers' `DeleteUserAsync` / `DeleteProjectAsync` - the structured-database and RAG engines each removing their own files/rows) and then the artifact half (`DeleteScopeAsync`), in that order so a local whole-tree wipe never races an open db handle. "Emptied, never removed" = the providers' project delete + `DeletePrefixAsync("")`; the admin scan = `ListUserKeysAsync` + `ListEntriesAsync` (maps 1:1 to S3 listing). `IObjectStore` knows nothing about databases - it owns only the artifact bytes.
  - **Databases are NOT objects.** `chat.db`/`rag.db` need real local file handles (WAL/mmap) and stay with the database providers, which own destroying their own data: a file-backed engine drops its pooled handles + unlinks its db files, a server-backed engine deletes the user/project rows; the storage layer never references `Gert.Database`. *Consequence:* a remote object backend paired with SQLite is a split deployment (objects remote, dbs local) - delete/export compose both stores; the full remote-storage payoff arrives together with a server database (`Gert.Database.Postgres`).
  - **Backends:** `Gert.Storage.LocalObjectStore` today; S3/Azure Blob = a sibling `Gert.Storage.*` project + one DI swap - it is written purely against `IObjectStore` and never changes when the backend does.

## 9. user.db - structured user state is a database, not JSON sidecars

Where do the username (admin scan), user settings, and per-project config live: small JSON
files on the object store, or a database?

- **Decision:** **A third per-user database - `user.db` at the user root** - holds all
  structured user state: a single-row `user_meta` (username, refreshed from the token when it
  changes), a single-row `settings` (the `UserSettings` record as one JSON blob, so the column
  set never tracks the shape field-by-field), and the **`projects` registry** (one row per
  project: name, description, instructions, defaults). Schema:
  [storage-and-data section user.db](storage-and-data.md#userdb).
  - **Why:** transactional and torn-write-proof by construction - the atomic-PUT/healing dance
    a JSON sidecar needs ([section 8](#8-storage-backend-seam---everything-non-database-through-iobjectstore),
    [section 3](#3-user-key)) simply disappears; reads/writes are queryable and migratable
    (`PRAGMA user_version`, `Migrations/user/*.sql`); and provisioning collapses to "open the
    database" - first open creates and migrates it, the provisioner seeds the `default`
    project row, and the steady-state request path stays read-only.
  - **Consequences:** `IObjectStore` is demoted to genuine blobs (uploads) plus
    the artifact half of the lifecycle and the admin footprint listing. The provider seam
    splits per database - `IUserDatabaseProvider` / `IChatDatabaseProvider` (and the RAG index
    is its own capability, `Gert.Rag.IRagIndexProvider`) - and each owns destroying its own data
    (`DeleteUserAsync` / `DeleteProjectAsync`); the service orchestrates the db + blob deletes. The admin scan opens
    each folder's `user.db` for the username.
  - **Unchanged:** everything stays inside the user's folder, so principle #1 (the API owns
    nothing persistent of its own) and principle #5 (deletion drops a store's data wholesale)
    hold exactly as before; the project *data* boundary is still the project folder
    ([configuration section 2](configuration.md#2-projects)).
  - *Rejected:* keeping sidecars on `IObjectStore` (atomic-rename semantics papered over torn
    writes but left config unqueryable and the healing path alive); merging this state into a
    project `chat.db` (wrong scope - it's user-level, and the registry must outlive any
    project).

## 10. Tool entitlement - the JWT is the sole source, no default grant

When a token carries no `gert_tools` claim, what tools does the user get: a configured
server-side default set, or nothing?

- **Decision:** **Nothing - the `gert_tools` claim is the only source of tool entitlement.**
  An absent or blank claim grants **zero tools** (fail-closed); `"*"` grants the whole
  registry; a delimited scope string grants exactly its ids intersect the registry. There is no
  `Tools:DefaultGrant` config and no `ToolOptions` ([auth section tool entitlements](auth.md#tool-entitlements-allowed-tools-in-the-jwt)).
  - **Why:** one rule with no exceptions. The previous design had a configured default set
    *plus* a `sandbox` carve-out - two places to reason about, and a silent path where a
    bare token gained capability the admin never typed. Anchoring entitlement entirely on
    the signed claim makes "what can this user do?" answerable from the token alone, matches
    the fail-closed posture of [principle #6](principles.md), and keeps Gert stateless (no
    server config participates in an authorization decision).
  - **Consequences:** a bare login with no claim is a working chat with no tools (plain
    completion). Admins grant capability explicitly - e.g. a `gert-tools` group granting
    `rag search todo clock make_artifact edit_artifact read_artifact list_artifacts` and a separate
    `gert-sandbox` group adding `sandbox`. `ToolRegistry` still intersects every grant, so a
    typo'd id fails closed rather than erroring the login.
  - *Rejected:* a configured default grant (convenient, but it's server state in an authz
    decision and reintroduces the "absent claim silently grants X" path); making the default
    *explicit-but-present* (still an exception to the one-source rule - the objection was the
    exception itself, not its visibility).

## 11. Turn execution - a global concurrency cap over an atomic per-conversation gate

How are queued `TurnJob`s executed: one global serial consumer, or per-conversation keyed
parallelism? See [chat-and-tools section detached turns](chat-and-tools.md#detached-turns).

- **Decision (settled):** **an engine-enforced per-conversation gate plus a bounded global
  concurrency cap.**
  - **(a) The atomic gate.** A partial unique index
    `ux_messages_streaming ON messages(conversation_id) WHERE status='streaming'` makes the
    streaming-placeholder insert itself the gate: the planner persists the user row + the
    placeholder in one IMMEDIATE transaction (`IChatRepository.TryInsertTurnMessagesAsync`),
    and a losing concurrent plan gets the constraint violation, surfaced as
    `TurnInProgressException` -> 409 - no TOCTOU window, with a read check kept only as
    a fast path. The planner also **writes back expired placeholders**
    (`TryExpireStreamingMessageAsync`, `streaming -> error`, conditional so a finalised row is
    never clobbered): the orphan rule is a real write, not just a lazy read-side mapping, so
    a dead turn's row frees the index instead of locking the conversation forever. Both
    invariants - the **seq single-writer invariant** and the **409 rule** - are explicit
    controls in the database, not properties of a serial worker.
  - **(b) A global concurrency cap.** The `TurnLauncher` runs each planned turn on a TPL Dataflow
    `ActionBlock` and bounds how many run at once with the block's `MaxDegreeOfParallelism`
    (`Gert:Turn:MaxConcurrentTurns`, default 4). Per-conversation serialization is
    the gate index's job, not the launcher's: the gate already admits at most one live turn per
    conversation (a second is 409'd at plan time), so per-conversation FIFO lanes were redundant
    alongside it - the launcher carries no `TurnKey` sharding. `1` reproduces a global serial
    worker. The gate is the correctness control; the cap is only throughput. (This superseded
    the original sharded keyed-lane `ChannelTurnQueue` + `TurnWorker`, which serialized per
    conversation in vain alongside the gate.)
  - **SQLite under two lanes:** the only genuinely new concurrency is *same user, same
    project, two conversations* hitting one `chat.db` (per-user/per-project databases make
    everything else disjoint). Open-per-use connections with WAL + `busy_timeout=5000` mean
    readers never block and writers queue up to 5 s; every write is a short single statement
    except the gate transaction (two INSERTs under `BEGIN IMMEDIATE` - the write lock is
    taken up front precisely so it cannot upgrade-deadlock). Pathological contention
    surfaces as `SQLITE_BUSY` after 5 s -> the runner's catch-all finalises that turn as
    `error`: fail-closed and visible. Adequate at this scale; no pragma changes.
  - **The two turn timers still share one anchor:** `TurnJob.PlannedAt` (= the placeholder
    row's `CreatedAt`, one clock read in the planner). The runner budgets only the
    `MaxTurnDuration` *remaining* from that instant, so a job that waited behind its lane
    can never outlive the reader-facing orphan/409 horizon and read as `error` while
    healthily running.
  - *Rejected:* per-conversation FIFO lanes (the sharded `ChannelTurnQueue` + `TurnWorker` the
    cap superseded) - the gate index already serializes a conversation, so the lanes closed a
    race nobody can hit and only added a `TurnKey`-hash sharding layer; an unbounded `Task.Run`
    per turn with no cap (no concurrency ceiling, no shutdown owner); parallelising without the
    gate (a duplicate streaming turn corrupts seq ordering and history - fail-closed loses).

## 12. Deletion crash-consistency - a journal + idempotent forward recovery

Erasing a user spans three independent stores - the structured-database engine
(`user.db`/`chat.db`), the RAG engine (`rag.db`), and the object store (file blobs) -
which may even sit on separate roots ([configuration -> data root](../installation/configuration.md#8-auth--storage--gertdatabase--gertrag---identity-the-data-root-and-the-engines)).
A crash between steps would leave a partial state, worst case **blobs (PII) left on disk after a
"delete my account" the operator believed finished**. See
[Operations -> User lifecycle](operations.md#user-lifecycle---remove-a-user).

- **Decision:** **A write-ahead deletion journal + idempotent forward recovery (a saga, not a
  transaction).** True ACID across a filesystem and a blob store isn't reachable, and a
  distributed-transaction coordinator is far too much machinery for ~20 users. Instead the erase
  is one guarded path (`IUserDataEraser`): **mark** the user owed in the `IDeletionJournal`
  *before* touching any store, drop each store's data (database halves before blobs, every step
  idempotent), then **clear** the mark *last* - only once every store is confirmed gone. A crash
  anywhere leaves the mark set; replaying the idempotent erase converges to fully-deleted. Delete
  has no meaningful undo, so recovery only ever rolls **forward**.
  - **Where the mark lives:** one empty marker per owed key under
    `{Storage:DataRoot}/.pending-deletions/{key}` (`LocalDeletionJournal`) - a **sibling of
    `users/`**, so the user-tree wipe never takes it and it never shows up in the admin scan. It
    holds only opaque, transient folder keys (no user data), so it is operational recovery state,
    not a central user registry - [principle #1](principles.md) still holds.
  - **Who replays it:** the `DeletionRecoveryService` hosted task (a service-layer concern,
    registered by `AddGertServices`) on **startup** (covers a process
    crash/restart) and the request-edge **provisioner gate** (covers a returning user whose
    self-service delete was interrupted - it finishes the erase *before* re-provisioning, so a
    fresh empty account never inherits stale residue). Both just re-run the eraser.
  - **One erase path:** self-service account delete (`AccountService`), admin delete
    (`AdminService`), and recovery all call the same `IUserDataEraser.EraseAsync(key)`, so the
    guard and the db-before-blob ordering live in exactly one place.
  - *Rejected:* a **trash-rename + reaper** (near-atomic *logical* delete locally, but `rename`
    isn't atomic across roots and degrades to copy+delete on a remote object store - it breaks
    exactly where the per-engine-root / remote-storage design is headed); **retry-only with no
    journal** (relies on a human re-issuing the delete and silently orphans an abandoned partial).
    Project deletion has the same shape and can adopt the same journal when needed; account
    deletion carries the PII-residue stakes, so it goes first.

## 13. Model API on Microsoft.Extensions.AI - scrap the custom wire layer

Gert maintained a hand-rolled model-wire abstraction (`IChatModelClient`/`ChatModelChunk`/
`ChatCompletionRequest`/`ChatModelMessage`/`ChatToolSpec`/`ChatModelToolCall`/`ToolCallStart`/
`ChatModelImage`, `IEmbeddingClient`, `OpenAIChatRequestBuilder`, the `OpenAIStreamParser` feeding
those DTOs) on top of the OpenAI SDK. The first-party **Microsoft.Extensions.AI** stack
(`IChatClient`, `IEmbeddingGenerator`, `AIFunction`) now covers the wire translation we were
maintaining by hand. Should we adopt it, and how far? See
[tech-stack section Model API](tech-stack.md#tech-stack), [chat-and-tools section the tool loop](chat-and-tools.md#chat-orchestration-the-tool-loop).

- **Decision:** **Adopt M.E.AI at the chat + embeddings ports; delete the custom wire DTOs; keep
  the Gert-specific behavior M.E.AI has no analog for, re-homed into thin wrappers.**
  `IChatClientFactory`/`IChatModelClientBuilder` now return a M.E.AI `IChatClient`; the embeddings
  port is `IEmbeddingGenerator<string, Embedding<float>>`. `IChatClient`/`ChatMessage`/`ChatOptions`/
  `ChatResponseUpdate`/`AIFunction` flow directly into the agent loop - **not** hidden under the old
  port (the cheap "wrap underneath" path was explicitly rejected).
  - **Versions.** `Microsoft.Extensions.AI{,.Abstractions,.OpenAI}` are pinned at **10.7.0**, which
    is **stable** (the OpenAI adapter is no longer preview, contra older guidance) and requires
    `OpenAI >= 2.11.0` - exactly our existing pin. The AI packages run their own 10.x cadence,
    distinct from the runtime `Microsoft.Extensions.*` 10.0.9 family but interoperating with it
    (they depend on the runtime extensions at `>= 10.0.9`). Abstractions is contracts-safe (in
    `Gert.Chat`); AI core + AI.OpenAI carry the SDK and live only in the `Gert.Chat.OpenAI` leaf;
    an architecture test keeps the OpenAI adapter out of `Gert.Agent`/`Gert.Service`.
  - **Keep (no M.E.AI analog), re-homed into `SalvagingChatClient`** (a `DelegatingChatClient` over
    `chatClient.AsIChatClient()` in `Gert.Chat.OpenAI`): the `<tool_call>` leak salvage, the vLLM
    `reasoning`/`reasoning_content` extraction (the adapter surfaces only the latter), the
    truncated-argument degrade-to-`{}` guard, the name-first live-intent signal, the provider's
    sampling, and the off-spec vendor fields (`top_k`/`min_p`/`repetition_penalty`/
    `chat_template_kwargs`) via `ChatOptions.RawRepresentationFactory` seeding an OpenAI SDK
    `ChatCompletionOptions` + JsonPatch. Interleaved-thinking replay (`preserve_thinking`) rides a
    native `AssistantChatMessage` on the message's `RawRepresentation` - the one thing the adapter
    cannot express. Embeddings keep order-by-index reassembly + the dimension/count assertions
    (M.E.AI's `GeneratedEmbeddings` drops the source index), so `OpenAIEmbeddingGenerator` runs over
    the SDK `EmbeddingClient` directly rather than `.AsIEmbeddingGenerator()`.
  - **Keep the multi-provider catalog + the keyed plugin seam.** "Scrap the wire layer" is the wire,
    not multi-provider config: `IChatProviderCatalog`/`ConfigChatProviderCatalog`/`ChatProviderOptions`/
    `IDefaultChatProvider` and the `IChatModelClientBuilder` keyed-by-`Type` plugin stay (the
    thinking-vs-instruct provider selection rides them; `PluginArchitectureTests` pins the seam).
  - **Tools become `AIFunction`s at the advertise boundary, side-effects via a host card seam.**
    Each offered tool is advertised as a lean `ToolFunction : AIFunction` carrying the tool's OWN
    compact `ToolSchema` output verbatim (the tools region is a token budget - qwen format adherence
    collapses past ~1.8k tokens, so the verbose schema `AIFunctionFactory.CreateDeclaration` would
    synthesise is avoided). A tool's citations/artifacts/stdout/todos are pushed through a new
    `IToolHost.Card` (`IToolCard`) seam (impl in `Gert.Agent`) instead of riding `ToolResult`, which
    slims to `{Success, ResultJson, Error}` - "intelligence into the tool."
  - **Kept the M.E.AI-native `AgentLoop`; did NOT convert it to a `FunctionInvokingChatClient`
    subclass.** The original plan called for the loop to become a FICC subclass, but FICC drives the
    model loop with a single fixed `ChatOptions` across iterations and exposes no per-iteration
    request hook, so it cannot reproduce Gert's cited wind-down brake - the refused round keeps tools
    advertised, the wind-down round clears them, and a still-calling wind-down round stops with the
    streamed content (`Runaway_tool_loop_is_bounded`: 5 executed + 1 refused + 1 wind-down = 7 model
    calls, with the per-round tool advertisement). Fully overriding FICC's loop to restore that is
    just the loop we already have. Movement A already made the loop M.E.AI-native (it consumes
    `IChatClient`/`ChatResponseUpdate`, collects `FunctionCallContent`, builds a `ChatMessage`
    history, and preserves entitlement/budgets/timeout/wind-down/live-intent/metrics/narration-rides-back),
    so the substance and the test gates of the FICC step are met without the form that would regress
    a cited invariant ([principle #6](principles.md), the "claim is the ceiling" entitlement re-check
    must not be weakened).
  - *Rejected:* **wrapping M.E.AI underneath the old `IChatModelClient`** (keeps the abstraction we
    were asked to scrap, and the dead translation layer); **`AIFunctionFactory.CreateDeclaration`
    for the advertised schemas** (it synthesises a verbose, pretty-printed schema with strict-mode
    `additionalProperties` that bloats the qwen tools budget - the lean `ToolFunction` schema rides
    the wire compact, the adapter adding only a bounded `additionalProperties:false` per tool);
    **the `FunctionInvokingChatClient` subclass** (above - it cannot preserve the wind-down
    call-count invariant).
