# Strengthening plan

**Status: active.** The post-v1 hardening round, from the 2026-06-10 architecture review.
Dependency-ordered units in the same shape that built the system (goal / depends / touches /
design / acceptance). House rules
still apply: test-first where the gate is behavioural, build the named control as part of
the unit, **update the owning design doc in the same change** (the
[link gate](README.md#how-this-folder-works) holds you to it), and stop-and-surface on any
design ambiguity. When every unit lands, this file is deleted — git history keeps it.

| Unit | Title | Status |
|------|-------|:------:|
| S1 | Turn heartbeat — shrink the orphan horizon | ⬜ |
| S2a | Steering instead of 409 — backend | ⬜ |
| S2b | Steering — SPA | ⬜ |
| S3 | Dedicated embeddings endpoint (required) | ⬜ |
| S4 | `turn_events` pruning | ⬜ |
| S5 | Multi-instance topology — decide & document | ⬜ |
| S6 | Context compaction (design → phases) | ⬜ |
| S7 | Project import / restore | ⬜ |
| S8 | Keyed turn parallelism with an atomic 409 gate (one combined change) | ✅ |

Safe concurrent tracks: **S1 → S2a → S2b** is the spine; **S3, S4, S5, S7** are independent
of it and of each other; **S6a** can start once [context-compaction.md](context-compaction.md)
is settled. **S8 rode the spine too** — it edits the same planner/worker/rules files as S1
and S2a. *(Deviation, orchestrator-approved: S8 landed first, against today's code — see the
note in its section below.)* Use worktrees for parallel units.

---

### S1 — Turn heartbeat (shrink the orphan horizon)

- **Goal:** A crashed/killed worker currently leaves the conversation 409-blocked until
  `MaxTurnDuration` — minutes of lockout. Make liveness explicit: the runner heartbeats the
  streaming assistant row; readers treat a **stale heartbeat**, not just old age, as dead.
- **Depends:** —
- **Touches:** `Migrations/chat/005_heartbeat.sql` (`messages.heartbeat_at TEXT NULL`),
  `TurnRunner` (a periodic touch every `Gert:Turn:HeartbeatInterval`, default 10 s,
  independent of event flushes so silent rounds and slow tools don't false-trip),
  `MessageStatusRules` (streaming → error when
  `now − coalesce(heartbeat_at, created_at) > Gert:Turn:StaleTurnAfter`, default 45 s;
  `MaxTurnDuration` stays as the outer bound), `TurnOptions` (+ validation:
  `StaleTurnAfter ≥ 3 × HeartbeatInterval`), `IChatRepository` (touch method).
- **Design:** [chat-and-tools § detached turns](chat-and-tools.md#detached-turns) (the
  orphan rule), [turn-budgets §2b](turn-budgets.md#2b-what-open-webui-does-surveyed-2026-06-07-open-webuiopen-webuimain)
  (bound every part, visible trips).
- **Caveat — queued jobs emit no heartbeats:** the runner only starts beating once the
  worker dequeues the job, but the placeholder row exists from *plan* time
  (`TurnJob.PlannedAt` = its `CreatedAt`, the shared anchor with the runner's remaining
  budget). A job sitting in the queue longer than `StaleTurnAfter` would read as dead while
  perfectly healthy. S1 must therefore either heartbeat at the enqueue/dequeue boundaries
  (the planner stamp counts as the first beat; the worker touches on dequeue) or treat
  not-yet-started turns distinctly (a null `heartbeat_at` falls back to the `MaxTurnDuration`
  horizon, not `StaleTurnAfter`) — pick one and test the queue-wait case explicitly.
- **Docs to update:** chat-and-tools (orphan rule), [installation §9](../installation/configuration.md#9-gertturn--the-detached-turn-pipeline).
- **Acceptance:** a streaming row with a stale heartbeat reads as `error` and a new
  `POST …/messages` is accepted within `StaleTurnAfter` (not `MaxTurnDuration`); a live turn
  blocked on a 60 s sandbox call (zero events) is **not** killed; planner 409 and all readers
  go through the one rule; rules unit-tested at the boundaries.

### S2a — Steering instead of 409 — backend ([turn-budgets §4c](turn-budgets.md#4c-steering-instead-of-409--proposed-larger))

- **Goal:** A message posted during a live turn is **queued and injected**, not rejected.
  Plan phase: if a turn is live (per S1's rule), validate + persist the user message
  (seq-allocated, durable, replayable) + enqueue onto a per-conversation steering queue +
  respond `202 { queued: true, seq }`. Run phase: the runner **drains the queue at every
  round boundary** — including once more before deciding to finalize — and appends queued
  messages at the prompt **tail**, the same placement discipline the planner's generalized
  tail path established (`ITailReminder` / `AppendTailReminder` —
  [chat-and-tools](chat-and-tools.md#chat-orchestration-the-tool-loop)); reuse that
  rendering helper rather than growing a second tail-formatting path. The single-writer
  invariant holds: the running turn consumes the message; no second turn starts. `cancel`
  stays the hard stop.
- **Depends:** S1 (a steer aimed at an *orphaned* turn must fall through to the normal
  plan-and-enqueue path — the same staleness rule decides which world you're in).
- **Touches:** new `ITurnSteering` seam (per-`TurnKey` queue, sibling of `ITurnCancellation`),
  `TurnPlanner` (replace the 409 branch; keep 409 only when `Gert:Turn:SteeringEnabled` is
  off), `TurnRunner` (drain points + a final pre-finalize drain; emit the queued user
  message's event so attached clients render it mid-turn), `TurnOptions`.
- **Race to close in tests:** steer lands after the final drain but before finalize → the
  planner re-checks liveness after enqueue; if the turn finalized without consuming, it
  starts a normal turn for the already-persisted message. No message may be silently lost
  **or** double-sent.
- **Caveat to verify live:** tail injection × `preserve_thinking` reasoning replay against
  vLLM 0.22 (the [turn-budgets §6](turn-budgets.md#6-open-questions) open question) — a
  `Live_…` test in the established pattern.
- **Note:** the steering queue is in-process, like the bus — fine under S5's sticky-by-user
  topology; the range/replay path stays the cross-instance truth.
- **Docs to update:** rest-api (the messages endpoint: 202-queued semantics replace the 409
  §), chat-and-tools (plan/run phases), turn-budgets (mark 4c shipped).
- **Acceptance:** integration — POST during a streaming turn returns 202, the message rides
  the current turn (snapshot shows tail injection next round), ordering and reload-replay
  are correct; POST against an orphaned turn starts a fresh turn; flag off → old 409.

### S2b — Steering — SPA

- **Goal:** The composer stays enabled while streaming; a steered message renders
  immediately with a "queued" affordance until its event confirms consumption; stop stays
  one click.
- **Depends:** S2a
- **Touches:** `components/main/composer.js`, `message-stream.js`/`message.js`,
  `services/chat.js`, `state/chat.js`.
- **Docs to update:** ui-components (§7 map row), [spa-style-guide](spa-style-guide.md) only
  if a new primitive emerges.
- **Acceptance:** smoke scenario (fixture-driven multi-round completion in the mock vLLM):
  send → while streaming, send again → both bubbles render in order, the reply references
  the steer, reload mid-turn replays identically; matrix stays green.

### S3 — Dedicated embeddings endpoint (required config)

- **Goal:** Embeddings stop riding the chat upstream's config. New **required** section —
  `Gert:Embeddings { BaseUrl, ApiKey?, ModelId, Dimensions }` — validated at startup
  (`ValidateOnStart`: missing `BaseUrl` fails boot with a message naming the key);
  `EmbeddingModelId`/`EmbeddingDimensions` leave `Gert:Vllm`. A second named `HttpClient`
  (own base address, key, resilience pipeline) backs `VllmEmbeddingClient` — today both
  sections point at the same server; splitting them later becomes pure config. Bonus this
  unlocks now: `make serve-mock-vllm` can keep embeddings on the **mock** while chat goes
  real (a chat checkpoint typically doesn't serve `/v1/embeddings` at all).
- **Depends:** —
- **Touches:** `Gert.External` (`EmbeddingsOptions`, `ServiceCollectionExtensions` second
  client, `VllmEmbeddingClient.HttpClientName`), `appsettings.json` + `FakeE2E` profile +
  Console wiring, `tools/smoke` (mock URL into the new key; `serve-mock-vllm` leaves
  embeddings mocked), tests.
- **Docs to update:** [installation/configuration.md](../installation/configuration.md)
  (new `Gert:Embeddings` section; §3 trimmed), [decisions §1](decisions.md#1-embedding-model--dimension)
  (note the endpoint split), [operations § embeddings](operations.md#cross-cutting-concerns),
  [tech-stack](tech-stack.md) table row.
- **Acceptance:** boot fails fast (clear message) when `Gert:Embeddings:BaseUrl` is unset;
  embeddings traffic provably leaves on the second client (handler-stub assertion); full
  suite + smoke green with both sections pointing at one mock; chat-real/embeddings-mock
  works.

### S4 — `turn_events` pruning

- **Goal:** The replay log stops growing without bound. Replay only ever needs the
  in-flight tail (readers resubscribe from the streaming row's `seq`; finished turns are
  served whole by the thread GET) — so on finalize of turn *N*, prune that conversation's
  events from turns ≤ *N−1* (everything below turn N's user-message `seq`). Always-on
  hygiene, no knob.
- **Depends:** —
- **Touches:** `IChatRepository.PruneTurnEventsAsync(conversationId, belowSeq)` +
  SQLite impl, `TurnRunner` finalize (after the terminal event persists), tests.
- **Docs to update:** chat-and-tools (detached turns — the log's retention rule),
  storage-and-data (`turn_events` comment).
- **Acceptance:** mid-turn reload still replays the current turn loss-free; a client
  re-attaching after finalize reconciles via the thread GET; row counts stay bounded across
  many turns (asserted); cascade delete unaffected.

### S5 — Multi-instance topology — decide & document

- **Goal:** Pin what's actually supported before someone discovers it in production:
  **(a)** single instance — the default; **(b)** N instances with **user-sticky routing**
  (proxy keys on the folder key / `sub`-hash, so a user's folder, bus, steering queue, and
  SQLite writers all live on one instance — per-user, *not* per-browser-session, because two
  devices of one user must land together); **shared `/data` over network filesystems is
  explicitly unsupported with SQLite** (WAL + remote locking); true shared-nothing
  horizontal scale arrives with `Gert.Database.Postgres`. Recorded as a decision, not
  folklore.
- **Depends:** — (informs S2a's queue-locality note)
- **Touches:** docs only.
- **Docs to update:** [decisions.md](decisions.md) (new §9 entry), [operations.md](operations.md)
  (deployment bullet), [tech-stack § engine portability](tech-stack.md#engine-portability)
  (one cross-link).
- **Acceptance:** `make check-links` green; the decision names the rejected alternative
  (shared-FS SQLite) and the revisit trigger (Postgres backend).

### S6 — Context compaction

- **Goal:** Execute [context-compaction.md](context-compaction.md) once its open questions
  are settled. Phases as recommended there: **S6a** bound + visible trip (`context_exhausted`
  event instead of an upstream 400 mid-stream), **S6b** cheap elision (reasoning-replay trim
  first, then old image attachments — note the canvas tool suite already keeps model-authored
  *files* out of history entirely: tool args never re-enter the prompt and `read_artifact`
  is the recall path), **S6c** auto-compaction (summarize-and-anchor on the conversation row,
  background job, visible divider, per-conversation `off · auto`).
- **Depends:** the design note settled (S6a is safe to start regardless — it's pure guard);
  S6c benefits from S4 (log hygiene) but doesn't require it.
- **Touches (S6c):** `Migrations/chat/006_compaction.sql`
  (`conversations.compaction_summary`, `compacted_upto_seq`, `compacted_at`), planner prompt
  assembly, a compaction worker on the detached-worker pattern, SPA divider, settings
  cascade.
- **Docs to update:** context-compaction.md becomes the implemented spec folded into
  chat-and-tools + configuration; storage-and-data schema; installation §9 knobs.
- **Acceptance:** per phase, defined in the note once settled; S6a's floor: an over-budget
  turn fails *before* any upstream call, with an event naming the limit and the outs.

### S7 — Project import / restore

- **Goal:** Close the export/import asymmetry. Step 1: project export gains a
  `manifest.json` (format version, counts) — additive. Step 2:
  `POST /api/projects/import` (multipart archive → new project) that
  **parses-and-reinserts**: conversations/messages/artifacts as validated rows, documents
  re-ingested from `files/` through the normal pipeline (re-extracted, re-embedded — robust
  against schema and embedding-model drift), memory re-embedded. **Never** raw-unpack DB
  files from an archive (untrusted SQLite is an attack surface, and principle #6 applies to
  archives like any input: name validation, size caps, entry-count caps — the zip-bomb
  guard exists in `Gert.External.Isolation`).
- **Depends:** — (priority last; ship after the spine)
- **Touches:** export writers, a new import service + controller + validators, tests
  (round-trip + adversarial archive).
- **Docs to update:** rest-api (account & data), configuration §5 (lifecycle table gains
  the restore row), security (new finding if review surfaces one — import is a new input
  boundary).
- **Acceptance:** export → import round-trips to an equivalent project (chats readable,
  docs `ready`, memory retrievable); traversal-named/oversized/bomb archives are rejected
  with 400s and no partial state; the new input boundary has `NaughtyStrings` coverage.

### S8 — Keyed turn parallelism with an atomic 409 gate (one combined change)

> **✅ Shipped** — see [decisions §11](decisions.md#11-turn-execution--keyed-lanes-over-an-atomic-per-conversation-gate)
> (now settled). **Deviation from the Depends line below, orchestrator-approved:** S8 landed
> *before* S1/S2a, against today's code — "expired" means the current orphan rule
> (`CreatedAt` + `MaxTurnDuration`), and the 409 branch stays a 409. When S1/S2a land they
> re-touch exactly two single points: the write-back predicate in `TurnPlanner.PlanAsync`
> and its 409 branch.

- **Goal:** Retire [decisions §11](decisions.md#11-turn-execution--keyed-lanes-over-an-atomic-per-conversation-gate)'s
  interim posture: the single global serial `TurnWorker` is today the *only* run-phase
  protection for the seq single-writer invariant and the TOCTOU-racy 409 check
  (check-then-insert, no transaction, no unique index). Replace it with explicit controls
  and unlock cross-conversation concurrency. **One combined change, never one half without
  the other:**
  - **(a) Per-conversation atomic gate:** a partial unique index
    `messages(conversation_id) WHERE status='streaming'` makes the placeholder insert
    itself the gate (a losing racer gets the constraint violation → 409, no window); plus a
    planner **write-back of expired placeholders** — the orphan rule's lazy read-side
    mapping becomes a real `streaming → error` write for rows past the horizon, so a dead
    turn's row releases the index instead of blocking it forever.
  - **(b) Bounded keyed parallelism:** hash `TurnKey` onto N serial sub-workers (N small,
    configurable), so one conversation's turns stay strictly ordered on one lane while
    different conversations run concurrently.
  - Shipping (b) without (a) removes the only protection those invariants have; shipping
    (a) without (b) closes a race nobody can hit yet and changes nothing user-visible.
- **Depends:** S1 (the write-back must use S1's staleness rule to decide "expired" —
  heartbeat-stale, not just old); S2a (it rewrites the planner's 409 branch into steering —
  the atomic gate becomes the "is a turn live" predicate under whichever branch survives;
  land S8 after S2a or rebase whichever lands second). Touches the same files as both —
  same-track, no parallel worktree.
- **Touches:** `Migrations/chat/NNN_streaming_gate.sql` (partial unique index),
  `TurnPlanner` (constraint-violation → `TurnInProgressException`; expired-placeholder
  write-back), `IChatRepository` (write-back method), `ChannelTurnQueue`/`TurnWorker`
  (keyed lanes), `TurnOptions` (lane count), tests (a concurrent-plan race test that fails
  on today's code, lane-ordering tests).
- **Docs to update:** decisions §11 (mark remedied), chat-and-tools (detached turns —
  worker topology), storage-and-data (the new index), installation §9 (the lane knob).
- **Acceptance:** two concurrent plans for one conversation yield exactly one streaming
  placeholder and one 409/steer (asserted at the SQLite tier, real database); turns for one
  conversation never interleave (event seqs stay monotonic per conversation under load);
  turns for different conversations provably overlap; an expired placeholder is written
  back and a new turn proceeds; the whole suite stays green with N = 1 (the degenerate
  serial config).
