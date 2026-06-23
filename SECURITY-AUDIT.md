# Security audit - 2026-06-23

Point-in-time audit of branch `quality/audit-sweep`. Method: every documented finding
F1-F12 ([docs/design/security.md](docs/design/security.md)) was re-verified against its
implementing control **and** its test; new/regressed vulnerabilities were hunted across
eight lenses (IDOR/path, authz/entitlement, SQL/FTS5, sandbox, validation, SSE/replay,
secret/log leakage, DoS/parsers); every candidate was then adversarially reviewed by three
independent skeptics and kept only if fewer than two could refute it.

## Bottom line

The posture is **strong** and matches the design. Isolation is structural (the per-user
store is opened from `sha256(iss + "\n" + sub)`, never from a request value), validation is
fail-closed, SQL is parameterized, the SSRF guard and the markdown renderer are allow-list
controls. **No exploitable cross-tenant read/write, no RCE/sandbox escape, no injection, and
no auth bypass was found.** All of F1-F12 are intact with tests present. Two low/low-medium
defense-in-depth gaps were found and **fixed in this branch** (below). Six other candidates
were raised and **refuted** as non-reachable or already-mitigated.

## F1-F12 verification

| Finding | Control intact | Test present | Note |
|---|---|---|---|
| F1 CSP + security headers | yes | yes | `script-src 'self'`, no `unsafe-inline` on the shell; `connect-src` = self + Pocket ID only |
| F2 token in memory only | yes | yes | module-local var in `services/auth.ts`; no token in any Storage; refresh via httpOnly cookie |
| F3 sandboxed artifact iframes | yes | yes | `allow-scripts` without `allow-same-origin`; SVG fully sandboxed; own restrictive CSP |
| F4 in-house markdown/math | yes | yes | closed `createEl`/`MML_ELEMENTS` allow-list, no `innerHTML`, `sanitizeUrl`, `data:image` only |
| F5 SSRF guard | yes | yes | http/https + IPv4-only + full reserved blocklist + ports 80/443 + connect-time pin + per-redirect re-vet |
| F6 admin `{key}` validation | yes | yes | `^[0-9a-f]{64}$` shape guard (see doc-precision note) |
| F7 isolated ingestion parse | yes | yes | no in-process binary parser on uploads; absent-helper fails the doc, never the host (see gaps) |
| F8 secrets handling | yes | yes | no secret values in `appsettings.json`; wire logger redacts the api key |
| F9 TLS/HSTS | yes | code-only | `UseHsts()` in non-Development/Testing (TestServer can't exercise it) |
| F10 per-user rate limit | yes | yes | fixed-window, partitioned by token `(iss,sub)`; probe stays unthrottled |
| F11 JWT alg pinning | yes | yes | `ValidAlgorithms` pinned to RS256; dev-JWKS branch doubly gated |
| F12 folder-root derivation | yes | yes | key from token only; `sub` rendered traversal-proof by the sha256 derivation |

## New findings - fixed in this branch

1. **(low) Raw `ex.Message` reached the client via `document.Error`.**
   `IngestionService.IngestAsync`'s catch-all persisted the raw exception message into the
   client-visible `document.Error`, violating the style-guide rule (section 7: never serialize
   a raw `ex.Message`; log the detail, emit a generic message) - and it did not log. A
   `File.OpenRead` fault on a server-owned blob (delete race / permission error) could reflect
   an absolute server path (the install layout + the caller's own uid-hash). No cross-tenant
   break. **Fix:** the catch-all now `LogError`s the full exception and persists a generic
   `"Processing failed."`; expected failures keep their own controlled messages.

2. **(low-medium) Unbounded SearXNG search-API response read.**
   `SearXngWebSearch.SearchAsync` read the search JSON with `ReadAsStringAsync` and no byte cap,
   the lone external read without one (every sibling - `SafeHttpFetcher`, `MontySandbox`,
   `IsolatedTextExtractor` - caps). A large/slow/compromised SearXNG, or a MITM on a plain-http
   base URL, could stream a multi-hundred-MB body and OOM the host. **Fix:** the named `searxng`
   `HttpClient` now sets `MaxResponseContentBufferSize = MaxFetchBytes` (2 MiB), restoring the
   codebase-wide "cap every external body" invariant.

## Candidates raised and refuted (adversarial review)

- **GVisor OCI bundle is a stub** - selecting `GVisor` cannot run code, but it **fails closed**
  (degrades to a tool error), so it is not exploitable. Tracked as an impl gap, not a vuln.
- **Conversation route `{id}` reaching disk unguarded** - refuted; `StorageKeys.ValidateConversationId`
  / `RouteParams` guard it before any path join.
- **`FailClosedMetaTest` could miss a future raw service-param DTO** - refuted; no such DTO exists,
  and the meta-test walks the hub's request DTOs.
- **Cross-tenant leak in detached-turn SSE/polling** - refuted; the bus + reader open from
  `IUserContext`; the cursor path cannot cross users.
- **Artifact capability ticket** - verified working (HMAC-signed, constant-time compare, startup
  weak-secret guard).

## Known gaps / residual risk (not regressions)

- **F7 isolation is unverified until the `gert-extract` helper ships.** The privilege-drop /
  rlimits / no-network / XXE / zip-bomb defenses are arguments handed to an out-of-process helper
  binary that is not yet built; extraction is fully stubbed (every pdf/docx/xlsx fails cleanly).
  `HardenedXml` and `ZipBombGuard` are unit-tested but currently **unwired** (no in-process caller).
  When the helper lands, its real enforcement must be verified end-to-end.
- **F6 doc precision.** The admin by-key path relies on the `^[0-9a-f]{64}$` shape guard (which
  makes traversal structurally impossible) rather than an explicit post-join "under `/data/users`"
  assertion like the pid/project path uses; security.md F6 reads as if both are present on the
  admin path. Sound, but the doc is marginally stronger than the code literally does.
- **Test-coverage tighteners** (controls intact, tests could pin more): a negative assertion that
  `unsafe-inline` is absent from the shell `script-src`; a positive HSTS-header test; a unit test
  for the extractor's newly-bounded `ReadCappedAsync` truncation path; an `appsettings.json`
  no-secret tripwire test.

## Residual risks already accepted

Unchanged from [security.md section 4](docs/design/security.md#4-residual-risks-we-accept):
self-scoped prompt injection, the ~1h token revocation window, no load/pixel testing, and the
trusted-operator admin surface.
