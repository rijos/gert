# REST API

All endpoints require a valid bearer token except `GET /healthz`. JSON in/out unless noted. The user is always implicit (from the token) — there is **no** `userId` in any path.

## Models

```
GET /api/models
```
Returns the configured vLLM models for the picker (id, display name, endpoint, capabilities, context window).

```json
[
  { "id":"qwen3-27b-fp8-mtp", "name":"Qwen3-27B FP8", "default":true,
    "capabilities":["tools","vision"], "context":131072 },
  { "id":"llama-3.3-70b-instruct", "name":"Llama-3.3-70B",
    "capabilities":["tools"], "context":131072 },
  { "id":"qwen3-4b", "name":"Qwen3-4B", "fast":true,
    "capabilities":["tools"], "context":32768 }
]
```

## Conversations

| Method | Path | Body / notes |
|--------|------|--------------|
| `GET` | `/api/conversations` | List (id, title, model, updated_at, archived). Client groups into Today/Yesterday/Earlier. |
| `POST` | `/api/conversations` | `{ title?, model_id, tools }` → new conversation. |
| `GET` | `/api/conversations/{id}` | Full thread: messages + tool_calls + citations + artifacts. |
| `PATCH` | `/api/conversations/{id}` | `{ title?, model_id?, tools?, archived? }` (rename / switch model / toggle tools). |
| `DELETE` | `/api/conversations/{id}` | Cascade-deletes messages, tool calls, citations, artifacts. |

## Sending a message (streaming)

```
POST /api/conversations/{id}/messages
Accept: text/event-stream
```
Body:
```json
{ "content": "should I use Qdrant or sqlite-vec?",
  "model_id": "qwen3-27b-fp8-mtp",
  "tools": { "rag": true, "search": true, "sandbox": false } }
```

Responds with **Server-Sent Events** (SSE). Each line is `event: <type>\ndata: <json>\n\n`. This is what drives the mockup's tool cards → streamed text → footnotes sequence:

| `event:` | `data` payload | UI effect |
|----------|----------------|-----------|
| `message_start` | `{ "message_id": "…" }` | creates the assistant bubble |
| `tool_call` | `{ "id","kind":"rag","status":"running","request":{"query":"…"} }` | renders a tool card with the spinner node |
| `tool_result` | `{ "id","kind":"rag","status":"done","latency_ms":142, "hits":[{"doc":"qdrant-benchmarks.pdf","page":"p.4","score":0.89}] }` | fills the card's doc-hit rows |
| `delta` | `{ "text": "Short version: " }` | typewriter token append |
| `citation` | `{ "ordinal":1, "label":"qdrant-benchmarks.pdf · p.4", "doc_id":"…" }` | the `[1]` marker + footnote |
| `artifact` | `{ "id","kind":"md","name":"decision.md","content":"…" }` | opens a canvas tab |
| `message_end` | `{ "token_count":312 }` | removes caret |
| `error` | `{ "message":"…" }` | inline error |

Everything emitted is also persisted to `chat.db` as the stream completes, so reloading the conversation reproduces the same cards, citations, and artifacts.

> **Why SSE, not WebSocket:** token streaming is one-directional server→client; SSE is simpler over plain HTTP, survives proxies well, and maps 1:1 to the event types above. ASP.NET Core implements it by writing `text/event-stream` to `Response.Body` (or via `IAsyncEnumerable` results). Use WebSocket only if you later need client→server mid-generation control (e.g. interrupt).

## Documents (knowledge panel)

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/documents` | List for the doclist: name, size, chunk_count, status, error. |
| `POST` | `/api/documents` | `multipart/form-data` upload. Stores the file, inserts `documents` row with `status='processing'`, enqueues ingestion, returns immediately. |
| `GET` | `/api/documents/{id}` | **Polled** by the client while processing → drives `processing → ready/failed` pills and "embedding 12 / 19 chunks…". Returns `status` and a progress field (e.g. `chunk_count` / chunks embedded). |
| `DELETE` | `/api/documents/{id}` | Deletes chunks + vec rows + fts rows + the original file. |
| `GET` | `/api/documents/events` | *(deferred — see [decisions.md §6](decisions.md#6-live-ingestion-progress))* SSE stream of ingestion progress; additive future upgrade over polling. |

## Artifacts

Artifacts are produced during chat (see [Chat orchestration](chat-and-tools.md#chat-orchestration-the-tool-loop)) and stored in `chat.db`. They are returned inline with the thread, plus:

```
GET    /api/conversations/{id}/artifacts   # list for the canvas tab strip
GET    /api/artifacts/{id}                 # raw content (download / "Source" view)
```

## Admin (requires `Admin` policy)

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/admin/users` | Lists user folders by reading each `meta.json`: username, key, size, doc count, last-active. The closest thing to a "user list" the API has. |
| `DELETE` | `/api/admin/users/{key}` | **`rm -rf /data/users/{key}`.** Removes all of that user's data (see [Operations → User lifecycle](operations.md#user-lifecycle--remove-a-user--remove-a-folder)). |

The API **cannot create users** — Pocket ID does. Admin here is purely data lifecycle and usage visibility.

## Not public

SearXNG, the gVisor sandbox, and vLLM are reached **only** server-side as part of the chat orchestration. They are never proxied directly to the browser, so secrets and execution stay inside the API boundary.
