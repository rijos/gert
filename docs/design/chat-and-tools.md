# Chat orchestration, RAG, ingestion & tools

## Chat orchestration (the tool loop)

vLLM exposes an **OpenAI-compatible** `/v1/chat/completions` with function calling and streaming, so the orchestrator can use a standard OpenAI client pointed at the model's base URL.

The API advertises three tools to the model:

```jsonc
[
  { "name":"search_documents", "description":"Hybrid search over this project's private docs + memory",
    "parameters": { "query":"string", "k":"integer" } },
  { "name":"web_search", "description":"Search the web via SearXNG",
    "parameters": { "query":"string" } },
  { "name":"run_python", "description":"Execute Python in a sandbox, return stdout",
    "parameters": { "code":"string" } }
]
```

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
