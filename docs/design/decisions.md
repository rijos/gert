# Design decisions

The decision record: choices that shaped the system, each with the question it answered, the
**Decision**, and the why — including the alternatives that were rejected and what would have
to change to revisit one. All entries below are settled; new decisions append here. Questions
still open live with their owning doc ([configuration §9](configuration.md#9-open-decisions),
[turn-budgets](turn-budgets.md)) until they're settled enough to record.

## 1. Embedding model + dimension

Bakes into the `vec0` table. Source: vLLM embeddings endpoint vs. a separate service. See [Operations → Cross-cutting (Embeddings)](operations.md#cross-cutting-concerns).

- **Decision:** **bge-m3, 1024-dim, served by vLLM** (`--task embed`, OpenAI-compatible `/v1/embeddings`). Chosen for multilingual retrieval (incl. Dutch) and 8k context; keeps the embedding service count at one. `vec_chunks.embedding` is `FLOAT[1024]`.

## 2. One DB vs two per project

Split (recommended) or merged `gert.db`. See [Per-user storage → Why two databases](storage-and-data.md#why-two-databases-per-project).

- **Decision:** **Two databases — `chat.db` + `rag.db`, one pair per project.** Lets "forget my documents" wipe a project's RAG without touching its chat history, and keeps vector-index rebuilds/`VACUUM` off the chat write path. The extra wiring is negligible at ~20 users. (The pair lives *inside* each project folder — see [decision §7](#7-project-model--isolation).)

## 3. Folder key

`sha256(iss + sub)` (recommended) vs. raw `sub` vs. email. See [Per-user storage → Resolving paths](storage-and-data.md#resolving-paths).

- **Decision:** **`sha256(iss + "\n" + sub)` lowercase hex**, with a fail-closed provisioning gate; what's on disk is descriptive, never a gate. *(The descriptive record has since moved from a `meta.json` sidecar into `user.db` — [§9](#9-userdb--structured-user-state-is-a-database-not-json-sidecars); the anchoring rationale below is unchanged.)*
  - **Anchor on `sub`, not email or username.** `sub` is Pocket ID's stable, opaque UUID — *never renamed, never recycled*. Email is **mutable** (a rename orphans the folder) and **recycled** (a reassigned address would inherit the prior owner's data — the exact reuse attack we want to avoid); username is renamed. `sub` is also no less trusted: every claim in a signature-validated JWT is equally trusted, `sub` is just the most stable.
  - **Namespace by issuer.** `sub` is only unique *within* an issuer, so the key hashes `iss + "\n" + sub`. With one IdP today this is moot; it makes a second IdP collision-proof for free.
  - **Hashing** gives a fixed-length, path-safe, traversal-proof folder name for any value the IdP emits; the opaque name is covered by the stored username (`user.db`) and `GET /api/admin/users` for key→user mapping.
  - **Collision is not the threat** — `sha256` makes cross-user collision infeasible regardless of input; the real risk is *identifier reuse*, addressed by anchoring on `sub` (above).
  - **Validate before touching disk** ([principle #6](principles.md)): provisioning asserts `iss` == configured authority, `aud` matches, and `sub` is present + within a bounded charset/length **before** any path-derive or `mkdir`. No well-formed identity → no folder.
  - **Trust the validated JWT past the gate** ([security F12](security.md#3-findings--remediations)): the folder key derives from the token and nothing else, so no per-request disk-side re-check exists. The username stored in `user.db` is purely descriptive — key→user mapping for `GET /api/admin/users`, refreshed from the token when it changes — and each database's `PRAGMA user_version` anchors migrations ([§9](#9-userdb--structured-user-state-is-a-database-not-json-sidecars)).

## 4. Token lifetime / revocation

Strategy for immediate off-boarding. See [Operations → User lifecycle](operations.md#user-lifecycle--remove-a-user--remove-a-folder).

- **Decision:** **Accept Pocket ID's ~1-hour access token as the revocation window; no denylist.** Pocket ID does not support a short, independently-configurable access-token lifetime ([issue #792](https://github.com/pocket-id/pocket-id/issues/792), closed *not planned*), so the 10–15 min approach from the Authentik-era draft isn't available. Refresh tokens (with rotation) still drive the SPA's silent renewal. Off-boarding = deactivate in Pocket ID, effective within ~1h. **We deliberately do *not* add a `sub`-denylist:** it is shared, mutable, per-instance auth state that would break running **multiple GERT instances** (a denied `sub` on instance A wouldn't be known to instance B without a shared store), defeating horizontal scaling. GERT stays **stateless** — every instance validates a token purely from the JWT + Pocket ID's JWKS, nothing shared. If sub-hour revocation ever becomes a hard requirement, the right answer is a shorter token lifetime at the IdP (or a shared-store check introduced explicitly), not in-process state.

## 5. OCR

Should scanned-only PDFs (`old-scan.pdf`) be OCR'd (e.g. Tesseract) instead of failing, or is "no extractable text → Failed" the intended behavior?

- **Decision:** **Fail fast — "no extractable text → Failed".** No OCR dependency; the Failed pill is honest feedback and matches the mockup. OCR (Tesseract `eng`+`nld`) is a clean future enhancement — purely a pipeline change, no schema impact — if scanned docs become part of the corpus.

## 6. Live ingestion progress

SSE (`…/documents/events`) vs. simple polling. See [REST API → Documents](rest-api.md#documents-knowledge-panel).

- **Decision:** **Polling `GET /api/projects/{pid}/documents/{id}`.** Zero new plumbing, stateless, adequate at ~20 users with infrequent uploads. The SSE `…/documents/events` stream is deferred — a clean additive upgrade if the live counter proves worth the in-process broadcaster.

## 7. Project model & isolation

How a user's data is organised: one flat store, or scoped workspaces? See [Configuration → projects](configuration.md#2-projects).

- **Decision:** **Per-project isolation, "default" not "global".** Each project is its own folder with its own `chat.db` + `rag.db` + memory, fully isolated — no cross-project search, no shared/global corpus. The initial, always-present project is **`default`** (the landing project); creating a project just makes another isolated folder. This nests [principle #2](principles.md) (filesystem isolation) and [principle #5](principles.md) (deletion is `rm -rf`) one level down. *Rejected:* a "global + local" blended scope — it reintroduced cross-corpus query fusion and a `use_global` flag for little gain over simply switching projects.



## 8. Storage backend seam — everything non-database through IObjectStore

Where do config sidecars (`meta.json`, `settings.json`, `projects/{pid}/meta.json`) live: direct file I/O, or the object-store seam? (Supersedes the earlier "config files are direct file I/O in the adapter" stance.)

- **Decision:** **`IObjectStore` is the single storage-backend seam — every byte under a user's tree that is not a database file flows through it**: uploads (`files/…`), memory bodies (`memory/…`), and the JSON config sidecars alike. A config file is just a small object; treating it specially bought nothing once S3/Azure-Blob backends were on the table.
- **Amended by [§9](#9-userdb--structured-user-state-is-a-database-not-json-sidecars):** the JSON config sidecars no longer exist — structured user state moved into `user.db`. `IObjectStore` remains the seam for the *genuine* blobs (uploads, memory bodies) and the coarse scope lifecycle (`DeleteScopeAsync` = the `rm -rf`; the admin footprint listing); everything else below still stands.
  - **Scopes:** an `ObjectScope` is the user root or one project root; keys are scope-relative and traversal-guarded. The scope carries only the opaque `sha256(iss+sub)` key (derivation = `StorageKeys`, core policy in `Gert.Service`).
  - **Atomic PUT is a port contract** — a reader never observes a partial object. Cloud backends give it natively; `LocalObjectStore` stages to a temp sibling + renames. This retires the truncated-`meta.json` failure class at the storage layer.
  - **Lifecycle = scope ops:** delete user/project = `DeleteScopeAsync` (the `rm -rf` of principle #5); "emptied, never removed" = `DeletePrefixAsync("")`; the admin scan = `ListUserKeysAsync` + `ListEntriesAsync` (maps 1:1 to S3 listing).
  - **Databases are NOT objects.** `chat.db`/`rag.db` need real local file handles (WAL/mmap) and stay with `IDatabaseProvider`; local whole-tree deletes release pooled handles via the `IDatabaseHandleReleaser` port. *Consequence:* a remote object backend paired with SQLite is a split deployment (objects remote, dbs local) — delete/export compose both stores; the full remote-storage payoff arrives together with a server database (`Gert.Database.Postgres`).
  - **Backends:** `Gert.Storage.LocalObjectStore` today; S3/Azure Blob = a sibling `Gert.Storage.*` project + one DI swap. `ObjectStoreUserStore` (the `IUserStore` impl) is written purely against the port and never changes with the backend.

## 9. user.db — structured user state is a database, not JSON sidecars

Where do the username (admin scan), user settings, and per-project config live: JSON sidecar
files (`meta.json`, `settings.json`, `projects/{pid}/meta.json`) on the object store, or a
database? (Amends the sidecar half of [§8](#8-storage-backend-seam--everything-non-database-through-iobjectstore).)

- **Decision:** **A third per-user database — `user.db` at the user root** — holds all
  structured user state: a single-row `user_meta` (username, refreshed from the token when it
  changes), a single-row `settings` (the `UserSettings` record as one JSON blob, so the column
  set never tracks the shape field-by-field), and the **`projects` registry** (one row per
  project: name, description, instructions, defaults). Schema:
  [storage-and-data § user.db](storage-and-data.md#userdb).
  - **Why:** transactional and torn-write-proof by construction — the atomic-PUT/healing dance
    a JSON sidecar needs ([§8](#8-storage-backend-seam--everything-non-database-through-iobjectstore),
    [§3](#3-folder-key)) simply disappears; reads/writes are queryable and migratable
    (`PRAGMA user_version`, `Migrations/user/*.sql`); and provisioning collapses to "open the
    database" — first open creates and migrates it, the provisioner seeds the `default`
    project row, and the steady-state request path stays read-only.
  - **Consequences:** `IObjectStore` is demoted to genuine blobs (uploads, memory bodies) plus
    scope lifecycle and the admin footprint listing; `IUserStore` no longer carries config.
    The provider seam splits per database — `IUserDatabaseProvider` / `IChatDatabaseProvider` /
    `IRagDatabaseProvider` — and the admin scan opens each folder's `user.db` for the username.
  - **Unchanged:** everything stays inside the user's folder, so principle #1 (the API owns
    nothing persistent of its own) and principle #5 (deletion is `rm -rf`) hold exactly as
    before; the project *data* boundary is still the project folder
    ([configuration §2](configuration.md#2-projects)).
  - *Rejected:* keeping sidecars on `IObjectStore` (atomic-rename semantics papered over torn
    writes but left config unqueryable and the healing path alive); merging this state into a
    project `chat.db` (wrong scope — it's user-level, and the registry must outlive any
    project).

## 10. Tool entitlement — the JWT is the sole source, no default grant

When a token carries no `gert_tools` claim, what tools does the user get: a configured
server-side default set, or nothing?

- **Decision:** **Nothing — the `gert_tools` claim is the only source of tool entitlement.**
  An absent or blank claim grants **zero tools** (fail-closed); `"*"` grants the whole
  registry; a delimited scope string grants exactly its ids ∩ the registry. There is no
  `Tools:DefaultGrant` config and no `ToolOptions` ([auth § tool entitlements](auth.md#tool-entitlements-allowed-tools-in-the-jwt)).
  - **Why:** one rule with no exceptions. The previous design had a configured default set
    *plus* a `sandbox` carve-out — two places to reason about, and a silent path where a
    bare token gained capability the admin never typed. Anchoring entitlement entirely on
    the signed claim makes "what can this user do?" answerable from the token alone, matches
    the fail-closed posture of [principle #6](principles.md), and keeps Gert stateless (no
    server config participates in an authorization decision).
  - **Consequences:** a bare login with no claim is a working chat with no tools (plain
    completion). Admins grant capability explicitly — e.g. a `gert-tools` group granting
    `rag search todo clock make_artifact edit_artifact read_artifact` and a separate
    `gert-sandbox` group adding `sandbox`. The Console host is unaffected (its
    `LocalUserContext` hardcodes `"*"`). `ToolRegistry` still intersects every grant, so a
    typo'd id fails closed rather than erroring the login.
  - *Rejected:* a configured default grant (convenient, but it's server state in an authz
    decision and reintroduces the "absent claim silently grants X" path); making the default
    *explicit-but-present* (still an exception to the one-source rule — the objection was the
    exception itself, not its visibility).

## 11. Turn execution — keyed lanes over an atomic per-conversation gate

How are queued `TurnJob`s executed: one global serial consumer, or per-conversation keyed
parallelism? See [chat-and-tools § detached turns](chat-and-tools.md#detached-turns).

- **Decision (settled — [strengthening-plan S8](strengthening-plan.md#s8--keyed-turn-parallelism-with-an-atomic-409-gate-one-combined-change),
  shipped as one combined change):** **an engine-enforced per-conversation gate plus bounded
  keyed parallelism.**
  - **(a) The atomic gate.** A partial unique index
    `ux_messages_streaming ON messages(conversation_id) WHERE status='streaming'` makes the
    streaming-placeholder insert itself the gate: the planner persists the user row + the
    placeholder in one IMMEDIATE transaction (`IChatRepository.TryInsertTurnMessagesAsync`),
    and a losing concurrent plan gets the constraint violation, surfaced as
    `TurnInProgressException` → 409 — no TOCTOU window, with the old read check kept only as
    a fast path. The planner also **writes back expired placeholders**
    (`TryExpireStreamingMessageAsync`, `streaming → error`, conditional so a finalised row is
    never clobbered): the orphan rule's lazy read-side mapping became a real write, so a dead
    turn's row frees the index instead of locking the conversation forever. Both invariants
    that used to lean on the serial worker — the **seq single-writer invariant** and the
    **409 rule** — are now explicit controls in the database.
  - **(b) Keyed lanes.** The one `TurnWorker` hosted service drains
    `Gert:Turn:MaxConcurrentTurns` (default 4) internal lanes of the sharded
    `ChannelTurnQueue`; jobs shard by the full `TurnKey` (iss, sub, pid, conversation) hash,
    so one conversation's turns ride one lane in strict FIFO — per-conversation ordering is
    structural, not timing — while different conversations may overlap. `1` reproduces the
    old global serial worker exactly. The gate is the correctness control; the lane count is
    only throughput.
  - **SQLite under two lanes:** the only genuinely new concurrency is *same user, same
    project, two conversations* hitting one `chat.db` (per-user/per-project databases make
    everything else disjoint). Open-per-use connections with WAL + `busy_timeout=5000` mean
    readers never block and writers queue up to 5 s; every write is a short single statement
    except the gate transaction (two INSERTs under `BEGIN IMMEDIATE` — the write lock is
    taken up front precisely so it cannot upgrade-deadlock). Pathological contention
    surfaces as `SQLITE_BUSY` after 5 s → the runner's catch-all finalises that turn as
    `error`: fail-closed and visible. Adequate at this scale; no pragma changes.
  - **The two turn timers still share one anchor:** `TurnJob.PlannedAt` (= the placeholder
    row's `CreatedAt`, one clock read in the planner). The runner budgets only the
    `MaxTurnDuration` *remaining* from that instant, so a job that waited behind its lane
    can never outlive the reader-facing orphan/409 horizon and read as `error` while
    healthily running.
  - *Rejected:* unbounded `Task.Run` per turn (no ordering, no owner — violates the
    worker-owns-detached-work rule in the style guide); a channel per conversation created
    eagerly (unbounded channel count, no backpressure story); N hosted services instead of N
    lanes (DI churn, N owners for one queue — the style guide wants one worker owning the
    detached work); parallelising without the gate (a duplicate streaming turn corrupts seq
    ordering and history — fail-closed loses), or shipping the gate without the lanes (closes
    a race nobody can hit and changes nothing user-visible).
