# Decisions to confirm

Open choices still to lock down. Each will be resolved one by one; the **Decision** line is filled in as we settle them.

## 1. Embedding model + dimension

Bakes into the `vec0` table. Source: vLLM embeddings endpoint vs. a separate service. See [Operations → Cross-cutting (Embeddings)](operations.md#cross-cutting-concerns).

- **Decision:** **bge-m3, 1024-dim, served by vLLM** (`--task embed`, OpenAI-compatible `/v1/embeddings`). Chosen for multilingual retrieval (incl. Dutch) and 8k context; keeps the embedding service count at one. `vec_chunks.embedding` is `FLOAT[1024]`.

## 2. One DB vs two per project

Split (recommended) or merged `gert.db`. See [Per-user storage → Why two databases](storage-and-data.md#why-two-databases-per-project).

- **Decision:** **Two databases — `chat.db` + `rag.db`, one pair per project.** Lets "forget my documents" wipe a project's RAG without touching its chat history, and keeps vector-index rebuilds/`VACUUM` off the chat write path. The extra wiring is negligible at ~20 users. (The pair lives *inside* each project folder — see [decision §7](#7-project-model--isolation).)

## 3. Folder key

`sha256(iss + sub)` (recommended) vs. raw `sub` vs. email. See [Per-user storage → Resolving paths](storage-and-data.md#resolving-paths).

- **Decision:** **`sha256(iss + "\n" + sub)` lowercase hex**, with a fail-closed provisioning gate and a `meta.json` identity binding.
  - **Anchor on `sub`, not email or username.** `sub` is Pocket ID's stable, opaque UUID — *never renamed, never recycled*. Email is **mutable** (a rename orphans the folder) and **recycled** (a reassigned address would inherit the prior owner's data — the exact reuse attack we want to avoid); username is renamed. `sub` is also no less trusted: every claim in a signature-validated JWT is equally trusted, `sub` is just the most stable.
  - **Namespace by issuer.** `sub` is only unique *within* an issuer, so the key hashes `iss + "\n" + sub`. With one IdP today this is moot; it makes a second IdP collision-proof for free.
  - **Hashing** gives a fixed-length, path-safe, traversal-proof folder name for any value the IdP emits; the opaque name is covered by `meta.json` and `GET /api/admin/users` for key→user mapping.
  - **Collision is not the threat** — `sha256` makes cross-user collision infeasible regardless of input; the real risk is *identifier reuse*, addressed by anchoring on `sub` (above) plus the binding below.
  - **Validate before touching disk** ([principle #6](principles.md)): provisioning asserts `iss` == configured authority, `aud` matches, and `sub` is present + within a bounded charset/length **before** any path-derive or `mkdir`. No well-formed identity → no folder.
  - **Identity binding** ([security F12](security.md#3-findings--remediations)): `meta.json` records `(iss, sub)`; on every request to an existing folder the API asserts it matches the token, so a recreated/reassigned identity can never silently inherit a folder — it is refused.

## 4. Token lifetime / revocation

Strategy for immediate off-boarding. See [Operations → User lifecycle](operations.md#user-lifecycle--remove-a-user--remove-a-folder).

- **Decision:** **Accept Pocket ID's ~1-hour access token as the revocation window; no denylist.** Pocket ID does not support a short, independently-configurable access-token lifetime ([issue #792](https://github.com/pocket-id/pocket-id/issues/792), closed *not planned*), so the 10–15 min approach from the Authentik-era draft isn't available. Refresh tokens (with rotation) still drive the SPA's silent renewal. Off-boarding = deactivate in Pocket ID, effective within ~1h. **We deliberately do *not* add a `sub`-denylist:** it is shared, mutable, per-instance auth state that would break running **multiple GERT instances** (a denied `sub` on instance A wouldn't be known to instance B without a shared store), defeating horizontal scaling. GERT stays **stateless** — every instance validates a token purely from the JWT + Pocket ID's JWKS, nothing shared. If sub-hour revocation ever becomes a hard requirement, the right answer is a shorter token lifetime at the IdP (or a shared-store check introduced explicitly), not in-process state.

## 5. OCR

Should scanned-only PDFs (`old-scan.pdf`) be OCR'd (e.g. Tesseract) instead of failing, or is "no extractable text → Failed" the intended behavior?

- **Decision:** **Fail fast — "no extractable text → Failed".** No OCR dependency; the Failed pill is honest feedback and matches the mockup. OCR (Tesseract `eng`+`nld`) is a clean future enhancement — purely a pipeline change, no schema impact — if scanned docs become part of the corpus.

## 6. Live ingestion progress

SSE (`…/documents/events`) vs. simple polling. See [REST API → Documents](rest-api.md#documents-knowledge-panel).

- **Decision:** **Polling `GET /api/projects/{pid}/documents/{id}`.** Zero new plumbing, stateless, adequate at ~20 users with infrequent uploads. The SSE `…/documents/events` stream is deferred — a clean additive upgrade if the live counter proves worth the in-process broadcaster.

## 7. Project model & isolation

How a user's data is organised: one flat store, or scoped workspaces? See [Configuration → projects](configuration.md#2-projects).

- **Decision:** **Per-project isolation, "default" not "global".** Each project is its own folder with its own `chat.db` + `rag.db` + memory, fully isolated — no cross-project search, no shared/global corpus. The initial, always-present project is **`default`** (the landing project); creating a project just makes another isolated folder. This nests [principle #2](principles.md) (filesystem isolation) and [principle #5](principles.md) (deletion is `rm -rf`) one level down. *Rejected:* a "global + local" blended scope — it reintroduced cross-corpus query fusion and a `use_global` flag for little gain over simply switching projects.


