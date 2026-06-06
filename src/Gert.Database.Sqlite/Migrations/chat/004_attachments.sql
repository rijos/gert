-- chat.db schema v4 — inline image attachments on messages.
-- Applied by SqliteMigrationRunner when PRAGMA user_version < 4.
--
-- * messages.attachments_json: JSON array of {mime_type, data} objects — images
--   pasted into the composer, stored inline as base64 (the composer downscales
--   before sending, and every upstream vision call needs the full bytes anyway,
--   so a separate blob store would just add a read). NULL for rows without
--   attachments (all assistant rows, and user rows from before v4).

ALTER TABLE messages ADD COLUMN attachments_json TEXT NULL;
