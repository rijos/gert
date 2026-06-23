# Improvements - 2026-06-23

Findings and recommendations from a full-codebase sweep of branch `quality/audit-sweep`
(code quality, doc/site sync, security, architecture). What was actionable and safe was
applied on this branch; the rest is recommended below, prioritized. Security specifics live
in [SECURITY-AUDIT.md](SECURITY-AUDIT.md).

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
- **Architecture guard (this commit).** Added a `PluginArchitectureTests` case asserting each
  assembly-split impl leaf never references `Gert.Service`, so the dead-ref class cannot return.

## Recommended next (prioritized)

### 1. Resolve the in-progress restructure (the README's own "scaffolding" concern)

The codebase sits on two overlapping refactors. The M.E.AI migration (`refactor-meai.md`) is
**fully landed** and captured in `decisions.md` #13, yet the file is still tracked at the repo
root labelled "Status: PLAN, not started." The "split the noun" refactor (`refactor.md`) is
**half-applied**: the `AgentEvent`/`AgentResult` vocabulary moved to `Gert.Model.Agent`, but the
structural targets (`IAgent`/`IAgentRun`/`Agent`/`ChannelSink`, the DB back-channel replacing the
in-memory cancel/question registries, the queue/worker collapse) were never done - leaving a
hybrid `Turn*`/`Agent*` naming layer in `Gert.Agent`.
**Recommend:** delete or move `refactor-meai.md` to `.dev/`; for `refactor.md`, either finish the
structural pass or re-scope the doc to what actually landed (and move it out of the tracked root).
These are your planning docs, so they were left untouched here - this is a decision for you.

### 2. Ship the two stubbed security-critical components

Both currently fail closed (safe today) but are load-bearing once enabled:
- **`gert-extract` helper** (`IsolatedTextExtractor`): F7's privilege-drop / rlimits / no-network /
  XXE / zip-bomb enforcement is only an argv vector handed to a helper binary that is not built;
  pdf/docx/xlsx extraction is entirely stubbed, and `HardenedXml` + `ZipBombGuard` are unit-tested
  but **unwired** (no in-process caller). Ship the helper as a first-class deliverable and verify
  the isolation end-to-end, or document that binary-document ingestion is not yet operational.
- **GVisor OCI bundle writer** (`GVisorSandbox`): the `runtime` config is built then discarded; a
  `Gert:Tools:Sandbox:Type=GVisor` host cannot execute Python (it degrades to a tool error).
  Implement the bundle writer or make `IsAvailable`/`Build` surface "not implemented" explicitly.

### 3. Validation and startup robustness

- **`model_id` allowlist.** `SendMessageRequestValidator` (and four siblings) carry duplicated
  TODOs to validate `model_id` against the provider catalog; it is never checked, so an unknown
  slug silently falls back. Needs `IChatProviderCatalog` reachable from `Gert.Validation` - a
  layering decision (validation depends only on `Gert.Model` + `Gert.Tools` today). Resolve the
  layering, add one shared rule, retire the five TODOs.
- **Fail-closed options validation.** Add `IValidateOptions<EmbeddingsParameters>` (and a
  per-provider `ChatProviderParameters` validator) asserting a parseable absolute `BaseUrl`,
  `Dimensions > 0`, and non-negative timeout/retry, wired with `ValidateOnStart`, so a typo'd
  upstream fails at boot with a named knob instead of on the first turn/upload.

### 4. Test-coverage tighteners

Controls/behaviours below are correct but under-pinned (a regression would pass CI):
CSP `script-src` negative assertion that `unsafe-inline` is absent; a positive HSTS-header test;
a unit test for the extractor's bounded `ReadCappedAsync`; an `appsettings.json` no-secret
tripwire; `EnsureInlineAttachmentsFit` (the inline-attachment budget gate); `LocalObjectStore`
traversal-rejection (rooted/`..`/scope-root keys); `UserProvisioner` and `DeletionRecoveryService`;
the `web_fetch` success-path transforms; and the stream parser's malformed-args fallback.

### 5. Organization and CI

- **Namespace vs folder in `Gert.Tools.Builtin`.** Impl types live in `Gert.Tools.*` namespaces
  that read like the contracts assembly; a rename to `Gert.Tools.Builtin.*` (mirroring the
  assembly + folder) would make impl namespaces clearly distinct. Mechanical, but verify
  `PluginArchitectureTests` (which keys Search/Sandbox off `Gert.Tools.Search`/`Gert.Tools.Sandbox`
  namespaces) stays green.
- **`site/` is unguarded by CI.** `make check-links` covers tracked markdown but not the public
  site HTML. Extend it (or add a small HTML link/anchor check) so the site cannot silently drift
  again - the kind of drift this branch just repaired.
- **RAG test home.** `Gert.Rag.Sqlite`'s tests live under `Gert.Database.Sqlite.Tests`; consider a
  dedicated `Gert.Rag.Sqlite.Tests` project (or rename the combined one) for clarity.

### 6. Developer environment (not a code issue)

`make test`'s 4 `RateLimitingTests` failures on this machine are environmental: those tests force
the Development environment (to enable the limiter), which loads dotnet user-secrets, and a local
`Gert:Chat:Providers:nebius-cosmos3` entry is missing a `Context` value - so the fail-closed
provider catalog rejects it at host startup. CI (no user-secrets) is unaffected. Fix locally by
adding `Context` to that secret (or removing the incomplete provider). Optionally harden the
tests: `AddGertFakes` overrides `IChatClientFactory` but not `IChatProviderCatalog`, so these
Development-env tests stay coupled to the host machine's chat config.
