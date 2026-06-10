# Security & code review — 2026-06-10 (`fabletest`)

A full-codebase security and code-quality review, followed by remediation, run as a
multi-agent pass (8 review agents → 8 adversarial verification agents → 9 implementation
agents, every change gated by `make build` / `make test` / `make lint` / `make check-links`
/ `make smoke-auth`). This file is the consolidated record: every finding, its verdict
after adversarial verification, and what happened to it.

Companion changes on this branch: [spa-style-guide.md](../design/spa-style-guide.md) was
rewritten against the real codebase, and [dotnet-style-guide.md](../design/dotnet-style-guide.md)
was created and then *implemented* (the "settlement" migrations are done, not aspirational).

## Verdict

The security architecture holds where it matters most. The token-derived user key rule has
**no** violations — no request-supplied identity reaches a data path anywhere, and `pid`
cannot escape the user folder (four independent shape/prefix guards). SQL is parameterized
throughout including FTS5 phrase-quoting and vec0 blob binding. The SPA genuinely has no
HTML-string sinks; CSP holds with no `unsafe-inline`. The SSRF guard is DNS-rebind-proof by
construction. Secrets discipline is clean.

The real weaknesses found: a validation boundary that was fail-closed **by registration
but not by invocation** (one service never called its registered validators), HTTP timeout
wiring that would have cut any generation round longer than 120 s mid-stream, two security
findings (F5 redirect re-vet, F10 rate limiting) whose documented test coverage did not
exist, and a cluster of SPA correctness bugs including a settings-save path that 400'd
whenever a theme was pinned.

## Findings and dispositions

Severities are post-verification. "Fixed" = on this branch with tests; commits noted.

### Critical / High — all fixed

| # | Finding | Verdict | Disposition |
|---|---|---|---|
| 1 | `CreateConversationRequest`/`UpdateConversationRequest` validators registered but never invoked — the only unvalidated mutating path | Confirmed | Fixed (`d4d68ab`): service invokes via `IValidationProvider`; tests pin invocation |
| 2 | vLLM `HttpClient.Timeout` 120 s kills streams mid-generation; stock resilience retried the non-idempotent chat POST at 10 s TTFB and hard-failed at 30 s; `RetryCount` dead | Confirmed | Fixed (`d0b9b13`): infinite chat client timeout (turn budget owns the stream), pipeline bound from options, embeddings split onto own client; wiring pinned by `HttpClientWiringTests` |
| 3 | Settings save always 400s with a pinned theme (SPA sent `manila`/`ember`; wire enum is `light\|dark\|auto`); server theme never applied at boot | Confirmed | Fixed (`853de6e`): palette→wire mapping, `ui.setTheme`/`applyServerTheme`, boot applies server truth |
| 4 | "Use my docs" switch a silent no-op (never transmitted) | Confirmed — design intends it to *be* the `rag` toggle | Fixed (`853de6e`): bound to `chat.tools.rag`; duplicate RAG row removed |
| 5 | `Menu` leaks a permanent `document` click listener per instance; `MessageStream` accumulates immortal `van.derive`s doing scroll work on every token delta | Confirmed (dropdown.js variant refuted) | Fixed (`853de6e`): listen-only-while-open; derives scoped to the component root |
| 6 | `http.sse()` never cancels its reader — every SSE-transported turn parks a client connection *and* a server streamer loop | Confirmed (worse than reported) | Fixed (`853de6e`): try/finally `reader.cancel()` |
| 7 | F10 rate limiting: zero tests in any tier (limiter disabled under the Testing env the factory pins) | Confirmed | Fixed (`f9a4b15`): config-bound limits, 429 + partition-isolation + `/healthz` tests via env override |
| 8 | F5 SSRF: the "redirect" test was tautological; the redirect re-vet and the ConnectCallback DNS pinning could both be deleted with every test green; the "U13 live tier" they deferred to does not exist | Confirmed | Fixed (`f9a4b15`): internal resolver/IP-check seams (no config bypass), real local redirect-chain + rebind tests |
| 9 | Turn worker is one global serial consumer — one long turn starves all users; the TOCTOU 409 check and seq single-writer invariant silently depend on it | Confirmed, accident of mirroring ingestion | Documented as decisions.md §11 + strengthening-plan **S8** (atomic per-conversation gate + bounded keyed parallelism, one combined change). Restructuring the run pipeline was wrong to do unattended; see below |
| 10 | Orphan/409 horizon anchored at plan time but the runner's wall clock at run start — queue wait makes healthy turns read as `error` and reopens the 409 gate | Confirmed | Fixed (`85bc979`): `TurnJob.PlannedAt` shared anchor; runner caps at the remaining budget |
| 11 | gVisor egress, when enabled, is full host networking rather than a filtered allow-list | Confirmed (behind off-by-default flag on a stubbed backend) | Deferred — flag is off by default and the gVisor backend is an unshipped stub; belongs with the sandbox build-out. Tracked here as a residual |

### Medium — fixed

- Streamed uploads with unknown `SizeBytes` were "recorded", never rejected; cap now
  enforced byte-level in `CountingStream` with partial-blob cleanup (`a0606ec`).
- Failed/partial ingestion left chunks retrievable by RAG (no status filter, no cleanup);
  hybrid search now joins only `status='ready'` and the failure path deletes inserted
  chunks (`a0606ec`).
- `MemoryService.UpsertAsync` non-atomic (embed failure left a Ready-but-unsearchable row
  + orphan blob); reordered embed-before-disk with compensation (`a0606ec`).
- `{pid}` route guard missing on conversations/messages/events/WS (malformed pid → 500,
  not branded 400) + `Guid.TryParse` vs `TryParseExact("D")` boundary/storage mismatch
  (`d4d68ab`).
- Artifact-ticket HMAC accepted arbitrarily weak operator secrets; now ≥ 32 UTF-8 bytes
  enforced at startup, knob documented (`d4d68ab`, `7980c75`, hardened to
  `IValidateOptions` in `bd7552b`).
- Kestrel's default ~28.6 MB body limit silently undercut the 50 MiB upload cap (bare 413
  instead of the branded 400) (`d4d68ab`).
- Unexpected turn faults were unlogged and `ex.Message` went verbatim into user-visible
  events (also `ChatSocketSession`); now logged, generic message to clients (`85bc979`,
  `7215658`).
- Unexpected DI/options drift: `Storage:DataRoot` unvalidated in `SqliteDatabasePaths`
  (empty → silent `./users` under CWD); Monty HTTP backstop vs sandbox wall-clock relation
  unenforced; Monty `/run` response read unbounded (`7215658`, `d0b9b13`).
- SPA: settings modals' `getElementById` plumbing, theme logic duplicated outside
  `state/ui.js`, admin page swallowing load failures, html-artifact fetching in a
  component (`853de6e`).
- Validation meta-test and architecture test escape hatches; fake-fidelity drift (C#
  case-insensitive vs Python case-sensitive fixture match) — partially addressed
  (meta-test still proves registration, but the one real invocation hole is closed and
  the guide/tests now state the invocation rule); the remainder is listed under residuals.

### Low / hygiene — fixed in the sweeps

Stale doc-comment citations (~15, `ChatService` ghosts, meta.json-era claims, `$type`,
dangling crefs); `ReadArtifactTool` malformed-range loop fault; `RagTool` silent
empty-vector search; misleading `text.too_long` for null text; admin list hiding
username-less folders; missing arg guards; Dapper bootstrap ×3; `DecodeFilename` ×3 and
`DefaultModelId` ×2 dedupes; account-export temp-file leak; `DeriveTitle` surrogate-pair
split; rate-limit partition on raw `sub` (now `iss`+`sub`); dev-JWKS silent activation
(now logs a warning); dead `Tools:DefaultGrant` key; SPA dead code, blob-URL revocation,
byte-formatter dedupe, protocol-relative links treated as external, shadow/scrim/brand
tokens (`7215658`, `bd7552b`, `853de6e`).

### Style-guide settlements — implemented (`bd7552b`)

`TimeProvider` injected everywhere (zero `DateTimeOffset.UtcNow` left in `src/`); one
options idiom (`AddOptions<T>().Bind(...).ValidateOnStart()`); `AddGertSqliteStorage`
replaces the host-duplicated registration block; all 9 controllers on granular service
interfaces (the `IGertServices` hub is Console-only, as the guide prescribes).

## Known residuals (accepted or deferred, in rough priority order)

1. **S8 (turn pipeline)** — the global serial worker + TOCTOU 409 combination is now
   *documented* (decisions.md §11) but not restructured: the fix requires a migration
   (partial unique index), planner write-back, and bounded keyed parallelism in one
   change, with race tests. Deliberately not done unattended overnight.
2. **gVisor backend** — `GVisorSandbox`/`IsolatedTextExtractor` remain fail-closed stubs;
   when egress is enabled it is full host networking (finding 11). The monty backend is
   the shipped path.
3. **WS/SSE connections outlive token expiry** — no mid-connection re-validation;
   long-lived sockets keep streaming after the JWT expires. Bounded by turn length.
4. **`window.GERT_DEV_TOKEN` branch ships in production code** (`services/auth.js`) — the
   smoke harness depends on it; inert unless something injects the global before boot.
5. **OIDC `state` not validated on callback** — PKCE carries the flow; there is no mock
   IdP to test against tonight (the harness injects tokens directly), so this was skipped
   rather than landed untested.
6. **`LocalObjectStore` containment is lexical** — a symlink planted *inside* a user tree
   would be followed. Requires the attacker to already write inside the tree.
7. **Pasted SVG attachments open via `blob:` `window.open`** — active SVG content runs in
   an opaque-origin context; worth revisiting with the artifact sandbox pattern.
8. **`EmbeddingDimensions` is a config knob the SQLite adapter hardcodes away**
   (`FLOAT[1024]` migration + const) — changing it is schema work, not a patch.
9. **`settings_json` ModelParams merge grows without bound** across requests (slowly).
10. **Test-tier honesty gaps that remain**: the smoke "SSRF" E2E never reaches the fetcher
    (`FetchPages=false` in every profile); C# fake fixture matching is case-insensitive
    while the Python mock is case-sensitive; migrations are tested from v1 snapshots only;
    `GertApiFactory` re-pins RS256 itself; `tools/smoke/run.py` boots mocks on fixed ports
    behind a blind `sleep(1.0)`; turn-budget/compaction service paths are still thin.
11. **`IngestionService` persists raw `ex.Message` into the document `Error` field** —
    operator-visible, not user-event-visible; arguably covered by the §7 rule.
12. **`vanX.list` remains unused** — full-rebuild list rendering is the documented default;
    fine at current sizes, the opt-in is documented in the spa guide §4.

## Method note

Review reports (8 × full detail) were written to `/tmp/gert-fable/*.md` during the run and
are not committed; this file is the durable summary. Every High finding above survived an
independent adversarial verification pass whose explicit job was refutation — two claims
were partially refuted there (a dropdown.js "leak" that subscribes only to per-instance
state, and the upload fail-open being unreachable through the shipped hosts) and their
dispositions reflect that.
