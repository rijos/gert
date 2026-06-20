# REST API

All endpoints require a valid bearer token except the probes - `GET /healthz` (liveness) and `GET /readyz` (readiness: upstream reachability). JSON in/out unless noted. The user is always implicit (from the token) - there is **no** `userId` in any path.

Most data is **project-scoped**, so conversations, messages, documents, and artifacts live under `/api/projects/{pid}/...`, where `pid` is a project UUID or the literal `default`. Unlike the user (which comes only from the token), `pid` comes from the path - but it is validated and only ever resolved *inside* the token-derived user folder, so it selects among *this* user's projects and can never reach another user's data ([configuration.md section 2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe)). The model catalog, user settings, and admin are **not** project-scoped.

## Models

```
GET /api/models
```
Returns the configured **chat providers** for the picker - one entry per
`Gert:Chat:Providers` slug, in configured order ([configuration section 4](configuration.md#4-llm-providers--models),
[installation section providers](../installation/configuration.md#4-gertchatproviders---the-chat-provider-catalog)).
Each `id` is the provider slug (the conversation stores it as `model_id`); the
connection + sampling stay host-side. The same physical model often appears under
several slugs (a thinking preset and an instruct preset).

```json
[
  { "id":"qwen36-thinking", "name":"Qwen 3.6 - thinking", "default":true,
    "capabilities":["tools","vision"], "context":131072 },
  { "id":"qwen36-instruct", "name":"Qwen 3.6 - instruct",
    "capabilities":["tools","vision"], "context":131072 },
  { "id":"qwen3-4b", "name":"Qwen3-4B", "fast":true,
    "capabilities":["tools"], "context":32768 }
]
```

## Tools

```
GET /api/tools
```
Returns the tools **this caller is entitled to** - the catalog the composer's
tools popup renders. It is the registered tools filtered by the caller's
`gert_tools` claim, the **same hard ceiling** the turn planner applies
([auth section enforcement](auth.md#enforcement---the-claim-is-the-ceiling)), so the
popup can never offer a tool the model would be denied: a `limited` token sees
fewer rows, a token with no `gert_tools` claim sees an empty list. The user is
implicit (token); nothing is request-supplied. Per-user, so `no-store`.

Each entry is `{ id, name, description, tool_type }`: `id` is the capability id
(== the `gert_tools` entitlement name, e.g. `rag`); `name` is the model-facing
function name (`search_documents`) - the SPA maps `id` to a friendly label
client-side; `tool_type` is the execution flow (`standard` | `modal`), which the
popup uses to derive its groupings (the Canvas trio, the rag "Use my docs" row).

```json
[
  { "id":"ask_user", "name":"ask_user", "description":"Ask the user...", "tool_type":"modal" },
  { "id":"clock", "name":"get_time", "description":"Get the current time...", "tool_type":"standard" },
  { "id":"rag", "name":"search_documents", "description":"Search the user's documents...", "tool_type":"standard" }
]
```

The per-conversation on/off toggles are a separate, persisted concern: the popup
writes them through the conversation's `tools` map (the `ToolToggles` DTO on
`POST .../messages` and `PATCH .../conversations/{id}`), bounded by this catalog.

## Settings (user-level)

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/settings` | The user's preferences from `user.db` ([storage-and-data section user.db](storage-and-data.md#userdb)): theme, UI language, default reply language, default provider, default tools, memory mode ([configuration section 3](configuration.md#3-user-settings)). Sampling is not a user setting - it rides the selected provider. |
| `PUT` | `/api/settings` | Update any subset (merge: absent fields stay, each supplied field overrides). |

## Projects

| Method | Path | Body / notes |
|--------|------|--------------|
| `GET` | `/api/projects` | List the user's projects (the `user.db` registry): id, name, counts, updated_at. Optional `?q=` (name contains) + `?limit=`/`?offset=` (limit 0 = all, capped at 100) page the full-screen search's infinite scroll. |
| `POST` | `/api/projects` | `{ name, description?, instructions?, defaults? }` -> a new isolated project folder. |
| `GET` | `/api/projects/{pid}` | Project config + counts (conversations, documents, memory). |
| `PATCH` | `/api/projects/{pid}` | `{ name?, description?, instructions?, defaults? }` (rename / edit instructions / defaults). |
| `DELETE` | `/api/projects/{pid}` | **Drops the whole project** - its chats *and* documents (chat.db + rag.db + blobs together). `default` is emptied, not removed ([configuration section 5](configuration.md#5-data-lifecycle-user-facing)). |

`defaults` = `{ model_id?, tools?, reply_language? }` - the project-level entries in the [configuration cascade](configuration.md#1-the-configuration-cascade). (`model_id` is a provider slug; sampling is not a default - it rides the provider.)

## Conversations

Scoped to a project. `pid` may be `default`.

| Method | Path | Body / notes |
|--------|------|--------------|
| `GET` | `/api/projects/{pid}/conversations` | List (id, title, model, updated_at, archived), newest first. Optional `?q=` (title contains) + `?limit=`/`?offset=` (limit 0 = all, capped at 100) page the full-screen search's infinite scroll. Client groups into Today/Yesterday, then per-date headers. |
| `POST` | `/api/projects/{pid}/conversations` | `{ title?, model_id?, tools? }` -> new conversation (`model_id` = provider slug; unset fields inherit the project/user defaults). |
| `GET` | `/api/projects/{pid}/conversations/{id}` | Full thread: messages + tool_calls + citations + artifacts. |
| `PATCH` | `/api/projects/{pid}/conversations/{id}` | `{ title?, model_id?, tools?, archived? }` (rename / switch provider / toggle tools). |
| `DELETE` | `/api/projects/{pid}/conversations/{id}` | Cascade-deletes messages, tool calls, citations, artifacts. |
| `POST` | `/api/projects/{pid}/conversations/{id}/move` | `{ target_pid }` - relocate the thread (messages, tool calls, citations, artifacts) to another of the caller's projects. Copy-then-delete (target first, compensated on failure), messages re-sequenced through the target's counter; the turn-event log stays behind (it serves live resume only). 409 while a turn streams; 400 when the target already holds it. |

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

Optional `attachments` carry pasted images inline (vision input) - up to 6 of
`{ "mime_type": "image/png|image/jpeg|image/webp|image/gif", "data": "<base64>" }`.
With attachments present, `content` may be empty (an image alone is a message).
They persist on the user row (`messages.attachments_json`), come back verbatim
on the thread GET, and ride upstream as OpenAI-style `image_url` data-URL
content parts - for models the catalog gates as non-vision (a declared
capability list without `"vision"`), the prompt degrades to text-only rather
than erroring the turn.

Responds **202 Accepted** - the turn runs *detached* on a background worker
([chat-and-tools.md section detached turns](chat-and-tools.md#detached-turns)); the
body carries the persisted ids and the **subscribe cursor**:

```json
{ "conversation_id": "...", "user_message_id": "...",
  "assistant_message_id": "...", "seq": 42 }
```

A second POST while the conversation's latest assistant row is still
`streaming` returns **409 Conflict** (turns are serialized per conversation;
the SPA disables the composer while streaming).

## Receiving a turn

Every event of a conversation carries a per-conversation monotonic **`seq`** -
one cursor for pagination, catch-up, and resume. Two delivery views over the
same durable `turn_events` log; pick by capability, fall back freely (the seq
watermark makes switching transports gap- and duplicate-free):

```
GET /api/projects/{pid}/conversations/{id}/events?after={seq}&limit={n}   range / poll
GET /api/projects/{pid}/conversations/{id}/stream?after={seq}            SSE (live)
```

* **Range** returns `{ "events": [{ "seq": n, "event": { ... } }], "next_cursor": n, "has_more": b }`
  - always served from `chat.db`, correct across instances/restarts; also the
  polling fallback.
* **SSE stream** frames are `id: <seq>\nevent: <type>\ndata: <chatEvent json>\n\n`;
  replays `seq > after` from the log, then tails live. This is the
  dev-proxy-compatible path.

The `event` payload is the ChatEvent union (the `$type` field matches the SSE
`event:` name). This is what drives the mockup's tool cards -> streamed text ->
footnotes sequence:

| type | payload | UI effect |
|----------|----------------|-----------|
| `message_start` | `{ "message_id": "..." }` | creates the assistant bubble |
| `tool_call` | `{ "id","kind":"rag","status":"running","request":{"query":"..."} }` | renders a tool card inside the message's activity dropdown |
| `tool_result` | `{ "id","kind":"rag","status":"done","latency_ms":142, "hits":[{"doc":"qdrant-benchmarks.pdf","page":"p.4","score":0.89}], "stdout":"...?", "todos":[{"text":"...","status":"pending\|active\|done"}]? }` | fills the card's doc-hit rows / stdout pre / todo checklist |
| `delta` | `{ "text": "Short version: " }` | typewriter token append |
| `citation` | `{ "ordinal":1, "label":"qdrant-benchmarks.pdf - p.4", "doc_id":"..." }` | the `[1]` marker + footnote |
| `artifact` | `{ "id","kind":"md","name":"decision.md","content":"..." }` | opens a canvas tab |
| `question_asked` | `{ "id","question_id","questions":[{"question":"Which color?","header":"Color","options":["red","blue"],"allow_free_text":false}] }` | renders the interactive question card (tabs, one per entry) on the `ask_user` call's card (`id` = the tool-call id); non-terminal - the turn blocks until `POST .../answer`, timeout, or cancel |
| `question_answered` | `{ "id","question_id","answers":["blue"] }` | resolves the question card (answered state) - one answer per asked question, in order; non-terminal. A timeout emits no extra event - the call's ordinary `tool_result` ("The user did not respond.") is the signal |
| `message_end` | `{ "token_count":312 }` | removes caret |
| `cancelled` | `{ "token_count":null }` | removes caret + "Stopped" marker; the row persists as `status="cancelled"` with the partial text |
| `error` | `{ "message":"..." }` | inline error; the assistant row persists as `status="error"` |

A `tool_call` is emitted **as soon as the model starts the call** - the name
arrives before its arguments finish streaming, so the running card shows the
model's intent live; the same `id` is re-emitted with the full `request` once
the arguments assemble, and the card updates in place.

Every event is persisted **before** it is published, so reloading a
conversation reproduces the same cards, citations, and artifacts - and a reload
*mid-turn* resubscribes from the streaming assistant row's `seq` and loses
nothing (thread messages carry `status` + `seq` for exactly this).

> **Why SSE + polling:** SSE survives plain-HTTP proxies (the dev proxy does not
> need any upgrade handshake) and needs no auth workaround - a thin subscriber
> over the log + bus splice. The client->server channel (cancel) is a plain
> `POST`, so no bidirectional socket is required. The SPA tails over SSE and
> falls back to range polling; both share the seq cursor for a gap-free splice.

### Stop generation

`POST /api/projects/{pid}/conversations/{id}/cancel` stops the in-flight turn
**server-side**: the
runner's token cancels, the upstream vLLM stream is torn down, the assistant
row finalises as `status="cancelled"` with whatever streamed, and a terminal
`cancelled` event lands on the normal delivery transports - the still-attached
client renders the exact final partial. Idempotent: `202` when a live turn was
signalled, `204` when there was nothing to stop (a cancel that races the queued
job leaves a tombstone that pre-cancels it at pickup). A cancelled turn does
not block the conversation - the next `POST .../messages` is accepted
immediately, and cancelled partials are **excluded** from the next turn's
upstream history (UI-only context).

### Answer a question

`POST /api/projects/{pid}/conversations/{id}/answer` with body
`{ "question_id": "...", "answers": ["...", ...] }` delivers the user's answers
to the in-flight turn's pending
[`ask_user`](chat-and-tools.md#ask-the-user-ask_user) question - one entry per
asked question, in the order they were asked (1..4). The shape mirrors the
cancel endpoint exactly: same route prefix, covered by the fallback
authenticated-user policy, and **ownership is structural** - the registry key is
built from the token's iss/sub, so a foreign conversation id can never address
another tenant's question. `question_id` is the server-minted id from the
`question_asked` event (never the model's tool-call id). Responses:

* **202** - the waiting tool received the answers; the `question_answered`
  event follows on the normal delivery transports.
* **404** - no question is pending for this conversation, or the id is stale
  (it just timed out / was already answered). The SPA marks its card expired.
* **400** - validation failure (the body validator: a missing/empty/oversized
  answer, or a count outside 1..4), an answer count that does not match the
  pending question count, or a closed question (`allow_free_text=false`)
  answered with something outside its options.

While a question pends the turn is in-flight, so the usual 409 rule blocks new
`POST .../messages` in that conversation - the question card (or Stop) is the
only input.

## Documents (knowledge panel)

Scoped to a project - a document belongs to exactly one project's `rag.db`.

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/projects/{pid}/documents` | List for the doclist: name, size, chunk_count, status, error. |
| `POST` | `/api/projects/{pid}/documents` | `multipart/form-data` upload. Stores the file, inserts `documents` row with `status='processing'`, enqueues ingestion, returns immediately. |
| `GET` | `/api/projects/{pid}/documents/{id}` | **Polled** by the client while processing -> drives `processing -> ready/failed` pills and "embedding 12 / 19 chunks...". Returns `status` and a progress field (e.g. `chunk_count` / chunks embedded). |
| `DELETE` | `/api/projects/{pid}/documents/{id}` | Deletes chunks + vec rows + fts rows + the original file. |
| `GET` | `/api/projects/{pid}/documents/events` | *(deferred - see [decisions.md section 6](decisions.md#6-live-ingestion-progress))* SSE stream of ingestion progress; additive future upgrade over polling. |

## Memory (per project)

Memory entries are stored as files under `projects/{pid}/memory/` and embedded into the project's `rag.db` as `kind='memory'` ([configuration section 2.3](configuration.md#23-memory)).

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/projects/{pid}/memory` | List entries (id, title, pinned, updated_at). |
| `POST` | `/api/projects/{pid}/memory` | `{ title, content, pinned? }` - add/edit an entry; it is (re)embedded for retrieval. |
| `DELETE` | `/api/projects/{pid}/memory/{id}` | Remove an entry and its chunks. |

## Artifacts

Artifacts are produced during chat by the **canvas tool suite** - the model calls
`make_artifact` / `edit_artifact` ([chat-and-tools](chat-and-tools.md#artifacts-the-canvas-tool-suite)) -
and stored in the project's `chat.db`. They are returned inline with the thread, plus:

```
GET    /api/projects/{pid}/conversations/{id}/artifacts   # list for the canvas tab strip
GET    /api/projects/{pid}/artifacts/{id}                 # raw content (download / "Source" view)
```

## Account & data

Self-service data lifecycle ([configuration section 5](configuration.md#5-data-lifecycle-user-facing)). Identity removal is Pocket ID's, not the API's.

| Method | Path | Notes |
|--------|------|-------|
| `POST` | `/api/projects/{pid}/forget-documents` | Wipe a project's `rag.db` (+ `files/`), keep its chats. |
| `GET` | `/api/projects/{pid}/export` | Download one project: conversations (JSON/Markdown) + original files. |
| `GET` | `/api/account/export` | Download everything - all projects. |
| `DELETE` | `/api/account` | **Erases all of this user's data** across its stores (databases + blobs). Does **not** remove the Pocket ID account ([operations -> user lifecycle](operations.md#user-lifecycle---remove-a-user)). |

## Admin (requires `Admin` policy)

| Method | Path | Notes |
|--------|------|-------|
| `GET` | `/api/admin/users` | Lists user folders, reading each one's `user.db` for the username (plus a footprint listing): username, key, size, doc count, last-active. The closest thing to a "user list" the API has. |
| `GET` | `/api/admin/users/{key}` | One user's folder summary. `{key}` validated as below. |
| `DELETE` | `/api/admin/users/{key}` | **Erases all of that user's data** across its stores (see [Operations -> User lifecycle](operations.md#user-lifecycle---remove-a-user)). |
| `GET` | `/api/admin/system-prompt` | What the model is sent, verbatim: the built-in system prompt plus every registered tool spec (`{ system_prompt, tools: [{ id, name, description, parameters_schema }] }`). Pure configuration - per-project pinned instructions are user data and excluded (admin grants no cross-user data read). Rendered on the admin page's "Model prompt" section. |

> **`{key}` is the most dangerous path parameter in the API** - it feeds a destructive whole-account delete. Unlike `pid`
> (whose IDOR-safety is covered in [configuration section 2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe)),
> `{key}` is **not** scoped under a token-derived folder, so it gets its own guard: it **must match
> `^[0-9a-f]{64}$`** (a sha256 hex) before any path-join, and the resolved absolute path is asserted
> to sit **under `/data/users/`** before the delete runs. A non-hex / `..` / absolute value is
> rejected outright - never path-joined ([security F6](security.md#3-findings--remediations), tested
> in [testing section 5/section 6](testing.md#validation---the-input-security-boundary)).

The API **cannot create users** - Pocket ID does. Admin here is purely data lifecycle and usage visibility.

## Not public

SearXNG, the gVisor sandbox, and vLLM are reached **only** server-side as part of the chat orchestration. They are never proxied directly to the browser, so secrets and execution stay inside the API boundary.
