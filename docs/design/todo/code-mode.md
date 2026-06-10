# Code-mode (monty, Phase 2) — PARKED design todo

> **Status: PARKED (2026-06-10). Do NOT implement.** Phase 1 (monty as the `run_python`
> backend) is shipped. Phase 2 (code-mode) is blocked on the **security boundary**, which is
> not solid enough yet — see "Security boundary" below. This doc is the parked design + the
> open problem to resolve before any implementation.

## What code-mode is

Let the model write a Python snippet that calls its *other* enabled tools as functions —
`hits = search_documents(query="…"); …` — executed on monty (the Phase-1 sidecar). One
program instead of N sequential model tool-calls; the model composes tool results in code.

Verified against real `pydantic-monty 0.0.18`: `Monty(code).start()` auto-suspends on any
**undefined function call**, yielding `FunctionSnapshot{function_name, kwargs_json, call_id}`
— no upfront declaration. The host resumes with `{"return_value": …}` (value flows into the
code) or `{"exc_type": "PermissionError", …}` (the code sees a **catchable** error, cannot
escalate). `dump()`/`load_snapshot()` serialize a suspended state across HTTP.

## Security boundary — THE open problem (why this is parked)

**Per-call authorization is necessary but NOT sufficient.** Code-mode turns a single
prompt-injected tool call (injection can arrive via RAG'd documents or web-search results)
into a **Turing-complete program over all the session's tools**. "Each call is individually
authorized" does not make the *program* safe. Two composite threats the earlier plan did
not bound:

1. **Exfiltration via `web_search`.** The SSRF guard (F5) blocks *internal* targets — it does
   **not** stop data leaving to an arbitrary *external* site. So prompt-injected code can do
   `web_search(query=base64(private_doc_bytes))` and ship the user's private data to an
   attacker-controlled server that simply logs queries. Code-mode makes this trivial to
   automate over an entire project. **This is the sharpest weakness.**
2. **Amplified harvesting.** Even read-only, code can systematically sweep the whole project
   (loop over every doc) in one turn — far beyond manual tool-calling. Harmless *only* if
   there is no egress channel to pair it with.

### The solid invariant: **code-mode is egress-free**

The boundary becomes solid when it can be stated in one testable sentence:

> **No tool exposable in code-mode may send data outside the user's own project/conversation.**

Concretely:
- A per-tool `CodeModeExposable` property, **default-deny**. Only **egress-free,
  project/conversation-scoped** tools are exposable: `search_documents` (project-local read),
  `get_datetime` (local), the artifact/todo tools (writes confined to *this* conversation).
- **`web_search` is excluded** — it is the one egress/exfiltration tool. Excluding it is the
  literal end of the "make sure they're properly project scoped" requirement: web search is
  *inherently* not project-scoped.
- Enforced by a **meta-test** (like the validator meta-test): every `CodeModeExposable` tool
  must be on the egress-free list; the SSRF-fetching tool must never be exposable.
- Result: prompt-injected code-mode can read the user's own data and compute, but has **no
  channel to leak it out** — the results only go back to the model, which the user sees.

This invariant, plus the gates below, is what I'd want before implementing:
- **Gate each call on `job.ToolIds`** (the *offered* set = entitlement ∩ conversation-toggle ∩
  request ∩ model-cap) **∩ `CodeModeExposable`**, stricter than the model's own outer
  `AllowedToolIds` gate. Refusal = catchable `PermissionError`, never execution. No recursion
  (`run_code` not exposable).
- **New explicit `code` entitlement** (gert_tools) — separate operator opt-in; more powerful
  than plain `run_python`.
- **Project scoping** is automatic: inner `ToolInvocation` inherits `job.Pid` +
  `job.ConversationId`; no tool reads `pid` from its arguments (principles.md — the one request
  selector is only ever joined *under* the token-derived folder). A `pid=` in code args is ignored.
- **Resource bounds**: a new `MaxCodeModeCalls` cap per `run_code`; sidecar `max_calls`;
  per-inner-call `ToolCallTimeout`; monty duration/memory limits; outer `MaxToolRounds`.
- **Sidecar**: still Gert-drives (sidecar never calls Gert, no creds/DB); session tokens
  unguessable, single-use, TTL'd, turn-scoped; stays unprivileged, no `/data`, no net.

### OPEN DECISION (blocks implementation)
**Does `web_search` ever get into code-mode?**
- **Exclude it (recommended)** → the egress-free invariant holds; code-mode is project-local
  compute with no exfiltration path. This overrides the earlier "offer *all* tools" for
  `web_search` specifically.
- **Include it** → the invariant breaks; would need softer, weaker controls (per-turn egress
  budget, no raw-bytes-in-args heuristics, output review) that do not give a clean invariant.

Until this is decided, the boundary is not solid and Phase 2 should not be built.

## Design sketch (contingent on the boundary above)

"Gert drives, the sidecar pauses." All security stays in `TurnRunner`; the sidecar is a pure
run/pause engine. monty-only (offered only when `Gert:Sandbox:Backend=monty`).

- **Port + adapter**: `ICodeSession` (`StartAsync(code, maxCalls)` / `ResumeAsync(token,
  result)` → `CodeCompleted` | `CodeNeedsTool`); `MontyCodeSession` typed-HTTP client mirroring
  `MontySandbox`; registered only in the monty DI branch.
- **Sidecar `/start` + `/resume`** (real `tools/monty/app.py` + deterministic
  `tools/smoke/mocks/monty.py`): in-memory `{token: snapshot}` store, single-use TTL'd tokens,
  per-session call counter, stdout collected across resumes.
- **`CodeModeTool : ITool`** (Id `code`, Name `run_code`) — advertisement metadata only;
  `TurnRunner` intercepts by id. Add `"code"` to `BuiltInToolIds` + `AddScoped<ITool,…>`;
  add `CodeModeExposable` to `ITool`.
- **`TurnRunner`**: extract the per-call block (emit Running → execute → emit Result → persist
  row → collect citations/artifacts, ~lines 359–417) into a reused `HandleToolCallAsync`; add
  `ExecuteCodeModeAsync` driving `ICodeSession`, gating each `NeedsTool` on
  `job.ToolIds ∩ CodeModeExposable`. Inner calls surface as **sibling cards** (reuse the
  existing event model — no nesting work).
- **SPA**: `tools-menu.js` adds a `code` toggle **with a warning label**; `tool-card.js`
  renders the `code` kind (show the code + a warning marker).
- **Docs**: new finding **F13 (code-mode tool orchestration)** in `security.md` stating the
  egress-free invariant; the `code` entitlement in `auth.md`; `chat-and-tools.md` code-mode
  section. Run `/security-review` on the diff before merge.

## Verification (when unparked)
- `make build` · `make test` · `make lint` · `make check-links`.
- Unit: the `ToolIds ∩ CodeModeExposable` gate + no-recursion; **the egress-free meta-test**
  (web_search never exposable); sub-loop happy path (fake `ICodeSession`); refused-tool →
  `PermissionError` resume; `MaxCodeModeCalls` bound; project scoping (inner invocation carries
  `job.Pid`, ignores any `pid` in code args).
- E2E (`test_llm_tools.py`): `run_code` whose code calls `search_documents` → sibling cards +
  citations; toggled-off tool → refused; `code`-less role → `run_code` not advertised.
- Manual: `make serve-mock` (real monty `/start`+`/resume`).

## Deferred
gVisor code-mode (monty-only); grouped/nested card UI (sibling cards); stateless dumped-blob
session tokens (in-memory now); `web_search` in code-mode (pending the open decision above).
