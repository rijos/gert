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
        ├── meta.json              # identity — { iss, sub, username, created_at, schema_version }
        ├── settings.json          # user preferences (theme, language, defaults) — see configuration.md §3
        └── projects/
            ├── default/           # lazily created; always present (the landing project)
            │   ├── meta.json      # project config — name, instructions, defaults (configuration.md §2)
            │   ├── chat.db        # conversations, messages, tool calls, citations, artifacts
            │   ├── rag.db         # sqlite-vec: documents, chunks, embeddings, FTS
            │   ├── files/         # original uploaded files ({doc-id}.pdf, {doc-id}.md …)
            │   └── memory/        # memory entries (markdown) → embedded into this project's rag.db
            └── {project-id}/      # any further project — same shape, fully isolated
```

### Resolving paths

```csharp
public sealed class UserPaths(IOptions<StorageOptions> opt)
{
    // Anchor on the stable (iss, sub) pair — never renamed, never recycled (decisions.md §3).
    // sub is only unique within an issuer, so namespace by iss; the hash keeps the folder name
    // filesystem-safe and traversal-proof for any value the IdP emits.
    private string Key(string iss, string sub) =>
        Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes($"{iss}\n{sub}"))).ToLowerInvariant();

    // user-level — callers pass the validated (iss, sub) from the token (see EnsureProvisioned)
    public string Root(string iss, string sub) => Path.Combine(opt.Value.DataRoot, "users", Key(iss, sub));
    public string MetaFile(string iss, string sub)     => Path.Combine(Root(iss, sub), "meta.json");
    public string SettingsFile(string iss, string sub) => Path.Combine(Root(iss, sub), "settings.json");

    // project-level — pid is a UUID or the literal "default" (validated; see configuration.md §2.5)
    public string ProjectRoot(string iss, string sub, string pid) => Path.Combine(Root(iss, sub), "projects", pid);
    public string ProjectMeta(string iss, string sub, string pid) => Path.Combine(ProjectRoot(iss, sub, pid), "meta.json");
    public string ChatDb(string iss, string sub, string pid)      => Path.Combine(ProjectRoot(iss, sub, pid), "chat.db");
    public string RagDb(string iss, string sub, string pid)       => Path.Combine(ProjectRoot(iss, sub, pid), "rag.db");
    public string FilesDir(string iss, string sub, string pid)    => Path.Combine(ProjectRoot(iss, sub, pid), "files");
    public string MemoryDir(string iss, string sub, string pid)   => Path.Combine(ProjectRoot(iss, sub, pid), "memory");
}
```

> `(iss, sub)` come **only** from the validated token, never the request, and `EnsureProvisioned`
> below checks them *before* any of these methods run — so the path is only ever derived from an
> identity the gate already accepted. (Callers can thread the once-computed key instead of
> recomputing the hash; the per-`sub` signatures above just keep the derivation explicit.)

Hashing `iss + sub` gives a clean, fixed-length, path-safe folder name and avoids any traversal risk from exotic claim values; anchoring on `sub` (stable, never recycled) rather than email/username is what closes the **identifier-reuse** class of attack ([decisions §3](decisions.md#3-folder-key)). `meta.json` records `(iss, sub)` and the username so admins can still find a folder by username **and** so the API can verify the binding on every request (below). The **`pid`** comes from the request (unlike the user key), but it is validated to a UUID or the literal `default` and is only ever joined **under** `Root(iss, sub)` — so it can select among *this* user's projects but can never escape the user's folder, keeping cross-user IDOR structurally impossible ([configuration.md §2.5](configuration.md#25-path-resolution--why-a-request-supplied-project-id-is-still-idor-safe)).

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

On the first authenticated request, an `EnsureProvisioned(iss, sub)` step provisions the **user**.
It is **fail-closed and runs before any path is derived or any directory is created**
([principle #6](principles.md), [security F12](security.md#3-findings--remediations)):

0. **Validate the identity *before touching disk*** — assert `iss` == the configured authority,
   `aud` == `gert-api`, and `sub` is present and within a bounded charset/length (e.g.
   `^[A-Za-z0-9._:\-]{1,128}$`). A missing/malformed identity is rejected here; **no folder is ever
   created for an unvalidated token.**
1. **If the folder already exists, verify the identity binding** — read `meta.json` and assert its
   `(iss, sub)` equals the token's. A mismatch means a recreated/reassigned identity resolved onto
   an existing folder → **refuse the request** (never serve another identity's data). On a fresh
   folder, write `(iss, sub)` into `meta.json` as the binding.

Then it creates `Root/`, `settings.json`, and the `default` **project** so the user always has a
landing project. Provisioning a project (`EnsureProject(iss, sub, pid)`, also lazy) does:

1. Creates `ProjectRoot/`, `files/`, `memory/` if missing, and writes `meta.json` (project config).
2. Opens each project DB (`chat.db`, `rag.db`) and checks `PRAGMA user_version`.
3. Applies any migration scripts whose version is newer, then bumps `user_version`.
4. Writes/updates the user's `meta.json` (username may have changed in the IdP; `(iss, sub)` never does).

Migrations are plain SQL files (`Migrations/chat/001_init.sql`, `Migrations/rag/001_init.sql`) applied **per project DB**. No global migration step exists because there is no global database.

---

## Data model

Both schemas below live **inside a project folder** (`projects/{pid}/`). There is no
`project_id` column anywhere — the *path* is the scope, so a conversation or document simply
cannot reference another project's rows. User- and project-level *config* lives in JSON files
(`settings.json`, `projects/{pid}/meta.json`), not these databases ([configuration](configuration.md)).

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
