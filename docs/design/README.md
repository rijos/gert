# Gert — Backend Design

REST API backend for `gert-frontend-mockup-v2.html`.

> **One-line architecture:** the **IdP owns identity**, the **filesystem owns data**, and the **API owns nothing persistent of its own**. Every user is one folder; deleting a user is deleting that folder.

---

## Contents

1. [Components](components.md) — the moving parts and how they talk.
2. [Core design principles](principles.md) — no central DB, isolation as a filesystem property.
3. [Authentication & authorization](auth.md) — OIDC/PKCE, JWT claims, ASP.NET wiring, user context.
4. [Per-user storage & data model](storage-and-data.md) — folder layout, path resolution, connection management, `chat.db` / `rag.db` schemas.
5. [REST API](rest-api.md) — endpoints for models, conversations, streaming messages, documents, artifacts, admin.
6. [Chat orchestration, RAG, ingestion & tools](chat-and-tools.md) — the tool loop, hybrid retrieval, the ingestion pipeline, and tool details.
7. [Configuration & projects](configuration.md) — what users configure (themes, language, models, data deletion) and the per-project isolation model with memory.
8. [Operations](operations.md) — user lifecycle ("remove a user = remove a folder") and cross-cutting concerns.
9. [Security](security.md) — trust boundaries, the asset→threat→control table, and the cross-cutting controls (CSP, SSRF, parser hardening) with residual-risk acceptances.
10. [Tech stack](tech-stack.md) — chosen libraries and suggested solution layout.
11. [UI components & `wwwroot` layout](ui-components.md) — VanJS SPA structure, component conventions, CSS split, the no-npm dev/release pipeline.
12. [Testing plan](testing.md) — the fake in-memory host, .NET whitebox tests, Console coverage, and the Python + headless-browser smoke launcher.
13. [Implementation plan](implementation-plan.md) — the agentic build order: dependency-ordered units, milestones (walking skeleton → hardened E2E), and the security-control → unit traceability.
14. [Decisions to confirm](decisions.md) — open choices we still need to lock down.
15. [Turn budgets](turn-budgets.md) — open design: bounding long agentic turns (token budgets, steering vs 409, what pi does).
