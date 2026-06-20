# Chat orchestration, RAG, ingestion & tools

## Chat orchestration (the tool loop)

vLLM exposes an **OpenAI-compatible** `/v1/chat/completions` with function calling and streaming, so the orchestrator can use a standard OpenAI client pointed at the model's base URL.

The API advertises up to twelve tools to the model (each gated by entitlement,
conversation toggles, and the request - see the intersection rule below):

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
  { "name":"get_datetime", "description":"Current date/time, user-local by default (the send request snapshots the browser's IANA timezone; an explicit timezone argument wins, UTC is the last resort)",
    "parameters": { "timezone":"string?" } },
  // the canvas suite - model-driven file creation + iteration (section Artifacts below)
  { "name":"make_artifact", "description":"Create (or overwrite by name) a complete, self-contained file in the canvas",
    "parameters": { "name":"string", "format":"html|markdown|svg|python|csharp|cpp|javascript|rust", "content":"string" } },
  { "name":"edit_artifact", "description":"Change part of an existing artifact by exact substring replacement",
    "parameters": { "name":"string", "old_str":"string", "new_str":"string" } },
  { "name":"read_artifact", "description":"Return an artifact's current content, line-numbered",
    "parameters": { "name":"string", "range":"string?" } },
  { "name":"ask_user", "description":"Ask the user up to four clarifying questions (shown as tabs) and wait for their answers",
    "parameters": { "questions":"{ question:string, header:string?, options:string[]?, allow_free_text:boolean? }[]" } },
  { "name":"web_fetch", "description":"Fetch one public web page by URL, return its readable content (HTML reduced to plain text, clipped)",
    "parameters": { "url":"string", "max_chars":"integer?" } },
  { "name":"save_memory", "description":"Save ONE durable fact or preference for future conversations in this project",
    "parameters": { "title":"string", "content":"string" } },
  { "name":"run_sub_agent", "description":"Delegate one self-contained task to a sub-agent and wait for its result (it cannot see this conversation; only its final answer returns)",
    "parameters": { "task":"string", "context":"string?" } }
]
```

**Tool specs are a token budget.** The tools array renders into the prompt's
tools region, and Qwen3.6-27B's tool-call **format adherence collapses when
that region grows past roughly 1.8k tokens**: measured live (2026-06-12,
vLLM 0.22.1, seeds 7-9), the full set with the old verbose descriptions
(~2,000 prompt tokens) made the model emit mangled call XML
(`<user_search>`, `<parameter>query>`) or flatly claim "I don't have a web
search tool", while the SAME eleven tools with lean descriptions - and a 10-of-11
subset, even padded with 600 tokens of plain system text - called cleanly. The
budget is specifically the tools region, not overall prompt length. Every
description therefore stays at one or two short sentences carrying only the
behavioural contract (the per-tool sections below hold the rationale); growing
one back, or adding a twelfth tool, must be re-verified against the live model
(`tools/smoke` section live tool sweep).

**Tool-call robustness (leak salvage).** The server-side tool parser
(`--tool-call-parser qwen3_coder`, regex-based) misses near-miss calls; the raw
`<tool_call>` markup then leaks into `content` deltas - streamed to the user
verbatim, or (streaming) swallowed into a silently empty reply. The
`OpenAIStreamParser` therefore routes content through a hold-back state machine:
text streams through normally (at most a 10-char tail held until
disambiguated), but a completed `<tool_call>` opener switches to buffering; at
the close tag or stream end the segment is salvage-parsed - the Hermes JSON
body and the qwen3-coder XML body both - into a real `ChatModelToolCall`
(id `salvaged_<name>_<n>`), and an unsalvageable segment is dropped, never
shown. The client logs both counts after the stream; salvages firing at all
means the server parser configuration deserves a look (`qwen3_xml` is the
community-recommended parser for Qwen3.6). A segment past 256 KiB degrades
back to visible text - better ugly than unboundedly silent.

`set_todos`, `get_datetime`, and the canvas suite touch no external world: the
todo list is replace-not-patch (the latest call is the truth, rendered as a
checklist on its tool card and persisted with the `tool_calls` row - no extra
storage), the clock reads only through the injected `TimeProvider` (so tests
pin the instant), and the artifact tools read/write only this conversation's
`chat_objects` rows - reached through the tool host's chat-scoped
`IObjectResource`, never a raw key.

**Round narration rides back.** A model that narrates while it calls tools
(qwen streams "here's file one..." AND `set_todos` in the same round) must see
its own words next round - the tool-loop echoes each round's streamed text as
the `content` of the assistant tool-calls message. Dropping it (a
`content: null` tool-calls message) makes the model find a done-marked list
with no work in its own empty turn, conclude it skipped the steps, and restart
the answer every round ("oops, I jumped the gun" x3, files generated twice).
The `set_todos` result also carries a `reminder` field:
with open items it says "N step(s) remain - continue in this same reply",
because qwen's instruct mode otherwise yields to the user after one step;
when all items are done it says to wrap up.

**Mode-correct sampling rides the provider (Qwen3.6).** The checkpoint's
`generation_config.json` carries only the thinking-mode set (temperature 1.0,
top_p 0.95, top_k 20) - that is what vLLM applies to omitted fields. Neither
mode trusts those defaults: each is a separately **configured provider**
([installation section providers](../installation/configuration.md#4-gertchatproviders---the-chat-provider-catalog)),
and the user **picks** thinking-vs-instruct by selecting that provider - there
is no per-request mode toggle. The provider's `Parameters` carry the sampling
verbatim onto the wire (typed OpenAI fields + the `Extra` map for `top_k` and
`chat_template_kwargs.enable_thinking`):

- **`qwen36-instruct`** - temperature 0.7, top_p 0.8, **presence_penalty 1.5**,
  `top_k=20`, `enable_thinking=false`. Without the presence penalty, thinking-off
  turns fall into repetition loops ("ask you to ask you to..."; greedy temp 0 is
  worse and explicitly advised against).
- **`qwen36-thinking`** - temperature 0.6, top_p 0.95, `top_k=20`,
  **presence_penalty unset/0**, `enable_thinking=true`: Qwen3.6's recommended
  thinking set, tuned for precise coding (WebDev), declared in full so a
  checkpoint swap can't shift the decode. `presence_penalty` is **0**, not the
  instruct preset's 1.5 - a high penalty degrades precise code (Qwen warns of
  language mixing and a slight perf drop), so the coding-quality trade wins
  here. The known failure mode of penalty-0 thinking turns is a live repetition
  loop (a turn answering "I will search for today's top stories. `</think>`"
  thirty times instead of calling `web_search`); **watch tool/search turns for
  it**.

Sampling is **whatever the selected provider declares** - there is no
conversation/user sampling override left to merge. The single-vLLM fallback
provider ships permissive defaults (no explicit sampling), so a zero-config boot
still answers. Interleaved thinking (replaying prior `reasoning_content`
upstream) is gated by the provider's `chat_template_kwargs.preserve_thinking`,
applied in the adapter.

**Cross-turn todo revival.** The planner rebuilds upstream history as
role+content only (tool calls and results never re-enter the prompt), so a
list set via `set_todos` in turn N is invisible in turn N+1 - the model would
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
`BuildTailReminder` call are best-effort - a broken snapshot or a tool parse
bug never fails the turn. Verified live against Qwen3.6 on vLLM 0.22
(2026-06-06): with the reminder, turn 2 picks up the one remaining `pending`
item that only the snapshot names (`Live_todo_reminder_revives...`).

### Artifacts (the canvas tool suite)

Artifacts are created by **explicit tool calls**, not by parsing the model's prose.
The earlier convention - a named fenced block (` ```html name=demo.html `) that the
runner extracted from the final content - was replaced wholesale: a file's own
` ``` ` fences could truncate the block (the nested-fence bug), and extraction
tolerances kept growing to chase model formatting. As tool arguments the content is
opaque JSON, so none of that class of bug exists. Three functions:

- **`make_artifact(name, format, content)`** - create or **overwrite by name**
  within the conversation: a re-used name saves over the prior draft (same canvas
  tab, bumped version). `format` is the closed kind set
  (`html - markdown - svg - python - csharp - cpp - javascript - rust`). The system
  prompt instructs the model to use this *instead of* pasting whole files into code
  blocks.
- **`edit_artifact(name, old_str, new_str)`** - iterate without re-emitting the
  whole file, mirroring Anthropic's `str_replace` contract: `old_str` must match
  **exactly** (whitespace included) and **exactly once**; zero or many matches
  return an error the model reads and corrects next round - the feedback loop.
- **`read_artifact(name, range?)`** - return the current content with **1-indexed,
  number-prefixed lines** (mirrors Anthropic's `view`), so a follow-up
  `edit_artifact` can copy a snippet verbatim. Read-only; emits no canvas event.

Each call persists as a normal `tool_calls` row; created/updated artifacts ride back
on the tool result and the runner emits the `artifact` event - the canvas tab
opens/updates **live, mid-turn**, and a reload gets the same artifacts back through the
thread GET.

**How the model learns the convention.** The built-in `SystemPrompts.Canvas`
fragment rides first in every turn's system prompt (before project pinned
instructions) and tells the model to call `make_artifact` for any complete file
instead of a code block. If artifacts stop appearing for prompts that should
produce them, debug in this order: (1) is the canvas suite actually *offered* -
entitlement (`make_artifact`/`edit_artifact`/`read_artifact` ids must be in the
`gert_tools` claim, the sole grant source - [auth section tool registry](auth.md#tool-registry)),
the conversation's Canvas toggle, and a tool-capable model all gate it; (2) is
`SystemPrompts.Canvas` present in the upstream request (a stale host build);
(3) did the model paste a fenced block anyway (a model/template regression -
capture the completion and re-measure compliance). Inline code blocks stay
inline in the bubble; nothing is ever extracted from prose.

### Detached turns

A turn is **detached from the request that started it**: `POST .../messages` only
*plans* (validate -> materialize the conversation if new -> persist the user
message and a `streaming` assistant placeholder -> snapshot identity +
entitlements into a `TurnJob`) and enqueues; a `TurnWorker`
(`BackgroundService`, mirroring ingestion) runs the tool loop off-thread.
**The database is the source of truth and transports are just delivery** - the
runner's emit protocol is *persist, then publish*: allocate a per-conversation
monotonic `seq` (`conversations.next_seq`), append the event to the durable
`turn_events` log, then publish to the in-process `ConversationBus`. Clients
attach over SSE / range-polling (rest-api.md section receiving a turn) through
one splice (`ConversationStreamer`): subscribe first, replay `seq > cursor`
from the log, then drain live deduping by the seq watermark - no gaps, no
duplicates, and **generation survives the client disconnecting** (a reload
mid-turn resubscribes from the assistant row's `seq` and loses nothing).

Plan phase (request scope):

```
0. Validate (fail-closed) BEFORE any disk touch; reject a concurrent turn with 409
   (turns are serialized per conversation - the seq single-writer invariant). The
   rejection is the placeholder insert hitting the partial unique index
   ux_messages_streaming (at most one streaming row per conversation, engine-
   enforced); the planner's read check is only a fast path, and expired streaming
   rows are WRITTEN BACK to error before inserting (see the orphan rule below).
1. Materialize the conversation if new; persist the user message (status=complete)
   and the assistant placeholder (status=streaming), each with an allocated seq -
   both rows in one IMMEDIATE transaction (the gated insert above).
2. Resolve offered tools + snapshot the entitlement claim; capture history
   (complete rows only), system prompt, and the selected provider id into the
   TurnJob. Sampling is not resolved here - it rides the provider, applied by the
   adapter from `Gert:Chat:Providers`.
3. Enqueue; respond 202 { ids, seq } - the subscribe cursor.
```

Run phase (worker scope, `DetachedUserContext` seeded from the job's snapshot):

```
2. Call vLLM (stream). Every text delta is emitted LIVE as it arrives
   (persist `turn_events` row -> publish to the bus), no per-call buffering.
3. If the model emits tool_calls:
     a. emit `tool_call` (card appears)
     b. execute the tool against THIS project's resources
        - search_documents -> hybrid query (below) on this project's rag.db (docs + memory)
        - web_search       -> SearXNG
        - run_python       -> sandbox (monty by default, or gVisor)
        - make/edit/read_artifact -> this conversation's chat_objects rows (chat.db), via the host's IObjectResource
        (entitlement re-checked against the job's plan-time snapshot)
     c. emit `tool_result` + persist the tool_calls row live (with latency_ms);
        collected citations keep tool_call_id provenance
        (message -> tool_call -> citations); flush partial content to the row
     d. feed the tool result back to the model; go to 2
4. Otherwise: renumber + persist + emit citations, finalize the assistant row
   (status=complete, token_count), then emit `message_end`.
5. On any fault (model error, tool defect, or the max-turn-duration cap): finalize
   the row as status=error keeping the partial content, and emit a terminal
   `error` event. (A failed turn persists.)
```

**The orphan rule.** The turn queue is in-memory and non-durable: a crashed
worker leaves rows stuck at `streaming` forever. Every *reader*
(`MessageStatusRules`) maps a `streaming` row older than
`Gert:Turn:MaxTurnDuration` to `error` - stateless and multi-instance-safe -
and the planner's 409 check goes through the same rule, so an abandoned turn
never blocks a conversation. The planner additionally **writes the mapping
back** (`streaming -> error`, conditional on the row still being `streaming`)
before inserting a new turn's rows: readers still map lazily, but a dead turn's
row also durably frees the `ux_messages_streaming` gate index. **Both timers
share the plan-time anchor:** the reader-facing horizon ages the row from its
`CreatedAt` (stamped at plan time), and the runner caps its own wall clock at
the *remaining* budget measured from the very same instant (`TurnJob.PlannedAt`
- one clock read in the planner, not two). Time a job spends waiting in the
queue therefore counts against the turn, and a running turn always self-cancels
at or before the moment readers would start reporting its row as `error` - a
queue wait can never open a window where a healthy turn reads as dead and the
409 gate reopens against incomplete history
([decisions section 11](decisions.md#11-turn-execution---keyed-lanes-over-an-atomic-per-conversation-gate)).

**Worker topology.** `TurnWorker` drains `Gert:Turn:MaxConcurrentTurns`
(default 4) keyed lanes: jobs shard by the full `TurnKey` hash, so one
conversation's turns ride one lane in strict FIFO while different
conversations may run concurrently - the gate index is the correctness
control, the lane count only throughput
([decisions section 11](decisions.md#11-turn-execution---keyed-lanes-over-an-atomic-per-conversation-gate)).

**Scale-out.** The bus is per-process (live push is a latency optimization);
the `turn_events` log + range endpoint are the cross-instance truth, so a
client on another instance still sees everything - just less live.

Only tools that are **(a) granted to the user by the `gert_tools` JWT entitlement, (b) enabled on the conversation, (c) requested in the body, and (d) usable by the model** (a catalog entry without tool capability is never advertised tools, whatever the toggles say) are offered - the entitlement is the hard ceiling (see [Auth -> Tool entitlements](auth.md#tool-entitlements-allowed-tools-in-the-jwt)), enforced at advertise time in the planner and re-checked at execution time against the job's plan-time snapshot. So flipping "Use my docs" off removes `search_documents` for that turn, and a user without the `sandbox` entitlement never gets `run_python` advertised regardless of toggles. The request's toggles ride each send and a changed set persists onto the conversation before the intersection is computed - so flipping a tool ON mid-conversation takes effect that very turn; the conversation set is the *reload-restore* state, not a creation-time ceiling.

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

3. **Reciprocal Rank Fusion** to merge the two ranked lists into the final top-k, then join back to `chunks` + `documents` for content, `page`, filename, and score (the `0.89`, `0.81`, `0.77` in the mockup). The join-back filters to `documents.status = 'ready'`, so chunks of a failed or still-processing document are never retrievable - the read-side end of the ingestion failure cleanup below.

The result becomes the `tool_result` SSE and seeds the citations.

> **Memory rides the same query.** A project's memory entries are embedded into the *same*
> `rag.db` as its documents, distinguished only by `documents.kind = 'memory'`
> ([storage-and-data](storage-and-data.md#ragdb-sqlite-vec)). So `search_documents` retrieves
> memory and documents together with no extra plumbing; `pinned` memory is additionally
> prepended to the system prompt (loop step 0) rather than waiting to be retrieved
> ([configuration -> memory](configuration.md#23-memory)).

> **Loading sqlite-vec in .NET.** Use a SQLite build that permits extensions (`SQLitePCLRaw.bundle_e_sqlite3`), then on each `rag.db` connection:
> ```csharp
> conn.EnableExtensions(true);
> conn.LoadExtension(vecPath);   // path to vec0.so / vec0.dll on the host
> ```
> Embeddings can be bound either as packed little-endian `float32` bytes or as a JSON array string `"[0.12, -0.04, ...]"`; vec0 accepts both.

---

## Document ingestion pipeline

Upload returns immediately; the heavy work runs in a background worker fed by an in-process queue (`System.Threading.Channels` + a `BackgroundService`).

```
POST /api/projects/{pid}/documents
  └─ save file to /data/users/{key}/projects/{pid}/files/{doc-id}   (server key, no extension)
  └─ INSERT documents(status='processing')   into this project's rag.db
  └─ enqueue IngestJob{ sub, pid, doc_id, path }
  └─ 202 -> { id, status:"processing" }

IngestionWorker (per job):
  1. extract text   (PDF -> PdfPig, DOCX -> OpenXML, md/txt -> read)
  2. if no text     -> status='failed', error='no extractable text'   <- old-scan.pdf
  3. chunk          (token-aware windows w/ overlap; record page/section )
  4. embed          (batch chunks -> vLLM embeddings endpoint)
  5. write          (chunks + vec_chunks + fts_chunks), update chunk_count,
                     emitting progress ("embedding 12 / 19 chunks...")
  6. status='ready'
```

> **Hardened extraction (step 1) - isolated subprocess.** Uploads are untrusted bytes fed to large
> native/managed parsers (OpenXML: DOCX = a zip of XML; PdfPig), so step 1 runs **out-of-process in
> an unprivileged, resource-capped helper** - not the worker process. Dropped privileges, no network,
> read-only access to the one input file, hard `RLIMIT_AS`/`RLIMIT_CPU`/`RLIMIT_NPROC` + a wall-clock
> timeout, and in-process XML hardening inside it (**DTD/external-entity off** for XXE,
> **decompressed-size + zip-entry caps** for bombs). A crash/OOM/timeout fails *that document*
> (`status='failed'`), never the host - the ingestion analog of the `run_python` sandbox below.
> Failure also **deletes any already-inserted chunks** (`IngestionService.FailAsync` ->
> `DeleteChunksAsync`): chunk batches commit per batch, so without the compensation a
> mid-pipeline fault would leave a half-ingested document's chunks behind; together with the
> retrieval-side `status='ready'` join above, a failed document leaves nothing retrievable. It
> may reuse the same **gVisor (`runsc`)** lever ([security F7](security.md#3-findings--remediations),
> [tech-stack](tech-stack.md)).

Status transitions map directly to the panel pills: `processing` (amber, pulsing) -> `ready` (sage) or `failed` (brick). The SPA learns about transitions by **polling `GET /api/projects/{pid}/documents/{id}`** while a doc is processing (see [decisions.md section 6](decisions.md#6-live-ingestion-progress)); an SSE `.../documents/events` stream is a deferred, additive upgrade.

`DELETE /api/projects/{pid}/documents/{id}` removes the `chunks` (cascade clears `vec_chunks`/`fts_chunks` via triggers or explicit deletes) and unlinks the original file.

---

## Tools detail

### RAG

See [RAG: hybrid retrieval](#rag-hybrid-retrieval) above.

### Web search (SearXNG)
Server-side `HttpClient` (via `IHttpClientFactory`) to the SearXNG JSON API. Take the top results, optionally fetch + summarize, keep the few that matter (the mockup shows "1 result kept"), and return titles/URLs as web-type citations.

> **SSRF guard on the fetch step.** The summarize step pulls a result URL server-side, and that URL
> is attacker-influenceable (search results, or prompt-injected document content steering the
> model's query) - so the fetcher is hardened ([security F5](security.md#3-findings--remediations)):
> **`http`/`https` only** (no `file:`/`gopher:`/etc.); resolve the host and **block private,
> loopback, link-local, and unique-local ranges** (IPv4 *and* IPv6, including the cloud metadata
> IP), **re-checking after every redirect**; and cap response size, time, and redirect count. The
> fetcher must never reach vLLM, SearXNG, or any other internal service.

### Web fetch (`web_fetch`)

`web_fetch(url, max_chars?)` pulls **one model-named URL** server-side and
returns its readable content, clipped to `max_chars` (default 8 000, hard
ceiling 20 000 - larger asks are clamped, never errored; the byte-level cap
stays the fetcher's `MaxFetchBytes`). An HTML body is reduced to plain text
first (`HtmlTextExtractor`: a single-pass scanner - script/style/head subtrees
dropped whole, headings/lists kept as `#`/`-` markers, entities decoded only
after every tag is gone so encoded markup stays content; the output is data
and is never re-parsed as HTML). Non-HTML bodies (JSON, plain text) pass
through raw. The clip applies **after** extraction so it spends on content,
not boilerplate. One web-type citation is seeded for the fetched URL,
mirroring web search.

> **Same SSRF guard, same knobs.** The tool only calls the `IWebFetcher` port;
> the adapter wraps the **same hardened fetcher** as web search's page pulls
> ([security F5](security.md#3-findings--remediations) - the box above), and it
> deliberately shares the `Gert:Tools:Search` size/time/redirect caps rather than
> growing a parallel set. A **policy block or HTTP failure is a tool error the
> model reads** (card-visible, exactly like a sandbox error) - never a turn
> fault: the model fetched an attacker-influenceable URL, so the refusal must
> be visible, not fatal.

### Save memory (`save_memory`)

`save_memory(title, content)` is the write side of memory: it calls the same
`MemoryService.UpsertAsync` the knowledge panel's POST uses, so the entry is
chunked + embedded into the project's `rag.db` as `kind='memory'` and is
**immediately retrievable by `search_documents`**
([memory rides the same query](#rag-hybrid-retrieval)). The embed runs inline
(one vLLM embeddings round-trip) - comfortably inside the generic
`ToolCallTimeout`.

Two deliberate restrictions:

- **No `pinned` argument.** Pinned entries are prepended to every future
  system prompt; letting the model pin would let one turn quietly steer all
  later ones, so pinning stays a human action in the knowledge panel.
- **No dedup.** Each call creates a NEW entry (the DTO carries no id; editing
  is add + delete at the host) - the tool description warns the model to never
  re-save something it already saved this conversation. A validation failure
  (title > 200 chars, content > 100 000) returns a correctable tool error the
  model can shorten and retry.

### Sandbox - security-critical
`run_python` runs untrusted, model-written code - the strongest blast radius in the system ([security F5](security.md#3-findings--remediations)). Two backends sit behind one `IPythonSandbox` port, chosen by the operator (`Gert:Tools:Sandbox:Type`):

- **monty** *(default)* - [Pydantic Monty](https://github.com/pydantic/monty), a minimal Python interpreter written in Rust with **no syscalls**: the language itself has no filesystem, network, or env access, so untrusted code can only reach the world through host callbacks (plain `run_python` grants none - that arrives with code-mode). It runs in a **sidecar process** Gert reaches server-side over HTTP ([tools/monty](../../tools/monty/README.md)) - unprivileged, no `/data`, egress off: monty's capability sandbox nested in an OS sandbox. Microsecond startup, no container, no infra. A Python *subset* (no classes/`match`, small stdlib) - fine for "calculations and quick data transforms"; an unsupported construct returns an error the model reads.
- **gVisor (`runsc`)** - an **ephemeral container** running real CPython, for workloads needing the full language/stdlib: no inbound network, **outbound off by default** (an allow-list is opt-in only, never the default), read-only rootfs, a small writable `/tmp`, container destroyed after the call. Needs gVisor on the host.

Both backends share the posture: **no mount of `/data`** (the sandbox must never see another user's - or this user's - DB files), a hard wall-clock + memory cap, and only captured `stdout`/`stderr` returns. Because the sandbox has no filesystem access to user data, code execution stays isolated from the per-user storage model.

### Sub-agent (`run_sub_agent`)

`run_sub_agent(task, context?)` delegates a **self-contained task to a fresh
nested conversation** against the same provider and waits for its final
answer. The sub-agent sees nothing of the parent conversation - the task (plus
optional context) is its whole world - and the parent sees nothing of the
sub-agent's intermediate work: its searches, fetched pages, and tool rounds
stay in the nested loop, and **only the final text returns** as the tool
result. That asymmetry is the point: a context-hungry side quest (digest these
pages, survey this topic) costs the parent one tool result instead of a
transcript.

- **Nested tools = delegable AND entitled.** The sub-agent may use a fixed
  read-only subset (`rag`, `search`, `fetch`, `clock`), intersected with the
  parent turn's entitlement snapshot - the claim stays the ceiling
  ([auth](auth.md)) at every nesting depth. `run_sub_agent` is not in the
  delegable set, so delegation cannot recurse; nested invocations carry no
  `ModelId`, so a nested tool could not re-delegate even if it were.
- **Budget.** The nested loop is bounded three ways: its own round cap (16,
  far below the parent's `MaxToolRounds`), the per-round `MaxTokensPerRound`,
  and the parent turn's deadline minus a grace slice - the wait is exempt from
  the generic `ToolCallTimeout` (the `IInteractiveTool` marker, like
  `ask_user`) because a delegated task legitimately outlives 60 s. Running out
  of rounds or time degrades to a model-readable error result, never a turn
  fault.
- **Invisible by design.** Nested tool calls emit no events and persist no
  `tool_calls` rows; the parent's single `run_sub_agent` card (with the final
  answer as its output) is the user-visible record.

### Ask the user (`ask_user`)

`ask_user(questions[])` lets the model ask the user **up to four clarifying
questions mid-turn and block until they are all answered, the wait times out, or
the turn is cancelled**. Each question is `{ question, header?, options?,
allow_free_text? }`; the SPA renders them as **tabs** (one tab per question, the
`header` as its label, falling back to "Question N"), collects one answer per
tab, and submits them together once every question has an answer. No external
world: the questions travel as a single `question_asked` event (the one tool
that emits mid-execution, through the optional `ToolInvocation.EmitAsync` seam
the runner populates with its own persist-then-publish emit), and the answers
arrive through the singleton `ITurnQuestions` registry - the awaitable mirror of
the cancel registry, keyed by the same tenant-scoped `TurnKey` - from
[`POST .../answer`](rest-api.md#answer-a-question) as an `answers[]` in question
order. Per question, `allow_free_text` defaults to true for an open question and
false once `options` (max 8) are offered; a closed question's answer must be one
of its options (a runtime check in `TurnQuestions`). The successful tool result
pairs each question with its answer (`{answered:true, answers:[{question,
answer}, ...]}`) so the model knows which reply belongs to which prompt.

**Wait budget.** The wait is exempt from the generic `ToolCallTimeout`
backstop (the `IInteractiveTool` marker - a 60 s cap would kill every wait) and
instead runs for **min(`Gert:Turn:AskUserTimeout` (default 5 min), remaining
turn budget - a 15 s grace)**, where the remaining budget is anchored at
`TurnJob.PlannedAt` exactly like the runner's lifetime cap - so the graceful
path always wins over the turn-budget error finalize. A **timeout is a
successful tool result** (`{"answered":false,"reason":"timeout"}`, card line
"The user did not respond."), never a turn fault: on a detached (client-gone)
turn the question simply expires and the turn continues - the detached-turn
guarantee is preserved. A user **cancel** (`POST .../cancel`) cancels the
wait and finalizes the turn `cancelled` like any other; the pending question is
always released.

**Replay.** A pending question lives only in `turn_events` (the `tool_calls`
row lands when the call returns), so a reconnecting client recovers it through
the resume replay: `tool_call(ask_user)` -> `question_asked` with nothing after
=> render the interactive (tabbed) card; a following `question_answered` (or the
call's `tool_result`) => render the resolved/expired state. After the turn ends,
the thread GET rebuilds a read-only card from the persisted row
(`{answered, answers | reason}`).

**UX consequence.** While the question pends the turn is in-flight, so the 409
rule blocks new sends in that conversation - the question card (or Stop) is
the only input. One question card per turn: a second `ask_user` while one pends
is a tool error the model reads (so it must batch its questions into the single
call's `questions[]`).
