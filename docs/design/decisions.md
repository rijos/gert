# Decisions to confirm

Open choices still to lock down. Each will be resolved one by one; the **Decision** line is filled in as we settle them.

## 1. Embedding model + dimension

Bakes into the `vec0` table. Source: vLLM embeddings endpoint vs. a separate service. See [Operations → Cross-cutting (Embeddings)](operations.md#cross-cutting-concerns).

- **Decision:** **bge-m3, 1024-dim, served by vLLM** (`--task embed`, OpenAI-compatible `/v1/embeddings`). Chosen for multilingual retrieval (incl. Dutch) and 8k context; keeps the embedding service count at one. `vec_chunks.embedding` is `FLOAT[1024]`.

## 2. One DB vs two per user

Split (recommended) or merged `gert.db`. See [Per-user storage → Why two databases](storage-and-data.md#why-two-databases-per-user).

- **Decision:** **Two databases — `chat.db` + `rag.db`.** Lets "forget my documents" wipe RAG without touching chat history, and keeps vector-index rebuilds/`VACUUM` off the chat write path. The extra wiring is negligible at ~20 users.

## 3. Folder key

`sha256(sub)` (recommended) vs. raw `sub`. See [Per-user storage → Resolving paths](storage-and-data.md#resolving-paths).

- **Decision:** **`sha256(sub)` lowercase hex.** Fixed-length, path-safe, and traversal-proof for any `sub` the IdP emits. The only cost — opaque folder names — is covered by `meta.json` and `GET /api/admin/users` for key→user mapping.

## 4. Token lifetime / revocation

Strategy for immediate off-boarding. See [Operations → User lifecycle](operations.md#user-lifecycle--remove-a-user--remove-a-folder).

- **Decision:** **Accept Pocket ID's ~1-hour access token as the routine revocation window; add a `sub`-denylist for fast cut-off when needed.** Pocket ID does not support a short, independently-configurable access-token lifetime ([issue #792](https://github.com/pocket-id/pocket-id/issues/792), closed *not planned*), so the 10–15 min approach from the Authentik-era draft isn't available. Refresh tokens (with rotation) still drive the SPA's silent renewal. Routine off-boarding = deactivate in Pocket ID, effective within ~1h; immediate kill = a `sub`-denylist check in the JWT middleware (the one piece of shared state we accept, and only if sub-hour revocation is actually required).

## 5. OCR

Should scanned-only PDFs (`old-scan.pdf`) be OCR'd (e.g. Tesseract) instead of failing, or is "no extractable text → Failed" the intended behavior?

- **Decision:** **Fail fast — "no extractable text → Failed".** No OCR dependency; the Failed pill is honest feedback and matches the mockup. OCR (Tesseract `eng`+`nld`) is a clean future enhancement — purely a pipeline change, no schema impact — if scanned docs become part of the corpus.

## 6. Live ingestion progress

SSE (`/api/documents/events`) vs. simple polling. See [REST API → Documents](rest-api.md#documents-knowledge-panel).

- **Decision:** **Polling `GET /api/documents/{id}`.** Zero new plumbing, stateless, adequate at ~20 users with infrequent uploads. The SSE `/api/documents/events` stream is deferred — a clean additive upgrade if the live counter proves worth the in-process broadcaster.


