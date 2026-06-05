# REST API

All endpoints require a valid bearer token except `GET /healthz`. JSON in/out unless noted. The user is always implicit (from the token) — there is **no** `userId` in any path.

Most data is **project-scoped**, so conversations, messages, documents, and artifacts live under `/api/projects/{pid}/…`, where `pid` is a project UUID or the literal `default`. Unlike the user (which comes only from the token), `pid` comes from the path — but it is validated and only ever resolved *inside* the token-derived user folder, so it selects among *this* user's projects and can never reach another user's data ([configuration.md §2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe)). The model catalog, user settings, and admin are **not** project-scoped.

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

## Settings (user-level)

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/settings` | The user's preferences from `settings.json`: theme, UI language, default reply language, default model, default tools, memory mode ([configuration §3](configuration.md#3-user-settings)). |
| `PUT` | `/api/settings` | Update any subset. |

## Projects

| Method | Path | Body / notes |
|--------|------|--------------|
| `GET` | `/api/projects` | List the user's projects (reads `projects/*/meta.json`): id, name, counts, updated_at. |
| `POST` | `/api/projects` | `{ name, description?, instructions?, defaults? }` → a new isolated project folder. |
| `GET` | `/api/projects/{pid}` | Project config + counts (conversations, documents, memory). |
| `PATCH` | `/api/projects/{pid}` | `{ name?, description?, instructions?, defaults? }` (rename / edit instructions / defaults). |
| `DELETE` | `/api/projects/{pid}` | **`rm -rf projects/{pid}`** — its chats *and* documents. `default` is emptied, not removed ([configuration §5](configuration.md#5-data-lifecycle-user-facing)). |

`defaults` = `{ model_id?, tools?, params?, reply_language? }` — the project-level entries in the [configuration cascade](configuration.md#1-the-configuration-cascade).

## Conversations

Scoped to a project. `pid` may be `default`.

| Method | Path | Body / notes |
|--------|------|--------------|
| `GET` | `/api/projects/{pid}/conversations` | List (id, title, model, updated_at, archived). Client groups into Today/Yesterday/Earlier. |
| `POST` | `/api/projects/{pid}/conversations` | `{ title?, model_id?, tools?, params? }` → new conversation (unset fields inherit the project/user defaults). |
| `GET` | `/api/projects/{pid}/conversations/{id}` | Full thread: messages + tool_calls + citations + artifacts. |
| `PATCH` | `/api/projects/{pid}/conversations/{id}` | `{ title?, model_id?, tools?, params?, archived? }` (rename / switch model / toggle tools). |
| `DELETE` | `/api/projects/{pid}/conversations/{id}` | Cascade-deletes messages, tool calls, citations, artifacts. |

## Sending a message (detached turn)

```
POST /api/projects/{pid}/conversations/{id}/messages
```
Body:
```json
{ "content": "should I use Qdrant or sqlite-vec?",
  "model_id": "qwen3-27b-fp8-mtp",
  "tools": { "rag": true, "search": true, "sandbox": false } }
```

Responds **202 Accepted** — the turn runs *detached* on a background worker
([chat-and-tools.md § detached turns](chat-and-tools.md#detached-turns)); the
body carries the persisted ids and the **subscribe cursor**:

```json
{ "conversation_id": "…", "user_message_id": "…",
  "assistant_message_id": "…", "seq": 42 }
```

A second POST while the conversation's latest assistant row is still
`streaming` returns **409 Conflict** (turns are serialized per conversation;
the SPA disables the composer while streaming).

## Receiving a turn

Every event of a conversation carries a per-conversation monotonic **`seq`** —
one cursor for pagination, catch-up, and resume. Three delivery views over the
same durable `turn_events` log; pick by capability, fall back freely (the seq
watermark makes switching transports gap- and duplicate-free):

```
GET /api/projects/{pid}/conversations/{id}/events?after={seq}&limit={n}   range / poll
GET /api/projects/{pid}/conversations/{id}/stream?after={seq}            SSE (live)
GET /api/projects/{pid}/conversations/{id}/ws                            WebSocket (live)
```

* **Range** returns `{ "events": [{ "seq": n, "event": { … } }], "next_cursor": n, "has_more": b }`
  — always served from `chat.db`, correct across instances/restarts; also the
  polling fallback.
* **SSE stream** frames are `id: <seq>\nevent: <type>\ndata: <chatEvent json>\n\n`;
  replays `seq > after` from the log, then tails live. This is the
  dev-proxy-compatible path.
* **WS** authenticates via the bearer **subprotocol**
  (`new WebSocket(url, ["bearer", token])` — a browser WS cannot send an
  Authorization header and the token never goes in the URL; the server lifts it
  into the normal JwtBearer pipeline). Client messages are JSON with `type`:
  `{"type":"subscribe","after":42}` (replay-then-live) and
  `{"type":"range","after":0,"limit":200}`; server frames carry `kind`:
  `{"kind":"event","seq":n,"event":{…}}` / `{"kind":"range", …}` /
  `{"kind":"error","message":"…"}`. Unknown/malformed client messages are
  ignored. *(Future: `{"type":"cancel"}` for client-initiated turn abort.)*

The `event` payload is the ChatEvent union (the `$type` field matches the SSE
`event:` name). This is what drives the mockup's tool cards → streamed text →
footnotes sequence:

| type | payload | UI effect |
|----------|----------------|-----------|
| `message_start` | `{ "message_id": "…" }` | creates the assistant bubble |
| `tool_call` | `{ "id","kind":"rag","status":"running","request":{"query":"…"} }` | renders a tool card with the spinner node |
| `tool_result` | `{ "id","kind":"rag","status":"done","latency_ms":142, "hits":[{"doc":"qdrant-benchmarks.pdf","page":"p.4","score":0.89}], "stdout":"…?", "todos":[{"text":"…","status":"pending\|active\|done"}]? }` | fills the card's doc-hit rows / stdout pre / todo checklist |
| `delta` | `{ "text": "Short version: " }` | typewriter token append |
| `citation` | `{ "ordinal":1, "label":"qdrant-benchmarks.pdf · p.4", "doc_id":"…" }` | the `[1]` marker + footnote |
| `artifact` | `{ "id","kind":"md","name":"decision.md","content":"…" }` | opens a canvas tab |
| `message_end` | `{ "token_count":312 }` | removes caret |
| `error` | `{ "message":"…" }` | inline error; the assistant row persists as `status="error"` |

Every event is persisted **before** it is published, so reloading a
conversation reproduces the same cards, citations, and artifacts — and a reload
*mid-turn* resubscribes from the streaming assistant row's `seq` and loses
nothing (thread messages carry `status` + `seq` for exactly this).

> **Why both SSE and WS:** SSE survives plain-HTTP proxies (the dev proxy does
> not upgrade WebSockets) and needs no auth workaround; WS adds the
> client→server channel (range backfill today, mid-generation cancel later).
> Both are thin subscribers over the same log + bus splice — the SPA tries WS,
> falls back to SSE, then to range polling.

## Documents (knowledge panel)

Scoped to a project — a document belongs to exactly one project's `rag.db`.

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/projects/{pid}/documents` | List for the doclist: name, size, chunk_count, status, error. |
| `POST` | `/api/projects/{pid}/documents` | `multipart/form-data` upload. Stores the file, inserts `documents` row with `status='processing'`, enqueues ingestion, returns immediately. |
| `GET` | `/api/projects/{pid}/documents/{id}` | **Polled** by the client while processing → drives `processing → ready/failed` pills and "embedding 12 / 19 chunks…". Returns `status` and a progress field (e.g. `chunk_count` / chunks embedded). |
| `DELETE` | `/api/projects/{pid}/documents/{id}` | Deletes chunks + vec rows + fts rows + the original file. |
| `GET` | `/api/projects/{pid}/documents/events` | *(deferred — see [decisions.md §6](decisions.md#6-live-ingestion-progress))* SSE stream of ingestion progress; additive future upgrade over polling. |

## Memory (per project)

Memory entries are stored as files under `projects/{pid}/memory/` and embedded into the project's `rag.db` as `kind='memory'` ([configuration §2.3](configuration.md#23-memory)).

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/projects/{pid}/memory` | List entries (id, title, pinned, updated_at). |
| `POST` | `/api/projects/{pid}/memory` | `{ title, content, pinned? }` — add/edit an entry; it is (re)embedded for retrieval. |
| `DELETE` | `/api/projects/{pid}/memory/{id}` | Remove an entry and its chunks. |

## Artifacts

Artifacts are produced during chat (see [Chat orchestration](chat-and-tools.md#chat-orchestration-the-tool-loop)) and stored in the project's `chat.db`. They are returned inline with the thread, plus:

```
GET    /api/projects/{pid}/conversations/{id}/artifacts   # list for the canvas tab strip
GET    /api/projects/{pid}/artifacts/{id}                 # raw content (download / "Source" view)
```

## Account & data

Self-service data lifecycle ([configuration §5](configuration.md#5-data-lifecycle-user-facing)). Identity removal is Pocket ID's, not the API's.

| Method | Path | Notes |
|--------|------|-------|
| `POST` | `/api/projects/{pid}/forget-documents` | Wipe a project's `rag.db` (+ `files/`), keep its chats. |
| `GET` | `/api/projects/{pid}/export` | Download one project: conversations (JSON/Markdown) + original files. |
| `GET` | `/api/account/export` | Download everything — all projects. |
| `DELETE` | `/api/account` | **`rm -rf users/{key}`** — erases all of this user's data. Does **not** remove the Pocket ID account ([operations → user lifecycle](operations.md#user-lifecycle--remove-a-user--remove-a-folder)). |

## Admin (requires `Admin` policy)

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/admin/users` | Lists user folders by reading each `meta.json`: username, key, size, doc count, last-active. The closest thing to a "user list" the API has. |
| `GET` | `/api/admin/users/{key}` | One user's folder summary. `{key}` validated as below. |
| `DELETE` | `/api/admin/users/{key}` | **`rm -rf /data/users/{key}`.** Removes all of that user's data (see [Operations → User lifecycle](operations.md#user-lifecycle--remove-a-user--remove-a-folder)). |

> **`{key}` is the most dangerous path parameter in the API** — it feeds a `rm -rf`. Unlike `pid`
> (whose IDOR-safety is covered in [configuration §2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe)),
> `{key}` is **not** scoped under a token-derived folder, so it gets its own guard: it **must match
> `^[0-9a-f]{64}$`** (a sha256 hex) before any path-join, and the resolved absolute path is asserted
> to sit **under `/data/users/`** before the delete runs. A non-hex / `..` / absolute value is
> rejected outright — never path-joined ([security F6](security.md#3-findings--remediations), tested
> in [testing §5/§6](testing.md#validation--the-input-security-boundary)).

The API **cannot create users** — Pocket ID does. Admin here is purely data lifecycle and usage visibility.

## Not public

SearXNG, the gVisor sandbox, and vLLM are reached **only** server-side as part of the chat orchestration. They are never proxied directly to the browser, so secrets and execution stay inside the API boundary.
