# Per-user storage & data model

## Per-user storage

### Layout

```
/data/
└── users/
    └── {key}/                 # key = sanitized sub  (see Resolving paths)
        ├── meta.json          # { sub, username, created_at, schema_version }
        ├── chat.db            # conversations, messages, tool calls, artifacts
        ├── rag.db             # sqlite-vec: documents, chunks, embeddings, FTS
        └── files/             # original uploaded files
            ├── {doc-id}.pdf
            └── {doc-id}.md
```

### Resolving paths

```csharp
public sealed class UserPaths(IOptions<StorageOptions> opt)
{
    private string Key(string sub) =>
        // sub is opaque; keep the folder name filesystem-safe and traversal-proof
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sub))).ToLowerInvariant();

    public string Root(string sub)    => Path.Combine(opt.Value.DataRoot, "users", Key(sub));
    public string ChatDb(string sub)  => Path.Combine(Root(sub), "chat.db");
    public string RagDb(string sub)   => Path.Combine(Root(sub), "rag.db");
    public string FilesDir(string sub)=> Path.Combine(Root(sub), "files");
    public string MetaFile(string sub)=> Path.Combine(Root(sub), "meta.json");
}
```

Hashing `sub` gives a clean, fixed-length, path-safe folder name and avoids any traversal risk from exotic `sub` values. `meta.json` records the original `sub` and username so admins can still find a folder by username.

### Why two databases per user?

| Concern | `chat.db` | `rag.db` |
|---------|-----------|----------|
| Access pattern | many small writes (messages) | bulk writes on ingest, KNN reads on query |
| Schema | plain relational | `vec0` virtual tables + FTS5 |
| Maintenance | rarely rebuilt | `VACUUM`/rebuild after large deletes |
| Reset semantics | keep chats | "forget my documents" wipes RAG only |

Splitting keeps a vector-index rebuild from locking chat history and lets a user clear their knowledge base without losing conversations. *Alternative:* a single `gert.db` with both schemas is simpler to back up; choose this if you never need to reset RAG independently. (The mockup's "stored in your own file" line is satisfied either way.)

### Connection management & concurrency

- Use **`Microsoft.Data.Sqlite`**, open WAL mode and a busy timeout on every connection:

  ```sql
  PRAGMA journal_mode = WAL;
  PRAGMA busy_timeout = 5000;
  PRAGMA foreign_keys = ON;
  ```

- WAL allows the ingestion worker to write `rag.db` while the user's query reads it. SQLite still permits **one writer at a time** — fine for ~20 users with low contention.
- Open-per-request (open → use → dispose). Microsoft.Data.Sqlite pools connection handles internally, so this is cheap. Do **not** share a single connection across requests.

### Lazy provisioning + migrations

On the first authenticated request in a pipeline, an `EnsureProvisioned(sub)` step:

1. Creates `Root/`, `files/` if missing.
2. Opens each DB and checks `PRAGMA user_version`.
3. Applies any migration scripts whose version is newer, then bumps `user_version`.
4. Writes/updates `meta.json` (username may have changed in the IdP).

Migrations are plain SQL files (`Migrations/chat/001_init.sql`, `Migrations/rag/001_init.sql`) applied per user. No global migration step exists because there is no global database.

---

## Data model

### `chat.db`

```sql
CREATE TABLE conversations (
    id          TEXT PRIMARY KEY,           -- uuid
    title       TEXT NOT NULL,
    model_id    TEXT NOT NULL,              -- e.g. 'qwen3-27b-fp8-mtp'
    tools_json  TEXT NOT NULL DEFAULT '{}', -- {"rag":true,"search":true,"sandbox":false}
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
    id          TEXT PRIMARY KEY,
    message_id  TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    kind        TEXT NOT NULL,              -- rag | web_search | sandbox
    status      TEXT NOT NULL,              -- running | done | error
    request_json  TEXT,                     -- query / code
    response_json TEXT,                     -- hits / results / stdout
    latency_ms  INTEGER,                    -- shown as "rag · 142ms"
    created_at  TEXT NOT NULL
);

CREATE TABLE citations (
    id          TEXT PRIMARY KEY,
    message_id  TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    ordinal     INTEGER NOT NULL,           -- the [1], [2] markers
    source_type TEXT NOT NULL,              -- document | web
    doc_id      TEXT,                       -- → rag.db documents.id (if document)
    label       TEXT NOT NULL,              -- "qdrant-benchmarks.pdf · p.4" / URL title
    locator     TEXT,                       -- "p.4", "§3", URL
    score       REAL
);

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
```

### `rag.db` (sqlite-vec)

```sql
CREATE TABLE documents (
    id          TEXT PRIMARY KEY,
    filename    TEXT NOT NULL,
    mime        TEXT NOT NULL,
    size_bytes  INTEGER NOT NULL,
    status      TEXT NOT NULL,              -- processing | ready | failed  (the pills)
    chunk_count INTEGER NOT NULL DEFAULT 0,
    error       TEXT,                       -- "no extractable text" (old-scan.pdf)
    created_at  TEXT NOT NULL
);

CREATE TABLE chunks (
    id          INTEGER PRIMARY KEY,        -- rowid, links to vec + fts
    document_id TEXT NOT NULL REFERENCES documents(id) ON DELETE CASCADE,
    ordinal     INTEGER NOT NULL,
    content     TEXT NOT NULL,
    page        TEXT,                       -- "p.4", "§3"
    token_count INTEGER
);
CREATE INDEX ix_chunks_doc ON chunks(document_id);

-- vector index (sqlite-vec). Dimension MUST match the embedding model.
CREATE VIRTUAL TABLE vec_chunks USING vec0(
    chunk_id INTEGER PRIMARY KEY,
    embedding FLOAT[1024]                    -- bge-m3 = 1024 (see decisions.md §1)
);

-- lexical index for hybrid retrieval
CREATE VIRTUAL TABLE fts_chunks USING fts5(
    content,
    content='chunks',
    content_rowid='id'
);
```

`chunks.id`, `vec_chunks.chunk_id`, and the FTS rowid are the same integer, so the three tables join cheaply.
