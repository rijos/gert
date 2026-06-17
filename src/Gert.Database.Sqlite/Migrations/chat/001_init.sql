-- chat.db schema v1 (storage-and-data.md section chat.db).
-- Applied by SqliteMigrationRunner when PRAGMA user_version < 1; the runner bumps
-- user_version to 1 afterwards (inside the same transaction).

CREATE TABLE conversations (
    id          TEXT PRIMARY KEY,           -- uuid
    title       TEXT NOT NULL,
    model_id    TEXT NOT NULL,              -- e.g. 'qwen3-27b-fp8-mtp'
    tools_json  TEXT NOT NULL DEFAULT '{}', -- {"rag":true,"search":true,"sandbox":false}
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    archived    INTEGER NOT NULL DEFAULT 0,
    -- The per-conversation monotonic counter every persisted event/message draws
    -- its `seq` from (UPDATE ... RETURNING; single-writer per conversation -- the
    -- planner finishes before the worker dequeues, and a second turn is rejected
    -- with 409 while one is streaming).
    next_seq    INTEGER NOT NULL DEFAULT 1
);

CREATE TABLE messages (
    id              TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    role            TEXT NOT NULL,          -- user | assistant | system | tool
    content         TEXT NOT NULL,
    model_id        TEXT,
    token_count     INTEGER,
    created_at      TEXT NOT NULL,
    seq             INTEGER NOT NULL DEFAULT 0,       -- ordering cursor
    status          TEXT NOT NULL DEFAULT 'complete', -- streaming | complete | error
    -- The model's thinking text, persisted on finalize so a reload restores the
    -- collapsed "Thinking" block. NULL for user rows and thinking-disabled turns.
    reasoning       TEXT NULL,
    -- Pure generation wall-clock (stream-consumption spans only; tool execution
    -- excluded) -- the tokens/sec readout.
    duration_ms     INTEGER NULL,
    -- Context window occupied by the turn's final model round (prompt_tokens +
    -- completion_tokens) -- the composer's usage ring.
    context_tokens  INTEGER NULL,
    -- JSON array of {mime_type, data} objects -- images pasted into the composer,
    -- stored inline as base64 (the composer downscales before sending, and every
    -- upstream vision call needs the full bytes anyway). NULL for rows without
    -- attachments.
    attachments_json TEXT NULL
);
CREATE INDEX ix_messages_conv ON messages(conversation_id, created_at);
CREATE INDEX ix_messages_conv_seq ON messages(conversation_id, seq);

-- The atomic turn gate (decisions section 11): at most ONE streaming assistant
-- row per conversation, enforced by the engine -- the placeholder insert IS the
-- gate. A losing concurrent plan gets SQLITE_CONSTRAINT_UNIQUE, mapped to 409.
CREATE UNIQUE INDEX ux_messages_streaming ON messages(conversation_id) WHERE status = 'streaming';

CREATE TABLE tool_calls (
    id            TEXT PRIMARY KEY,
    message_id    TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    kind          TEXT NOT NULL,            -- rag | web_search | sandbox
    status        TEXT NOT NULL,            -- running | done | error
    request_json  TEXT,                     -- query / code
    response_json TEXT,                     -- hits / results / stdout
    latency_ms    INTEGER,                  -- shown as "rag - 142ms"
    created_at    TEXT NOT NULL
);
CREATE INDEX ix_tool_calls_msg ON tool_calls(message_id, created_at);

CREATE TABLE citations (
    id           TEXT PRIMARY KEY,
    message_id   TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    ordinal      INTEGER NOT NULL,          -- the [1], [2] markers
    source_type  TEXT NOT NULL,             -- document | web
    doc_id       TEXT,                      -- -> rag.db documents.id (if document)
    label        TEXT NOT NULL,             -- "qdrant-benchmarks.pdf p.4" / URL title
    locator      TEXT,                      -- "p.4", URL
    score        REAL,
    -- Provenance: which tool call produced the citation (NULL for model-inline).
    -- Display ordinals are computed at read time.
    tool_call_id TEXT REFERENCES tool_calls(id) ON DELETE SET NULL
);
CREATE INDEX ix_citations_msg ON citations(message_id, ordinal);

CREATE TABLE artifacts (
    id              TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    message_id      TEXT REFERENCES messages(id) ON DELETE SET NULL,
    kind            TEXT NOT NULL,          -- md | html | svg | py  (the canvas tabs)
    name            TEXT NOT NULL,          -- decision.md, status.html, ...
    language        TEXT,                   -- for code artifacts
    content         TEXT NOT NULL,
    version         INTEGER NOT NULL DEFAULT 1,
    created_at      TEXT NOT NULL
);
CREATE INDEX ix_artifacts_conv ON artifacts(conversation_id, created_at);

-- The durable streaming replay log (chat-and-tools.md section detached turns).
-- The runner appends one row per published event; range/SSE catch-up reads
-- `seq > cursor`. Delta rows are coalesced (flushed on size/time thresholds and
-- tool/message boundaries), so replay is loss-free without a row per token.
CREATE TABLE turn_events (
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    seq             INTEGER NOT NULL,       -- per-conversation monotonic (the cursor)
    type            TEXT NOT NULL,          -- the ChatEvent wire name (delta, tool_call, ...)
    payload_json    TEXT NOT NULL,          -- the serialized ChatEvent (wire contract)
    created_at      TEXT NOT NULL,
    PRIMARY KEY (conversation_id, seq)
) WITHOUT ROWID;
