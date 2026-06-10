# Gert — design docs

The design set for **Gert**, a privacy-first, self-hosted LLM chat server: an ASP.NET Core
Web API serving a VanJS SPA, with per-user SQLite, hybrid RAG, sandboxed code execution, and
web search. These documents are the source of truth for **how the system is designed and
why**; the code in [`src/`](../../src/) implements them.

> **One-line architecture:** the **IdP owns identity**, the **filesystem owns data**, and the
> **API owns nothing persistent of its own**. Every user is one folder; deleting a user is
> deleting that folder.

## How this folder works

- Every doc here is **live** — kept true to the shipped system — except where marked:
  [turn-budgets.md](turn-budgets.md) and [context-compaction.md](context-compaction.md) are
  **open design notes**, and [strengthening-plan.md](strengthening-plan.md) is the
  **active build plan**.
- **Settled choices** live in [decisions.md](decisions.md) with their why; **open questions**
  live with their owning doc ([configuration §9](configuration.md#9-open-decisions),
  [turn-budgets §6](turn-budgets.md#6-open-questions)) until settled.
- The docs cross-link by section anchor — follow the links rather than reading front-to-back.
  The link graph is **CI-gated**: `make check-links` (`tools/check_links.py`) fails the build
  if a relative link or anchor stops resolving, so renames and moved headings can't strand
  readers silently.
- **If you change behaviour a doc covers, update the doc in the same change.** Code comments
  cite these docs by section; keep both ends accurate.

## Contents

### Foundations
- [principles.md](principles.md) — the six core principles: no central DB, isolation as a
  filesystem property, the token-derived user key, lazy provisioning, deletion is `rm -rf`,
  fail-closed validation.
- [components.md](components.md) — the moving parts and how they talk: SPA, Pocket ID,
  Gert.Api, vLLM, SearXNG, the gVisor sandbox.
- [tech-stack.md](tech-stack.md) — chosen libraries, the host-agnostic architecture
  (Api + Console over one service layer), the solution layout, engine/storage portability.
- [decisions.md](decisions.md) — the decision record: embedding model, two DBs per project,
  folder key, revocation, OCR, ingestion progress, project isolation, the `IObjectStore` seam,
  `user.db` over JSON sidecars, and JWT-only tool entitlement.

### Backend
- [auth.md](auth.md) — OIDC/PKCE with passkeys, expected JWT claims, ASP.NET wiring, the
  authorization matrix, and `gert_tools` tool entitlements (the claim is the ceiling).
- [storage-and-data.md](storage-and-data.md) — the per-user/per-project folder layout, path
  resolution, lazy provisioning + migrations, and the `chat.db` / `rag.db` schemas.
- [rest-api.md](rest-api.md) — every endpoint: settings, projects, conversations, the
  **detached turn** (202 + seq-cursor delivery over WS/SSE/polling), documents, memory,
  artifacts, account, admin.
- [chat-and-tools.md](chat-and-tools.md) — the tool loop and detached-turn pipeline, artifacts,
  hybrid RAG (vec0 + FTS5 + RRF), the ingestion pipeline, and per-tool detail (RAG, web
  search + SSRF guard, gVisor sandbox, todos, clock).
- [configuration.md](configuration.md) — the configuration cascade
  (server → user → project → conversation), the project model, user settings, model catalog,
  and the user-facing data lifecycle. (Operator knobs:
  [installation/configuration.md](../installation/configuration.md).)

### Frontend
- [ui-components.md](ui-components.md) — the SPA map: `wwwroot` layout, the four layers
  (pages → components → state/services → lib), cross-cutting concerns (theme, streaming,
  token handling), and the no-npm dev/release pipeline.
- [spa-style-guide.md](spa-style-guide.md) — how to write a component: the
  `component({name, css, view})` factory, tokens-only theming (Manila/Ember via
  `light-dark()`), no local `@media`, VanX lists, routing, formatting.

### Cross-cutting
- [security.md](security.md) — trust boundaries, the asset → threat → control table, findings
  F1–F12 (all implemented and tested), and the residual risks we knowingly accept. **Start
  here for any security-relevant change.**
- [operations.md](operations.md) — user lifecycle ("remove a user = remove a folder"),
  security headers/CSP, TLS, secrets, rate limits, probes, backups, and the shared NDJSON
  logging format.
- [testing.md](testing.md) — the pyramid: fakes at two fidelities (in-process .NET +
  Python mock upstreams sharing one spec), real-SQLite repository tests, validation as the
  input-security boundary, API integration, and the Python + Playwright E2E.

### Open designs & plans
- [turn-budgets.md](turn-budgets.md) — bounding long agentic turns: the layered guards that
  shipped, and the still-open token-budget and steering proposals.
- [context-compaction.md](context-compaction.md) — keeping long conversations inside the
  model's context window: bounds, elision, auto-compaction. **Open — under discussion.**
- [strengthening-plan.md](strengthening-plan.md) — the active build plan: heartbeat,
  steering, split embeddings endpoint, event-log pruning, multi-instance decision,
  compaction, import.

## Reading paths

- **New to the project:** [principles](principles.md) → [components](components.md) →
  [tech-stack](tech-stack.md) → [storage-and-data](storage-and-data.md) →
  [chat-and-tools](chat-and-tools.md), then by interest.
- **Security review / audit:** [security.md](security.md) (boundaries + findings) →
  [auth](auth.md) → [storage-and-data § provisioning](storage-and-data.md#lazy-provisioning--migrations)
  → [testing § validation](testing.md#validation--the-input-security-boundary).
- **Working on the UI:** [ui-components](ui-components.md) (the map) →
  [spa-style-guide](spa-style-guide.md) (the manual) → [security F2–F4](security.md#3-findings--remediations).

## Which doc for which change

| If your change touches… | Read first | Usually also |
|---|---|---|
| Login, JWT validation, roles, tool grants | [auth.md](auth.md) | [security F11/F12](security.md#3-findings--remediations), [decisions §3–4](decisions.md) |
| User/project folders, schemas, migrations, provisioning | [storage-and-data.md](storage-and-data.md) | [principles](principles.md), [configuration §2](configuration.md#2-projects) |
| Endpoints, DTOs, status codes | [rest-api.md](rest-api.md) | [testing §6](testing.md#6-api-integration-tests--gertapitests), [configuration §7](configuration.md#7-api-surface) |
| The tool loop, turns, streaming/replay, prompts | [chat-and-tools.md](chat-and-tools.md) | [rest-api § receiving a turn](rest-api.md#receiving-a-turn), [turn-budgets.md](turn-budgets.md) |
| RAG, retrieval, ingestion, embeddings | [chat-and-tools.md](chat-and-tools.md) | [storage-and-data § rag.db](storage-and-data.md#ragdb-sqlite-vec), [decisions §1/§5](decisions.md) |
| Projects, settings, memory, data lifecycle | [configuration.md](configuration.md) | [storage-and-data.md](storage-and-data.md), [rest-api.md](rest-api.md) |
| Anything in `wwwroot/` | [ui-components.md](ui-components.md) + [spa-style-guide.md](spa-style-guide.md) | [security F2–F4](security.md#3-findings--remediations) |
| New inputs, parsers, fetches, anything security-adjacent | [security.md](security.md) | [testing § validation](testing.md#validation--the-input-security-boundary), [principles #6](principles.md) |
| Tests, fakes, fixtures, E2E | [testing.md](testing.md) | [tech-stack § architecture](tech-stack.md#architecture) |
| Deployment, logging, headers, limits | [operations.md](operations.md) | [installation/configuration.md](../installation/configuration.md) |
