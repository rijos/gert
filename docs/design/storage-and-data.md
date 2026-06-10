# Per-user storage & data model

Data is owned by the filesystem at **two** levels of isolation: each **user** is a folder, and
within it each **project** is a folder with its own `chat.db` + `rag.db` + memory, fully isolated
from other projects ([configuration → projects](configuration.md#2-projects)). The same
"a connection only opens *this* folder's files" argument that isolates users
([principle #2](principles.md)) therefore isolates projects too. The conversation/RAG schemas
below are **per project** — there is one pair of databases per project, not per user.

## Per-user storage

### Layout

```
/data/
└── users/
    └── {key}/                     # key = sha256(iss + sub)  (see Resolving paths)
        ├── user.db                # user-level structured state — username (admin scan),
        │                          #   settings, and the PROJECT REGISTRY (see § user.db)
        └── projects/
            ├── default/           # lazily created; always present (the landing project)
            │   ├── chat.db        # conversations, messages, tool calls, citations, artifacts
            │   ├── rag.db         # sqlite-vec: documents, chunks, embeddings, FTS
            │   ├── files/         # original uploaded files ({doc-id}.pdf, {doc-id}.md …)
            │   └── memory/        # memory entries (markdown) → embedded into this project's rag.db
            └── {project-id}/      # any further project — same shape, fully isolated
```

> **No JSON sidecars.** Earlier drafts kept `meta.json` / `settings.json` / per-project
> `meta.json` files; all three moved into **`user.db`** — durable, transactional, and
> immune to the torn-write/healing dance a JSON file needs
> ([decisions §9](decisions.md#9-userdb--structured-user-state-is-a-database-not-json-sidecars)).
> A project's *config* is a row in `user.db`'s registry; a project's *data* is its folder.

### Resolving paths

```csharp
public sealed class SqliteDatabasePaths(IOptions<StorageOptions> opt)
{
    // Anchor on the stable (iss, sub) pair — never renamed, never recycled (decisions.md §3).
    // sub is only unique within an issuer, so namespace by iss; the hash keeps the folder name
    // filesystem-safe and traversal-proof for any value the IdP emits.
    private string Key(string iss, string sub) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{iss}\n{sub}"))).ToLowerInvariant();

    // user-level — callers pass the validated (iss, sub) from the token (see Lazy provisioning)
    public string Root(string iss, string sub)   => Path.Combine(opt.Value.DataRoot, "users", Key(iss, sub));
    public string UserDb(string iss, string sub) => Path.Combine(Root(iss, sub), "user.db");

    // admin-side — {key} is shape-validated to ^[0-9a-f]{64}$ first (security F6)
    public string RootByKey(string key)   => Path.Combine(opt.Value.DataRoot, "users", key);
    public string UserDbByKey(string key) => Path.Combine(RootByKey(key), "user.db");

    // project-level — pid is a UUID or the literal "default" (validated; see configuration.md §2.5)
    public string ProjectRoot(string iss, string sub, string pid) => Path.Combine(Root(iss, sub), "projects", pid);
    public string ChatDb(string iss, string sub, string pid)      => Path.Combine(ProjectRoot(iss, sub, pid), "chat.db");
    public string RagDb(string iss, string sub, string pid)       => Path.Combine(ProjectRoot(iss, sub, pid), "rag.db");
    public string FilesDir(string iss, string sub, string pid)    => Path.Combine(ProjectRoot(iss, sub, pid), "files");
    public string MemoryDir(string iss, string sub, string pid)   => Path.Combine(ProjectRoot(iss, sub, pid), "memory");
}
```

> `(iss, sub)` come **only** from the validated token, never the request, and the fail-closed
> provisioning gate (below) checks them *before* any of these methods run — so the path is only
> ever derived from an identity the gate already accepted. (Callers can thread the once-computed
> key instead of recomputing the hash; the per-`sub` signatures above just keep the derivation
> explicit.)

Hashing `iss + sub` gives a clean, fixed-length, path-safe folder name and avoids any traversal risk from exotic claim values; anchoring on `sub` (stable, never recycled) rather than email/username is what closes the **identifier-reuse** class of attack ([decisions §3](decisions.md#3-folder-key)). `user.db`'s `user_meta` row records the username purely descriptively, so admins can still find an opaque hash folder by username ([§ user.db](#userdb)). The **`pid`** comes from the request (unlike the user key), but it is validated to a UUID or the literal `default` and is only ever joined **under** `Root(iss, sub)` — so it can select among *this* user's projects but can never escape the user's folder, keeping cross-user IDOR structurally impossible ([configuration.md §2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe)).

### Why two databases per project?

| Concern | `chat.db` | `rag.db` |
|---------|-----------|----------|
| Access pattern | many small writes (messages) | bulk writes on ingest, KNN reads on query |
| Schema | plain relational | `vec0` virtual tables + FTS5 |
| Maintenance | rarely rebuilt | `VACUUM`/rebuild after large deletes |
| Reset semantics | keep chats | "forget my documents" wipes RAG only |

Splitting keeps a vector-index rebuild from locking chat history and lets a user clear a project's knowledge base without losing that project's conversations ([configuration → data lifecycle](configuration.md#5-data-lifecycle-user-facing)). *Alternative:* a single `gert.db` with both schemas is simpler to back up; choose this if you never need to reset RAG independently. (The mockup's "stored in your own file" line is satisfied either way.)

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

Provisioning is **fail-closed and validate-before-disk**
([principle #6](principles.md), [security F12](security.md#3-findings--remediations)), and past
the gate it is just **opening databases** — every DB self-migrates on open, so there is no
separate provisioning ceremony:

0. **Validate the identity *before touching disk*** — assert `iss` == the configured authority,
   `aud` == `gert-api`, and `sub` is present and within a bounded charset/length (e.g.
   `^[A-Za-z0-9._:\-]{1,128}$`). A missing/malformed identity is rejected here
   (`UnauthorizedDatabaseIdentityException`); **no folder is ever created for an unvalidated
   token.**
1. **Open `user.db`** (`IUserDatabaseProvider`) — first open creates the folder and the file,
   checks `PRAGMA user_version`, and applies any newer `Migrations/user/*.sql`. The provisioner
   then **refreshes `user_meta.username` from the token only when it changed** (the steady-state
   path stays read-only — no per-request write/WAL churn; `(iss, sub)` never changes) and seeds
   the **`default` project row** in the registry so the user always has a landing project.
2. **Open project DBs lazily** (`IChatDatabaseProvider` / `IRagDatabaseProvider`) — a project's
   `chat.db`/`rag.db` (and `files/`, `memory/`) materialise on first use, each applying its own
   `Migrations/{chat,rag}/*.sql` by `PRAGMA user_version`.

Past the gate the validated JWT is trusted: the folder key derives from the token and nothing
else, and nothing on disk is ever consulted as an identity check — `user_meta` is descriptive
(the admin key→user mapping), and each database's `user_version` is its own migration anchor.

Migrations are plain SQL files (`Migrations/user/001_init.sql`, `Migrations/chat/001_init.sql` …)
applied **per database**. No global migration step exists because there is no global database.

---

## Data model

Three databases: **`user.db`** at the user root (structured user state), and a
**`chat.db` + `rag.db`** pair inside each project folder (`projects/{pid}/`). There is no
`project_id` column in the project schemas — the *path* is the scope, so a conversation or
document simply cannot reference another project's rows. User settings and the project
registry are rows in `user.db`, not files ([configuration](configuration.md)).

> **The migrations are the authoritative DDL** —
> `src/Gert.Database.Sqlite/Migrations/{user,chat,rag}/*.sql`, applied per database by
> `PRAGMA user_version`. Shown here is the **effective** schema after all migrations
> (`user.db` v1; `chat.db` v4: 001 init · 002 turn pipeline · 003 reasoning & metrics ·
> 004 attachments; `rag.db` v1).

### `user.db`

One per user, at the user root. Holds the durable, transactional replacements for what
earlier drafts kept as JSON sidecars
([decisions §9](decisions.md#9-userdb--structured-user-state-is-a-database-not-json-sidecars)):
the username for the admin scan, the user's settings, and the **project registry**.

```sql
-- Single-row user metadata (id pinned to 1). Descriptive only — never an identity
-- check (the folder key derives from the token); username refreshes from the token
-- when it changes in the IdP.
CREATE TABLE user_meta (
    id          INTEGER PRIMARY KEY CHECK (id = 1),
    username    TEXT NOT NULL,
    created_at  TEXT NOT NULL
);

-- Single-row user settings (id pinned to 1); one JSON blob so the column set never
-- has to track the UserSettings shape field-by-field (configuration.md §3 — theme,
-- languages, default model/tools, memory mode, per-model generation defaults).
CREATE TABLE settings (
    id            INTEGER PRIMARY KEY CHECK (id = 1),
    settings_json TEXT NOT NULL
);

-- The project registry: one row per project (the landing 'default' plus any the
-- user creates) — name, instructions, defaults (configuration.md §2). Per-project
-- conversation/RAG data lives in that project's own chat.db/rag.db, not here.
CREATE TABLE projects (
    id            TEXT PRIMARY KEY,         -- uuid, or the literal 'default'
    name          TEXT NOT NULL,
    description   TEXT,
    instructions  TEXT,
    defaults_json TEXT,                     -- serialised ProjectDefaults (nullable)
    created_at    TEXT NOT NULL,
    updated_at    TEXT NOT NULL
);
CREATE INDEX ix_projects_created ON projects(created_at);
```

### `chat.db`

```sql
CREATE TABLE conversations (
    id          TEXT PRIMARY KEY,           -- uuid
    title       TEXT NOT NULL,
    model_id    TEXT NOT NULL,              -- e.g. 'qwen3-27b-fp8-mtp'
    tools_json  TEXT NOT NULL DEFAULT '{}', -- {"rag":true,"search":true,"sandbox":false}
    params_json TEXT NOT NULL DEFAULT '{}', -- per-chat generation overrides (temperature…) — configuration.md §4
    created_at  TEXT NOT NULL,
    updated_at  TEXT NOT NULL,
    archived    INTEGER NOT NULL DEFAULT 0,
    next_seq    INTEGER NOT NULL DEFAULT 1, -- per-conversation monotonic event counter (the seq cursor);
                                            --   single-writer per conversation (409 while a turn streams)
    thinking          INTEGER NULL,         -- tri-state reasoning toggle (NULL = model default, 0/1 = off/on)
    preserve_thinking INTEGER NULL          -- tri-state: replay prior turns' reasoning upstream (Qwen3.6 interleaved)
);

CREATE TABLE messages (
    id              TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    role            TEXT NOT NULL,          -- user | assistant | system | tool
    content         TEXT NOT NULL,
    model_id        TEXT,
    token_count     INTEGER,
    created_at      TEXT NOT NULL,
    seq             INTEGER NOT NULL DEFAULT 0,         -- ordering cursor (drawn from next_seq)
    status          TEXT NOT NULL DEFAULT 'complete',   -- streaming | complete | error | cancelled
    reasoning       TEXT NULL,              -- the model's thinking text — restores the collapsed block on reload
    duration_ms     INTEGER NULL,           -- pure generation wall-clock (tool execution excluded) — tokens/sec
    context_tokens  INTEGER NULL,           -- context occupied by the final round — the composer's usage ring
    attachments_json TEXT NULL              -- inline image attachments [{mime_type, data}] (vision input)
);
CREATE INDEX ix_messages_conv     ON messages(conversation_id, created_at);
CREATE INDEX ix_messages_conv_seq ON messages(conversation_id, seq);

CREATE TABLE tool_calls (
    id          TEXT PRIMARY KEY,
    message_id  TEXT NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
    kind        TEXT NOT NULL,              -- rag | web_search | sandbox | todo | clock
                                            --   | make_artifact | edit_artifact | read_artifact
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
    score       REAL,
    tool_call_id TEXT REFERENCES tool_calls(id) ON DELETE SET NULL  -- provenance (NULL = model-inline)
);

CREATE TABLE artifacts (
    id              TEXT PRIMARY KEY,
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    message_id      TEXT REFERENCES messages(id) ON DELETE SET NULL,
    kind            TEXT NOT NULL,          -- md | html | svg | py | cs | cpp | js | rs  (the canvas tabs)
    name            TEXT NOT NULL,          -- decision.md, status.html…
    language        TEXT,                   -- for code artifacts
    content         TEXT NOT NULL,
    version         INTEGER NOT NULL DEFAULT 1,
    created_at      TEXT NOT NULL
);

-- The durable streaming replay log (chat-and-tools.md § detached turns): the runner
-- appends one row per published event; range/SSE/WS catch-up reads `seq > cursor`.
-- Delta rows are coalesced (size/time thresholds + tool/message boundaries), so
-- replay is loss-free without a row per token.
CREATE TABLE turn_events (
    conversation_id TEXT NOT NULL REFERENCES conversations(id) ON DELETE CASCADE,
    seq             INTEGER NOT NULL,       -- per-conversation monotonic (the cursor)
    type            TEXT NOT NULL,          -- the ChatEvent wire name (delta, tool_call, …)
    payload_json    TEXT NOT NULL,          -- the serialized ChatEvent (wire contract)
    created_at      TEXT NOT NULL,
    PRIMARY KEY (conversation_id, seq)
) WITHOUT ROWID;
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
    kind        TEXT NOT NULL DEFAULT 'document', -- document | memory   (configuration.md §2.3)
    pinned      INTEGER NOT NULL DEFAULT 0,       -- memory entries: always injected, not just retrieved
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
