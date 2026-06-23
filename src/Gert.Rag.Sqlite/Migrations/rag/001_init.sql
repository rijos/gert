-- rag.db schema v1 (storage-and-data.md section rag.db).
--
-- The vec0 / fts5 virtual tables below require the native sqlite-vec extension,
-- which the provider loads on every rag.db connection (SqliteRagConnectionFactory.OpenAsync) before this
-- migration runs. The three indexes share an integer rowid (chunks.id ==
-- vec_chunks.chunk_id == fts_chunks rowid), so they join cheaply.
--
-- fts_chunks is an *external-content* FTS5 table (content='chunks'): it stores no
-- copy of the text, only the inverted index keyed by chunks.id. There are no
-- INSERT/DELETE triggers - the repository writes the fts rows explicitly (with
-- the matching rowid) on insert and issues the fts5 'delete' command on removal,
-- and deletes vec_chunks rows explicitly, since FK ON DELETE CASCADE does not
-- reach virtual tables.

CREATE TABLE documents (
    id          TEXT PRIMARY KEY,
    filename    TEXT NOT NULL,
    mime        TEXT NOT NULL,
    size_bytes  INTEGER NOT NULL,
    status      TEXT NOT NULL,                  -- processing | ready | failed
    chunk_count INTEGER NOT NULL DEFAULT 0,
    error       TEXT,                           -- "no extractable text"
    created_at  TEXT NOT NULL
);

CREATE TABLE chunks (
    id          INTEGER PRIMARY KEY,            -- rowid, links to vec + fts
    document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    ordinal     INTEGER NOT NULL,
    content     TEXT NOT NULL,
    page        TEXT,                           -- "p.4", "section 3"
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
