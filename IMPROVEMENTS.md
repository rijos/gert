# Improvements - 2026-06-23 (follow-ups landed 2026-06-24)

Findings from a full-codebase sweep of branch `quality/audit-sweep` (code quality, doc/site
sync, security, architecture). Phases 1-4 applied the immediately-actionable fixes; a second
pass then resolved the prioritized follow-ups (items 1, 3, 4, 5), leaving only item 2 deferred
by decision. Everything done is listed below. Security specifics cite the findings F1-F12 in
[docs/design/security.md](docs/design/security.md).

## Done on this branch

- **Code quality (commit "Phase 1").** ~34 mechanical cleanups (stale/WHAT-restating comments,
  doc-citation corrections, ASCII punctuation, dead SPA exports + `slider.ts`, the dead
  `Toolset.WindDown`); plus correctness/defence-in-depth fixes with tests: the `MoveAsync`
  orphan-rule bug, the attachment-MIME allowlist single-source (`AttachmentKinds`), the
  `TurnPlanner` attachment-fit ordering, the bounded extractor read, `EnableExtensions(false)`
  after vec0, the `web_fetch` surrogate clip, and the removal of the dead
  `Gert.Database.Sqlite -> Gert.Service` reference. A pre-existing SPA typecheck failure
  (`composer.ts`) was also fixed.
- **Docs + site (commit "Phase 2").** Synced the design docs and the public site with the
  shipped code: 13-tool roster (was "twelve"/"five"), Monty-default sandbox (was gVisor-only),
  `TurnLauncher` (was `TurnWorker`/`ChannelTurnQueue`), the test-project layout, the current
  `Gert:Chat:Providers` config schema, the `user.db` storage layout, and removal of a
  documented-but-nonexistent memory REST API.
- **Security (commit "Phase 3").** Verified F1-F12 intact; fixed two defence-in-depth gaps
  (raw `ex.Message` reaching `document.Error`; the unbounded SearXNG response read).
- **Architecture guard (commit "Phase 4").** Added a `PluginArchitectureTests` case asserting each
  assembly-split impl leaf never references `Gert.Service`, so the dead-ref class cannot return.

The prioritized follow-ups below were then resolved in a second pass (items 1, 3, 4, 5);
item 2 was deliberately deferred.

- **The control-plane port (item 1).** The "split the noun" refactor's deferred phases 6-7 landed:
  the in-process `ITurnCancellation`/`ITurnQuestions` registries (plus the tombstones, the dual-CTS,
  and `TurnKey`) are gone, replaced by **`ITurnControlBus`** (`Gert.TurnControl`) - a pub/sub control
  plane the runner subscribes to per turn and the cancel/answer endpoints publish to, addressed by a
  token-derived `ControlScope` (user key + project + conversation). The default `Gert.TurnControl.Local`
  impl is in-process; a networked impl (Kafka/NATS) drops in behind the same port to split the agent
  host from the chat API across instances - the port is that seam. Cancellation is reactive (the signal
  trips the runner's linked token, no polling); a cancel that lands while the turn is queued is caught
  at subscribe time against the turn's plan instant (the freshness boundary). Answer validation moved
  into the bus, so the endpoints keep their HTTP contract (cancel is now fire-and-forget 202).
  An interim `turn_control` `chat.db` table was tried first and then replaced by this port (the db
  only reaches across instances on a shared filesystem and pays a poll latency). The refactor docs
  (`refactor.md`/`refactor-meai.md`) had already been removed; the design docs + the design memory
  are updated to match.
- **Validation and startup robustness (item 3).** model_id allow-list: a new `IModelIdCatalog` port
  in `Gert.Model.Chat` (implemented by the chat catalog, injected optionally into the five model_id
  validators), so an unknown slug now 400s at the boundary instead of silently falling back - the
  five duplicated TODOs are retired. Fail-closed options validation: `IValidateOptions` for the
  embeddings and per-provider chat `Parameters` (a parseable absolute http(s) `BaseUrl`,
  `Dimensions > 0`, non-negative timeout/retry) wired with `ValidateOnStart`, so a typo'd upstream
  fails at boot with the named knob instead of on the first turn/upload. Unit tests added.
- **Test-coverage tighteners (item 4).** The ten under-pinned controls now have tests: CSP
  `script-src` no-`unsafe-inline`, a positive HSTS header, the extractor's bounded `ReadCappedAsync`,
  an `appsettings.json` no-secret tripwire, `EnsureInlineAttachmentsFit`, `LocalObjectStore`
  scope-root/rooted-key rejection, `UserProvisioner`, `DeletionRecoveryService`, the `web_fetch`
  success-path transforms, and the tool-arg null/parse fallback.
- **Organization and CI (item 5).** (a) The impl namespaces `Gert.Tools.{Fetch,Search,Sandbox}` were
  renamed to `Gert.Tools.Builtin.*` (mirroring the assembly + folder; the `PluginArchitectureTests`
  namespace keys were updated to match). (b) `check_links` now also gates the public `site/` HTML
  (local links + `id`/`name` anchors). (c) The RAG-engine tests moved into a dedicated
  `Gert.Rag.Sqlite.Tests` project, with the shared `ProviderFixture` relocated into `Gert.Testing`.

## Deferred

### 2. Ship the two stubbed security-critical components

Deferred by decision to a dedicated effort (both are large - a separate hardened helper binary; a
full OCI runtime-spec writer needing a gVisor host). They fail closed today, so chat works without
binary-document ingestion and Monty is the only operational sandbox backend.

Both currently fail closed (safe today) but are load-bearing once enabled:
- **`gert-extract` helper** (`IsolatedTextExtractor`): F7's privilege-drop / rlimits / no-network /
  XXE / zip-bomb enforcement is only an argv vector handed to a helper binary that is not built;
  pdf/docx/xlsx extraction is entirely stubbed, and `HardenedXml` + `ZipBombGuard` are unit-tested
  but **unwired** (no in-process caller). Ship the helper as a first-class deliverable and verify
  the isolation end-to-end, or document that binary-document ingestion is not yet operational.
- **GVisor OCI bundle writer** (`GVisorSandbox`): the `runtime` config is built then discarded; a
  `Gert:Tools:Sandbox:Type=GVisor` host cannot execute Python (it degrades to a tool error).
  Implement the bundle writer or make `IsAvailable`/`Build` surface "not implemented" explicitly.

## Note: developer environment

`make test` is fully green on this machine now, but the `RateLimitingTests` remain coupled to local
chat config and can fail environmentally: they force the Development environment (to enable the
limiter), which loads dotnet user-secrets, so a local `Gert:Chat:Providers:*` entry missing a
`Context` value makes the fail-closed provider catalog reject it at host startup. CI (no
user-secrets) is unaffected. If it recurs, fix locally by adding `Context` to that secret (or
removing the incomplete provider). Optional hardening: `AddGertFakes` overrides `IChatClientFactory`
but not `IChatProviderCatalog`, so these Development-env tests stay coupled to the host machine's
chat config.
