# Chat orchestration, RAG, ingestion & tools

## Chat orchestration (the tool loop)

vLLM exposes an **OpenAI-compatible** `/v1/chat/completions` with function calling and streaming, so the orchestrator can use a standard OpenAI client pointed at the model's base URL.

The API advertises up to nine tools to the model (each gated by entitlement,
conversation toggles, and the request — see the intersection rule below):

```jsonc
[
  { "name":"search_documents", "description":"Hybrid search over this project's private docs + memory",
    "parameters": { "query":"string", "k":"integer" } },
  { "name":"web_search", "description":"Search the web via SearXNG",
    "parameters": { "query":"string" } },
  { "name":"run_python", "description":"Execute Python in a sandbox, return stdout",
    "parameters": { "code":"string" } },
  { "name":"set_todos", "description":"Replace the model-managed todo checklist the chat window renders",
    "parameters": { "todos":"[{ text, status: pending|active|done }]" } },
  { "name":"get_datetime", "description":"Current date/time (UTC + optional IANA timezone)",
    "parameters": { "timezone":"string?" } },
  // the canvas suite — model-driven file creation + iteration (§ Artifacts below)
  { "name":"make_artifact", "description":"Create (or overwrite by name) a complete, self-contained file in the canvas",
    "parameters": { "name":"string", "format":"html|markdown|svg|python|csharp|cpp|javascript|rust", "content":"string" } },
  { "name":"edit_artifact", "description":"Change part of an existing artifact by exact substring replacement",
    "parameters": { "name":"string", "old_str":"string", "new_str":"string" } },
  { "name":"read_artifact", "description":"Return an artifact's current content, line-numbered",
    "parameters": { "name":"string", "range":"string?" } },
  { "name":"ask_user", "description":"Ask the user ONE clarifying question and wait for their answer",
    "parameters": { "question":"string", "options":"string[]?", "allow_free_text":"boolean?" } }
]
```

`set_todos`, `get_datetime`, and the canvas suite touch no external world: the
todo list is replace-not-patch (the latest call is the truth, rendered as a
checklist on its tool card and persisted with the `tool_calls` row — no extra
storage), the clock reads only through the injected `TimeProvider` (so tests
pin the instant), and the artifact tools read/write only this conversation's
`artifacts` rows.

**Round narration rides back.** A model that narrates while it calls tools
(qwen streams "here's file one…" AND `set_todos` in the same round) must see
its own words next round — the tool-loop echoes each round's streamed text as
the `content` of the assistant tool-calls message. Dropping it (the old
`content: null`) made the model find a done-marked list with no work in its
own empty turn, conclude it had skipped the steps, and restart the answer
every round ("oops, I jumped the gun" ×3, files generated twice — the
2026-06-06 repro). The `set_todos` result also carries a `reminder` field:
with open items it says "N step(s) remain — continue in this same reply",
because qwen's instruct mode otherwise yields to the user after one step;
when all items are done it says to wrap up.

**Mode-correct sampling (Qwen3.6).** The checkpoint's
`generation_config.json` carries only the thinking-mode set (temperature 1.0,
top_p 0.95, top_k 20) — that is what vLLM applies to omitted fields. The
card's instruct (thinking-off) set — temperature 0.7, top_p 0.8,
**presence_penalty 1.5** — must ride the request explicitly, or thinking-off
turns decode with the wrong mode's sampling and, without the presence
penalty, fall into repetition loops ("ask you to ask you to…"; greedy temp 0
is worse and explicitly advised against). Declared per-model in the catalog
(`Gert:Models[].InstructParams`; the single-vLLM fallback entry ships the
Qwen3.6 values) and applied by the planner as the LAST field-by-field
fallback when `thinking == false` — conversation params and per-model user
settings always win.

**Cross-turn todo revival.** The planner rebuilds upstream history as
role+content only (tool calls and results never re-enter the prompt), so a
list set via `set_todos` in turn N is invisible in turn N+1 — the model would
silently abandon its own plan. The fix follows the pattern Claude Code and
Cline use (dynamic state re-injected into the *message array*, never the
system prompt), and is **generic, not todo-specific**: a tool opts in by
implementing `ITailReminder`. For every offered tool that does, the planner
reads its newest accepted snapshot (`GetLatestToolCallAsync(conv, tool.Id)`,
the `done` row's `response_json`) and calls `BuildTailReminder(snapshot)`; the
tool decides whether revival is warranted and returns the `<system-reminder>`
text or null. Any text returned is appended to the new user message in the
*rendered* prompt only. `TodoTool` is the one reviver today: it emits the block
(snapshot JSON plus "continue the remaining items, keep statuses current, don't
mention this") only while the list still has `pending`/`active` items.
Tail placement is deliberate: the system prompt and prior turns keep their
exact bytes, so vLLM prefix-cache reuse survives up to the previous tail. The
persisted user row stays clean (UI truth), a finished/empty list injects
nothing (no nagging about done work), and both the read and the
`BuildTailReminder` call are best-effort — a broken snapshot or a tool parse
bug never fails the turn. Verified live against Qwen3.6 on vLLM 0.22
(2026-06-06): with the reminder, turn 2 picks up the one remaining `pending`
item that only the snapshot names (`Live_todo_reminder_revives…`).

### Artifacts (the canvas tool suite)

Artifacts are created by **explicit tool calls**, not by parsing the model's prose.
The earlier convention — a named fenced block (` ```html name=demo.html `) that the
runner extracted from the final content — was replaced wholesale: a file's own
` ``` ` fences could truncate the block (the nested-fence bug), and extraction
tolerances kept growing to chase model formatting. As tool arguments the content is
opaque JSON, so none of that class of bug exists. Three functions:

- **`make_artifact(name, format, content)`** — create or **overwrite by name**
  within the conversation: a re-used name saves over the prior draft (same canvas
  tab, bumped version). `format` is the closed kind set
  (`html · markdown · svg · python · csharp · cpp · javascript · rust`). The system
  prompt instructs the model to use this *instead of* pasting whole files into code
  blocks.
- **`edit_artifact(name, old_str, new_str)`** — iterate without re-emitting the
  whole file, mirroring Anthropic's `str_replace` contract: `old_str` must match
  **exactly** (whitespace included) and **exactly once**; zero or many matches
  return an error the model reads and corrects next round — the feedback loop.
- **`read_artifact(name, range?)`** — return the current content with **1-indexed,
  number-prefixed lines** (mirrors Anthropic's `view`), so a follow-up
  `edit_artifact` can copy a snippet verbatim. Read-only; emits no canvas event.

Each call persists as a normal `tool_calls` row; created/updated artifacts ride back
on the tool result and the runner emits the `artifact` event — the canvas tab
opens/updates **live, mid-turn** (no longer only at `message_end`), and a reload
gets the same artifacts back through the thread GET.

**How the model learns the convention.** The built-in `SystemPrompts.Canvas`
fragment rides first in every turn's system prompt (before project pinned
instructions) and tells the model to call `make_artifact` for any complete file
instead of a code block. If artifacts stop appearing for prompts that should
produce them, debug in this order: (1) is the canvas suite actually *offered* —
entitlement (`make_artifact`/`edit_artifact`/`read_artifact` ids must be in the
`gert_tools` claim, the sole grant source — [auth § tool registry](auth.md#tool-registry)),
the conversation's Canvas toggle, and a tool-capable model all gate it; (2) is
`SystemPrompts.Canvas` present in the upstream request (a stale host build);
(3) did the model paste a fenced block anyway (a model/template regression —
capture the completion and re-measure compliance). Inline code blocks stay
inline in the bubble; nothing is ever extracted from prose.

### Detached turns

A turn is **detached from the request that started it**: `POST …/messages` only
*plans* (validate → materialize the conversation if new → persist the user
message and a `streaming` assistant placeholder → snapshot identity +
entitlements into a `TurnJob`) and enqueues; a `TurnWorker`
(`BackgroundService`, mirroring ingestion) runs the tool loop off-thread.
**The database is the source of truth and transports are just delivery** — the
runner's emit protocol is *persist, then publish*: allocate a per-conversation
monotonic `seq` (`conversations.next_seq`), append the event to the durable
`turn_events` log, then publish to the in-process `ConversationBus`. Clients
attach over WS / SSE / range-polling (rest-api.md § receiving a turn) through
one splice (`ConversationStreamer`): subscribe first, replay `seq > cursor`
from the log, then drain live deduping by the seq watermark — no gaps, no
duplicates, and **generation survives the client disconnecting** (a reload
mid-turn resubscribes from the assistant row's `seq` and loses nothing).

Plan phase (request scope):

```
0. Validate (fail-closed) BEFORE any disk touch; reject a concurrent turn with 409
   (turns are serialized per conversation — the seq single-writer invariant). The
   rejection is the placeholder insert hitting the partial unique index
   ux_messages_streaming (at most one streaming row per conversation, engine-
   enforced); the planner's read check is only a fast path, and expired streaming
   rows are WRITTEN BACK to error before inserting (see the orphan rule below).
1. Materialize the conversation if new; persist the user message (status=complete)
   and the assistant placeholder (status=streaming), each with an allocated seq —
   both rows in one IMMEDIATE transaction (the gated insert above).
2. Resolve offered tools + snapshot the entitlement claim; capture history
   (complete rows only), system prompt, and generation params into the TurnJob.
3. Enqueue; respond 202 { ids, seq } — the subscribe cursor.
```

Run phase (worker scope, `DetachedUserContext` seeded from the job's snapshot):

```
2. Call vLLM (stream). Every text delta is emitted LIVE as it arrives
   (persist `turn_events` row → publish to the bus), no per-call buffering.
3. If the model emits tool_calls:
     a. emit `tool_call` (card appears)
     b. execute the tool against THIS project's resources
        - search_documents → hybrid query (below) on this project's rag.db (docs + memory)
        - web_search       → SearXNG
        - run_python       → sandbox (monty by default, or gVisor)
        - make/edit/read_artifact → this conversation's artifacts rows (chat.db)
        (entitlement re-checked against the job's plan-time snapshot)
     c. emit `tool_result` + persist the tool_calls row live (with latency_ms);
        collected citations keep tool_call_id provenance
        (message → tool_call → citations); flush partial content to the row
     d. feed the tool result back to the model; go to 2
4. Otherwise: renumber + persist + emit citations, finalize the assistant row
   (status=complete, token_count), then emit `message_end`.
5. On any fault (model error, tool defect, or the max-turn-duration cap): finalize
   the row as status=error keeping the partial content, and emit a terminal
   `error` event. (Unlike the old pipeline, a failed turn persists.)
```

**The orphan rule.** The turn queue is in-memory and non-durable: a crashed
worker leaves rows stuck at `streaming` forever. Every *reader*
(`MessageStatusRules`) maps a `streaming` row older than
`Gert:Turn:MaxTurnDuration` to `error` — stateless and multi-instance-safe —
and the planner's 409 check goes through the same rule, so an abandoned turn
never blocks a conversation. The planner additionally **writes the mapping
back** (`streaming → error`, conditional on the row still being `streaming`)
before inserting a new turn's rows: readers still map lazily, but a dead turn's
row also durably frees the `ux_messages_streaming` gate index. **Both timers
share the plan-time anchor:** the reader-facing horizon ages the row from its
`CreatedAt` (stamped at plan time), and the runner caps its own wall clock at
the *remaining* budget measured from the very same instant (`TurnJob.PlannedAt`
— one clock read in the planner, not two). Time a job spends waiting in the
queue therefore counts against the turn, and a running turn always self-cancels
at or before the moment readers would start reporting its row as `error` — a
queue wait can never open a window where a healthy turn reads as dead and the
409 gate reopens against incomplete history
([decisions §11](decisions.md#11-turn-execution--keyed-lanes-over-an-atomic-per-conversation-gate)).

**Worker topology.** `TurnWorker` drains `Gert:Turn:MaxConcurrentTurns`
(default 4) keyed lanes: jobs shard by the full `TurnKey` hash, so one
conversation's turns ride one lane in strict FIFO while different
conversations may run concurrently — the gate index is the correctness
control, the lane count only throughput
([decisions §11](decisions.md#11-turn-execution--keyed-lanes-over-an-atomic-per-conversation-gate)).

**Scale-out.** The bus is per-process (live push is a latency optimization);
the `turn_events` log + range endpoint are the cross-instance truth, so a
client on another instance still sees everything — just less live.

Only tools that are **(a) granted to the user by the `gert_tools` JWT entitlement, (b) enabled on the conversation, (c) requested in the body, and (d) usable by the model** (a catalog entry without tool capability is never advertised tools, whatever the toggles say) are offered — the entitlement is the hard ceiling (see [Auth → Tool entitlements](auth.md#tool-entitlements-allowed-tools-in-the-jwt)), enforced at advertise time in the planner and re-checked at execution time against the job's plan-time snapshot. So flipping "Use my docs" off removes `search_documents` for that turn, and a user without the `sandbox` entitlement never gets `run_python` advertised regardless of toggles.

---

## RAG: hybrid retrieval

The mockup labels the retrieval a **hybrid query**, so combine lexical + vector and fuse:

1. **Vector KNN** against `vec_chunks` (the query is embedded by the same model that embedded the chunks):

   ```sql
   SELECT chunk_id, distance
   FROM   vec_chunks
   WHERE  embedding MATCH :qvec          -- packed float32 or a JSON '[...]' array
   ORDER  BY distance
   LIMIT  :k;
   ```

2. **Lexical BM25** against `fts_chunks`:

   ```sql
   SELECT rowid AS chunk_id, bm25(fts_chunks) AS score
   FROM   fts_chunks
   WHERE  fts_chunks MATCH :query
   ORDER  BY score
   LIMIT  :k;
   ```

3. **Reciprocal Rank Fusion** to merge the two ranked lists into the final top-k, then join back to `chunks` + `documents` for content, `page`, filename, and score (the `0.89`, `0.81`, `0.77` in the mockup). The join-back filters to `documents.status = 'ready'`, so chunks of a failed or still-processing document are never retrievable — the read-side end of the ingestion failure cleanup below.

The result becomes the `tool_result` SSE and seeds the citations.

> **Memory rides the same query.** A project's memory entries are embedded into the *same*
> `rag.db` as its documents, distinguished only by `documents.kind = 'memory'`
> ([storage-and-data](storage-and-data.md#ragdb-sqlite-vec)). So `search_documents` retrieves
> memory and documents together with no extra plumbing; `pinned` memory is additionally
> prepended to the system prompt (loop step 0) rather than waiting to be retrieved
> ([configuration → memory](configuration.md#23-memory)).

> **Loading sqlite-vec in .NET.** Use a SQLite build that permits extensions (`SQLitePCLRaw.bundle_e_sqlite3`), then on each `rag.db` connection:
> ```csharp
> conn.EnableExtensions(true);
> conn.LoadExtension(vecPath);   // path to vec0.so / vec0.dll on the host
> ```
> Embeddings can be bound either as packed little-endian `float32` bytes or as a JSON array string `"[0.12, -0.04, …]"`; vec0 accepts both.

---

## Document ingestion pipeline

Upload returns immediately; the heavy work runs in a background worker fed by an in-process queue (`System.Threading.Channels` + a `BackgroundService`).

```
POST /api/projects/{pid}/documents
  └─ save file to /data/users/{key}/projects/{pid}/files/{doc-id}.{ext}
  └─ INSERT documents(status='processing')   into this project's rag.db
  └─ enqueue IngestJob{ sub, pid, doc_id, path }
  └─ 202 → { id, status:"processing" }

IngestionWorker (per job):
  1. extract text   (PDF → PdfPig, DOCX → OpenXML, md/txt → read)
  2. if no text     → status='failed', error='no extractable text'   ← old-scan.pdf
  3. chunk          (token-aware windows w/ overlap; record page/§)
  4. embed          (batch chunks → vLLM embeddings endpoint)
  5. write          (chunks + vec_chunks + fts_chunks), update chunk_count,
                     emitting progress ("embedding 12 / 19 chunks…")
  6. status='ready'
```

> **Hardened extraction (step 1) — isolated subprocess.** Uploads are untrusted bytes fed to large
> native/managed parsers (OpenXML: DOCX = a zip of XML; PdfPig), so step 1 runs **out-of-process in
> an unprivileged, resource-capped helper** — not the worker process. Dropped privileges, no network,
> read-only access to the one input file, hard `RLIMIT_AS`/`RLIMIT_CPU`/`RLIMIT_NPROC` + a wall-clock
> timeout, and in-process XML hardening inside it (**DTD/external-entity off** for XXE,
> **decompressed-size + zip-entry caps** for bombs). A crash/OOM/timeout fails *that document*
> (`status='failed'`), never the host — the ingestion analog of the `run_python` sandbox below.
> Failure also **deletes any already-inserted chunks** (`IngestionService.FailAsync` →
> `DeleteChunksAsync`): chunk batches commit per batch, so without the compensation a
> mid-pipeline fault would leave a half-ingested document's chunks behind; together with the
> retrieval-side `status='ready'` join above, a failed document leaves nothing retrievable. It
> may reuse the same **gVisor (`runsc`)** lever ([security F7](security.md#3-findings--remediations),
> [tech-stack](tech-stack.md)).

Status transitions map directly to the panel pills: `processing` (amber, pulsing) → `ready` (sage) or `failed` (brick). The SPA learns about transitions by **polling `GET /api/projects/{pid}/documents/{id}`** while a doc is processing (see [decisions.md §6](decisions.md#6-live-ingestion-progress)); an SSE `…/documents/events` stream is a deferred, additive upgrade.

`DELETE /api/projects/{pid}/documents/{id}` removes the `chunks` (cascade clears `vec_chunks`/`fts_chunks` via triggers or explicit deletes) and unlinks the original file.

---

## Tools detail

### RAG

See [RAG: hybrid retrieval](#rag-hybrid-retrieval) above.

### Web search (SearXNG)
Server-side `HttpClient` (via `IHttpClientFactory`) to the SearXNG JSON API. Take the top results, optionally fetch + summarize, keep the few that matter (the mockup shows "1 result kept"), and return titles/URLs as web-type citations.

> **SSRF guard on the fetch step.** The summarize step pulls a result URL server-side, and that URL
> is attacker-influenceable (search results, or prompt-injected document content steering the
> model's query) — so the fetcher is hardened ([security F5](security.md#3-findings--remediations)):
> **`http`/`https` only** (no `file:`/`gopher:`/etc.); resolve the host and **block private,
> loopback, link-local, and unique-local ranges** (IPv4 *and* IPv6, including the cloud metadata
> IP), **re-checking after every redirect**; and cap response size, time, and redirect count. The
> fetcher must never reach vLLM, SearXNG, or any other internal service.

### Sandbox — security-critical
`run_python` runs untrusted, model-written code — the strongest blast radius in the system ([security F5](security.md#3-findings--remediations)). Two backends sit behind one `ISandbox` port, chosen by the operator (`Gert:Sandbox:Backend`):

- **monty** *(default)* — [Pydantic Monty](https://github.com/pydantic/monty), a minimal Python interpreter written in Rust with **no syscalls**: the language itself has no filesystem, network, or env access, so untrusted code can only reach the world through host callbacks (plain `run_python` grants none — that arrives with code-mode). It runs in a **sidecar process** Gert reaches server-side over HTTP ([tools/monty](../../tools/monty/README.md)) — unprivileged, no `/data`, egress off: monty's capability sandbox nested in an OS sandbox. Microsecond startup, no container, no infra. A Python *subset* (no classes/`match`, small stdlib) — fine for "calculations and quick data transforms"; an unsupported construct returns an error the model reads.
- **gVisor (`runsc`)** — an **ephemeral container** running real CPython, for workloads needing the full language/stdlib: no inbound network, **outbound off by default** (an allow-list is opt-in only, never the default), read-only rootfs, a small writable `/tmp`, container destroyed after the call. Needs gVisor on the host.

Both backends share the posture: **no mount of `/data`** (the sandbox must never see another user's — or this user's — DB files), a hard wall-clock + memory cap, and only captured `stdout`/`stderr` returns. Because the sandbox has no filesystem access to user data, code execution stays isolated from the per-user storage model.

### Ask the user (`ask_user`)

`ask_user(question, options?, allow_free_text?)` lets the model ask the user
**one clarifying question mid-turn and block until it is answered, times out,
or the turn is cancelled**. No external world: the question travels as a
`question_asked` event (the one tool that emits mid-execution, through the
optional `ToolInvocation.EmitAsync` seam the runner populates with its own
persist-then-publish emit), and the answer arrives through the singleton
`ITurnQuestions` registry — the awaitable mirror of the cancel registry, keyed
by the same tenant-scoped `TurnKey` — from
[`POST …/answer`](rest-api.md#answer-a-question). `allow_free_text` defaults to
true for an open question and false once `options` (max 8) are offered.

**Wait budget.** The wait is exempt from the generic `ToolCallTimeout`
backstop (the `IInteractiveTool` marker — a 60 s cap would kill every wait) and
instead runs for **min(`Gert:Turn:AskUserTimeout` (default 5 min), remaining
turn budget − a 15 s grace)**, where the remaining budget is anchored at
`TurnJob.PlannedAt` exactly like the runner's lifetime cap — so the graceful
path always wins over the turn-budget error finalize. A **timeout is a
successful tool result** (`{"answered":false,"reason":"timeout"}`, card line
"The user did not respond."), never a turn fault: on a detached (client-gone)
turn the question simply expires and the turn continues — the detached-turn
guarantee is preserved. A user **cancel** (`POST …/cancel` / WS) cancels the
wait and finalizes the turn `cancelled` like any other; the pending question is
always released.

**Replay.** A pending question lives only in `turn_events` (the `tool_calls`
row lands when the call returns), so a reconnecting client recovers it through
the resume replay: `tool_call(ask_user)` → `question_asked` with nothing after
⇒ render the interactive card; a following `question_answered` (or the call's
`tool_result`) ⇒ render the resolved/expired state. After the turn ends, the
thread GET rebuilds a read-only card from the persisted row
(`{answered, answer | reason}`).

**UX consequence.** While the question pends the turn is in-flight, so the 409
rule blocks new sends in that conversation — the question card (or Stop) is
the only input. One question per turn: a second `ask_user` while one pends is
a tool error the model reads.
