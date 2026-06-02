-- rag.db schema v1 (storage-and-data.md § rag.db).
--
-- SCOPE NOTE (U4a): this file is authored for completeness but is NOT applied in
-- this unit. The vec0 / fts5 virtual tables below require the native sqlite-vec
-- extension, which is wired in U4b. The migration runner is never pointed at this
-- file yet, and rag.db is never opened. See SqliteRagRepository / OpenRagAsync.
-- TODO U4b: requires sqlite-vec native extension before this can be applied.

CREATE TABLE documents (
    id          TEXT PRIMARY KEY,
    filename    TEXT NOT NULL,
    mime        TEXT NOT NULL,
    size_bytes  INTEGER NOT NULL,
    status      TEXT NOT NULL,                  -- processing | ready | failed
    chunk_count INTEGER NOT NULL DEFAULT 0,
    error       TEXT,                           -- "no extractable text"
    kind        TEXT NOT NULL DEFAULT 'document', -- document | memory
    pinned      INTEGER NOT NULL DEFAULT 0,       -- memory entries: always injected
    created_at  TEXT NOT NULL
);

CREATE TABLE chunks (
    id          INTEGER PRIMARY KEY,            -- rowid, links to vec + fts
    document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    ordinal     INTEGER NOT NULL,
    content     TEXT NOT NULL,
    page        TEXT,                           -- "p.4", "§3"
    token_count INTEGER
);
CREATE INDEX ix_chunks_doc ON chunks(document_id);

-- vector index (sqlite-vec). Dimension MUST match the embedding model (bge-m3 = 1024).
CREATE VIRTUAL TABLE vec_chunks USING vec0(
    chunk_id INTEGER PRIMARY KEY,
    embedding FLOAT[1024]
);

-- lexical index for hybrid retrieval
CREATE VIRTUAL TABLE fts_chunks USING fts5(
    content,
    content='chunks',
    content_rowid='id'
);
