-- chat.db schema v1 (storage-and-data.md § chat.db).
-- Applied by SqliteMigrationRunner when PRAGMA user_version < 1; the runner bumps
-- user_version to 1 afterwards (inside the same transaction).

CREATE TABLE conversations (
    id          TEXT PRIMARY KEY,           -- uuid
    title       TEXT NOT NULL,
    model_id    TEXT NOT NULL,              -- e.g. 'qwen3-27b-fp8-mtp'
    tools_json  TEXT NOT NULL DEFAULT '{}', -- {"rag":true,"search":true,"sandbox":false}
    params_json TEXT NOT NULL DEFAULT '{}', -- per-chat generation overrides
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    archived    INTEGER NOT NULL DEFAULT 0
);

CREATE TABLE messages (
    id              TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    role            TEXT NOT NULL,          -- user | assistant | system | tool
    content         TEXT NOT NULL,
    model_id        TEXT,
    token_count     INTEGER,
    created_at      TEXT NOT NULL
);
CREATE INDEX ix_messages_conv ON messages(conversation_id, created_at);

CREATE TABLE tool_calls (
    id            TEXT PRIMARY KEY,
    message_id    TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    kind          TEXT NOT NULL,            -- rag | web_search | sandbox
    status        TEXT NOT NULL,            -- running | done | error
    request_json  TEXT,                     -- query / code
    response_json TEXT,                     -- hits / results / stdout
    latency_ms    INTEGER,                  -- shown as "rag · 142ms"
    created_at    TEXT NOT NULL
);
CREATE INDEX ix_tool_calls_msg ON tool_calls(message_id, created_at);

CREATE TABLE citations (
    id          TEXT PRIMARY KEY,
    message_id  TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    ordinal     INTEGER NOT NULL,           -- the [1], [2] markers
    source_type TEXT NOT NULL,              -- document | web
    doc_id      TEXT,                       -- -> rag.db documents.id (if document)
    label       TEXT NOT NULL,              -- "qdrant-benchmarks.pdf · p.4" / URL title
    locator     TEXT,                       -- "p.4", "§3", URL
    score       REAL
);
CREATE INDEX ix_citations_msg ON citations(message_id, ordinal);

CREATE TABLE artifacts (
    id              TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    message_id      TEXT REFERENCES messages(id) ON DELETE SET NULL,
    kind            TEXT NOT NULL,          -- md | html | svg | py  (the canvas tabs)
    name            TEXT NOT NULL,          -- decision.md, status.html…
    language        TEXT,                   -- for code artifacts
    content         TEXT NOT NULL,
    version         INTEGER NOT NULL DEFAULT 1,
    created_at      TEXT NOT NULL
);
CREATE INDEX ix_artifacts_conv ON artifacts(conversation_id, created_at);
