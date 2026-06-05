-- chat.db schema v2 — the detached turn pipeline (chat-and-tools.md § detached turns).
-- Applied by SqliteMigrationRunner when PRAGMA user_version < 2.
--
-- * conversations.next_seq: the per-conversation monotonic counter every persisted
--   event/message draws its `seq` from (UPDATE … RETURNING; single-writer per
--   conversation — the planner finishes before the worker dequeues, and a second
--   turn is rejected with 409 while one is streaming).
-- * messages.seq/status: ordering cursor + lifecycle (streaming|complete|error).
--   Pre-migration rows get seq=0 (ordering falls back to created_at) and
--   status='complete' (they were all written whole).
-- * citations.tool_call_id: provenance — which tool call produced the citation
--   (NULL for model-inline). Display ordinals are computed at read time.
-- * turn_events: the durable streaming replay log. The runner appends one row per
--   published event; range/SSE/WS catch-up reads `seq > cursor`. Delta rows are
--   coalesced (flushed on size/time thresholds and tool/message boundaries), so
--   replay is loss-free without a row per token.

ALTER TABLE conversations ADD COLUMN next_seq INTEGER NOT NULL DEFAULT 1;

ALTER TABLE messages ADD COLUMN seq INTEGER NOT NULL DEFAULT 0;
ALTER TABLE messages ADD COLUMN status TEXT NOT NULL DEFAULT 'complete'; -- streaming | complete | error
CREATE INDEX ix_messages_conv_seq ON messages(conversation_id, seq);

ALTER TABLE citations ADD COLUMN tool_call_id TEXT REFERENCES tool_calls(id) ON DELETE SET NULL;

CREATE TABLE turn_events (
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    seq             INTEGER NOT NULL,       -- per-conversation monotonic (the cursor)
    type            TEXT NOT NULL,          -- the ChatEvent wire name (delta, tool_call, …)
    payload_json    TEXT NOT NULL,          -- the serialized ChatEvent (wire contract)
    created_at      TEXT NOT NULL,
    PRIMARY KEY (conversation_id, seq)
) WITHOUT ROWID;
