# Chat orchestration, RAG, ingestion & tools

## Chat orchestration (the tool loop)

vLLM exposes an **OpenAI-compatible** `/v1/chat/completions` with function calling and streaming, so the orchestrator can use a standard OpenAI client pointed at the model's base URL.

The API advertises five tools to the model:

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
    "parameters": { "timezone":"string?" } }
]
```

`set_todos` and `get_datetime` touch no external world: the todo list is
replace-not-patch (the latest call is the truth, rendered as a checklist on its
tool card and persisted with the `tool_calls` row — no extra storage), and the
clock reads only through the injected `TimeProvider`, so tests pin the instant.

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
system prompt): when the todo tool is offered and the conversation's newest
accepted snapshot (`GetLatestToolCallAsync(conv, "todo")`, newest `done` row's
`response_json`) still has `pending`/`active` items, the planner appends
`SystemPrompts.TodoReminder(snapshot)` — a `<system-reminder>` block carrying
the snapshot JSON plus "continue the remaining items, keep statuses current,
don't mention this" — to the new user message in the *rendered* prompt only.
Tail placement is deliberate: the system prompt and prior turns keep their
exact bytes, so vLLM prefix-cache reuse survives up to the previous tail. The
persisted user row stays clean (UI truth), a finished/empty list injects
nothing (no nagging about done work), and the read is best-effort — a broken
snapshot never fails the turn. Verified live against Qwen3.6 on vLLM 0.22
(2026-06-06): with the reminder, turn 2 picks up the one remaining `pending`
item that only the snapshot names (`Live_todo_reminder_revives…`).

### Artifacts (canvas tabs)

The model opts a fenced block into the canvas by **naming it in the fence info
string** — ` ```html name=demo.html ` … ` ``` `. When the turn's final content is
assembled, the runner extracts every named fence whose language maps onto the
closed artifact-kind set (`md`/`markdown`, `html`/`htm`, `svg`, `py`/`python`,
`cs`/`csharp`, `cpp`/`c++`/`cc`/`cxx`, `js`/`javascript`, `rs`/`rust`),
persists each as an `artifacts` row (provenance: conversation + producing
message), and emits an `artifact` event before `message_end` — the canvas tab
opens live, and a reload gets the same artifacts back through the thread GET.
Unnamed fences and unknown languages stay inline in the bubble; extraction is
additive (the fence text remains part of the message).

**How the model learns the convention.** Real models don't know `name=` on
their own — the built-in `SystemPrompts.Canvas` fragment rides first in every
turn's system prompt (before project pinned instructions) and teaches the
opt-in. Measured against Qwen3.6-27B-FP8 on vLLM 0.22 (2026-06-06): **5/5
compliance** for "make me a demo html page" with thinking ON and default
sampling — the convention is reliable when the prompt actually reaches the
model.

**Deliberately NO unnamed-fence fallback.** A complete-document heuristic
(auto-extracting unnamed ` ```html ` fences starting at `<!doctype>`) was tried
and removed: it blurs the opt-in contract and invents filenames the model never
chose. If artifacts stop appearing for prompts that should produce them,
debug in this order: (1) is the running host built from current code —
`SystemPrompts.Canvas` present in the upstream request? (2) did the model emit
the fence unnamed anyway (a model/template regression — capture the completion
and re-measure compliance)? Do not relax the extractor.

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
   (turns are serialized per conversation — the seq single-writer invariant).
1. Materialize the conversation if new; persist the user message (status=complete)
   and the assistant placeholder (status=streaming), each with an allocated seq.
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
        - run_python       → gVisor sandbox
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
never blocks a conversation.

**Scale-out.** The bus is per-process (live push is a latency optimization);
the `turn_events` log + range endpoint are the cross-instance truth, so a
client on another instance still sees everything — just less live.

Only tools that are **(a) granted to the user by the `gert_tools` JWT entitlement, (b) enabled on the conversation, and (c) requested in the body** are offered to the model — the entitlement is the hard ceiling (see [Auth → Tool entitlements](auth.md#tool-entitlements-allowed-tools-in-the-jwt)), enforced at advertise time in the planner and re-checked at execution time against the job's plan-time snapshot. So flipping "Use my docs" off removes `search_documents` for that turn, and a user without the `sandbox` entitlement never gets `run_python` advertised regardless of toggles.

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

3. **Reciprocal Rank Fusion** to merge the two ranked lists into the final top-k, then join back to `chunks` + `documents` for content, `page`, filename, and score (the `0.89`, `0.81`, `0.77` in the mockup).

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
> (`status='failed'`), never the host — the ingestion analog of the `run_python` sandbox below. It
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

### Sandbox (gVisor) — security-critical
Each `run_python` call executes in an **ephemeral gVisor (`runsc`) container**:

- no inbound network; **outbound disabled by default** (an allow-list is opt-in only, never the default) — the egress/exfiltration brake for arbitrary code ([security F5](security.md#3-findings--remediations)),
- read-only rootfs, a small writable `/tmp`, **no mount of `/data`** (the sandbox must never see another user's — or this user's — DB files),
- CPU/memory/PID limits and a hard wall-clock timeout,
- container destroyed after the call; only captured `stdout`/`stderr` returns.

Because the sandbox has no filesystem access to user data, code execution stays isolated from the per-user storage model.
