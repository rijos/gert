-- user.db schema v1 (storage-and-data.md § user.db).
-- Applied by SqliteMigrationRunner when PRAGMA user_version < 1; the runner bumps
-- user_version to 1 afterwards (inside the same transaction). Holds the durable,
-- transactional replacements for the former JSON sidecars: username (admin scan),
-- user settings, and the project registry. One row per user — the file is the scope.

-- Single-row user metadata (id is pinned to 1).
CREATE TABLE user_meta (
    id          INTEGER PRIMARY KEY CHECK (id = 1),
    username    TEXT NOT NULL,
    created_at  TEXT NOT NULL
);

-- Single-row user settings (id is pinned to 1); the whole record is one JSON blob
-- so the column set never has to track UserSettings field-by-field.
CREATE TABLE settings (
    id            INTEGER PRIMARY KEY CHECK (id = 1),
    settings_json TEXT NOT NULL
);

-- The project registry: one row per project (the landing 'default' plus any the
-- user creates). Per-project conversation/RAG data lives in that project's own
-- chat.db/rag.db, not here.
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
