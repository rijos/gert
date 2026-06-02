# Chat orchestration, RAG, ingestion & tools

## Chat orchestration (the tool loop)

vLLM exposes an **OpenAI-compatible** `/v1/chat/completions` with function calling and streaming, so the orchestrator can use a standard OpenAI client pointed at the model's base URL.

The API advertises three tools to the model:

```jsonc
[
  { "name":"search_documents", "description":"Hybrid search over the user's private docs",
    "parameters": { "query":"string", "k":"integer" } },
  { "name":"web_search", "description":"Search the web via SearXNG",
    "parameters": { "query":"string" } },
  { "name":"run_python", "description":"Execute Python in a sandbox, return stdout",
    "parameters": { "code":"string" } }
]
```

Loop:

```
1. Append the user message; load prior turns from chat.db (trim to context window).
2. Call vLLM (stream).
3. If the model emits tool_calls:
     a. emit `tool_call` SSE (card appears)
     b. execute the tool against THIS user's resources
        - search_documents → hybrid query (below) on this user's rag.db
        - web_search       → SearXNG
        - run_python       → gVisor sandbox
     c. emit `tool_result` SSE + persist a tool_calls row (with latency_ms)
     d. feed the tool result back to the model; go to 2
4. Otherwise stream `delta` tokens to the client, extract citations,
   detect/extract artifacts, then emit `message_end`.
5. Persist the assistant message, citations, and artifacts.
```

Only tools that are **(a) granted to the user by the `gert_tools` JWT entitlement, (b) enabled on the conversation, and (c) requested in the body** are offered to the model — the entitlement is the hard ceiling (see [Auth → Tool entitlements](auth.md#tool-entitlements-allowed-tools-in-the-jwt)). So flipping "Use my docs" off removes `search_documents` for that turn, and a user without the `sandbox` entitlement never gets `run_python` advertised regardless of toggles.

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
POST /api/documents
  └─ save file to /data/users/{key}/files/{doc-id}.{ext}
  └─ INSERT documents(status='processing')
  └─ enqueue IngestJob{ sub, doc_id, path }
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

Status transitions map directly to the panel pills: `processing` (amber, pulsing) → `ready` (sage) or `failed` (brick). The SPA learns about transitions by **polling `GET /api/documents/{id}`** while a doc is processing (see [decisions.md §6](decisions.md#6-live-ingestion-progress)); an SSE `GET /api/documents/events` stream is a deferred, additive upgrade.

`DELETE /api/documents/{id}` removes the `chunks` (cascade clears `vec_chunks`/`fts_chunks` via triggers or explicit deletes) and unlinks the original file.

---

## Tools detail

### RAG

See [RAG: hybrid retrieval](#rag-hybrid-retrieval) above.

### Web search (SearXNG)
Server-side `HttpClient` (via `IHttpClientFactory`) to the SearXNG JSON API. Take the top results, optionally fetch + summarize, keep the few that matter (the mockup shows "1 result kept"), and return titles/URLs as web-type citations.

### Sandbox (gVisor) — security-critical
Each `run_python` call executes in an **ephemeral gVisor (`runsc`) container**:

- no inbound network; outbound disabled or tightly allow-listed,
- read-only rootfs, a small writable `/tmp`, **no mount of `/data`** (the sandbox must never see another user's — or this user's — DB files),
- CPU/memory/PID limits and a hard wall-clock timeout,
- container destroyed after the call; only captured `stdout`/`stderr` returns.

Because the sandbox has no filesystem access to user data, code execution stays isolated from the per-user storage model.
