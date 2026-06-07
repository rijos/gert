# Turn budgets — bounding long agentic turns

**Status: open design — nothing in this document is implemented beyond the runaway brake.**
The question: what should bound a long agentic turn, given that round caps are the wrong
tool? Written after the 2026-06-07 runaway-loop incident and a survey of how
[pi](https://pi.dev) handles the same problem.

---

## 1. The incident, and why round caps are the wrong budget

A turn against real vLLM (qwen3.6) hit the then-hardcoded `MaxToolRounds = 5` mid-work.
The old cap branch dropped the round's tool calls and re-asked the model with an unchanged
prompt; the model — looking at a history full of tool calls — emitted tool calls again,
forever: one upstream POST every ~2–4 s until `MaxTurnDuration`, with the conversation
409-blocked the whole time.

Two fixes already landed (correctness, not budget policy):

- **The wind-down brake.** Past the cap, calls are *refused* with synthetic
  budget-exhausted results (wire-format valid, narration preserved), the model gets one
  tool-free round to answer, and a second capped round hard-breaks. Upstream calls are
  bounded at `MaxToolRounds + 2`.
- **`Gert:Turn:MaxToolRounds`** (default 16) replaced the const, and the runner logs
  rounds + the brake.

But the deeper conflict stands: **the system prompts for behaviour the cap punishes.**
The `set_todos` reminder says "N steps remain — continue", and every todo status update
burns a round. A round is also a poor proxy for cost — a `get_datetime` round and a
4 000-token retrieval round count the same. Round caps are runaway *protection*; they are
not a *budget*.

## 2. What pi does (surveyed 2026-06-07, `earendil-works/pi@main`)

pi's agent loop (`packages/agent/src/agent-loop.ts`) is `while (true)` — **no round cap,
no token cap, no spend cap**. A run ends only when:

1. the model stops emitting tool calls (natural completion);
2. `stopReason` is `error`/`aborted` (provider failure or the user pressing ESC);
3. a tool returns a `terminate` flag;
4. the optional `shouldStopAfterTurn` hook says stop — an extension seam, with no
   built-in budget implemented anywhere.

The only "budgets" in the codebase are token allocations unrelated to the loop:
compaction's context-window budget and per-level `thinkingBudgets`.

pi gets away with this because it is an **interactive terminal harness: the human is the
budget.** A person watches every round, aborts instantly, and — notably — can *steer*: new
messages typed mid-run are injected into the loop between rounds
(`getSteeringMessages`, checked after each turn's tools finish).

The lesson is not "no budgets". It is that pi's budget mechanism is **a human with
working controls**. Gert's incident was painful precisely because the human had none: the
turn ran detached server-side and the user's attempt to intervene got a 409.

## 3. Gert's constraints (why we can't just copy pi)

| Constraint | Consequence |
|---|---|
| **Detached turns** ([chat-and-tools](chat-and-tools.md#detached-turns)) — generation survives disconnects by design | No human is guaranteed to be attached; a server-side bound must exist. `MaxTurnDuration` is that bound today. |
| **Shared, multi-user GPU** | One user's runaway turn starves others — budgets are also a fairness control ([security F10](security.md#3-findings--remediations)). |
| **Turns are serialized per conversation** (the seq single-writer invariant; the 409) | Any steering design must keep ONE writer: the *running turn* must consume injected messages; a second concurrent turn is not an option. |
| **Prefix-cache friendliness** ([configuration](../installation/configuration.md#3-gertvllm--the-model-upstream)) | Budget/steering injections must append at the prompt *tail* (the `TodoReminder` precedent) — never mutate the system prompt or history mid-turn. |
| **Persist-then-publish event log** | Steering messages need seq allocation and durable rows like any other event; the UI replays them on reload. |

## 4. The candidate mechanisms

### 4a. Wall-clock (`MaxTurnDuration`) — exists, keep

The honest outer bound for a detached system: it caps GPU occupancy regardless of what
the model does, and doubles as the orphan horizon. Nothing replaces it.

### 4b. Per-turn token budget (`Gert:Turn:MaxTurnTokens`) — proposed

Tokens are the actual cost unit, and the usage tail of every round already reports them.
Enforcement is cheap and degrades gracefully through the machinery that now exists:

- At each **round boundary**, accumulate completion tokens (and optionally weigh prompt
  tokens — see open questions) from the round's usage chunk.
- When the budget is exceeded and the model wants another round: flip into the existing
  **wind-down** (refuse calls with a "token budget exhausted" result, one tool-free round,
  hard break). The turn ends with a coherent final answer, not an error.
- Emit the budget trip as a warning log + (optionally) a visible event so the UI can show
  "answer truncated by budget".

This is ~30 lines in `TurnRunner` because the wind-down brake already exists; the budget
just becomes a second trigger for it. `MaxToolRounds` then *demotes* to pure runaway
protection (default high, e.g. 64 — only degenerate loops hit it).

### 4c. Steering instead of 409 — proposed, larger

pi's most transferable idea. Today a `POST …/messages` during a live turn 409s; instead:

- Persist the user message immediately (seq-allocated, durable, replayable), mark it
  `queued`, respond 202.
- The runner checks a per-conversation steering queue **at each round boundary** and
  appends queued messages to the prompt tail (prefix-cache safe, same placement rule as
  `TodoReminder`), then continues the loop.
- The single-writer invariant holds: the running turn consumes the message; no second
  turn starts. If no turn is live, the normal plan-and-enqueue path runs unchanged.

This converts "runaway you can't talk to" into "long task you can redirect" — and the
existing `cancel` stays the hard stop. Touches planner, runner, repository (queue read),
and the SPA composer (un-disable while streaming); the event-log/replay design already
accommodates it.

### 4d. Stop-policy seam (`shouldStopAfterTurn`-like) — defer

A hook deciding "another round?" per turn would let budgets be pluggable policy. With
exactly one host and one policy today, this is structure without a second consumer;
revisit if per-user/per-model budgets (4e) materialise.

### 4e. Per-user budgets (rate limiting) — separate concern

Cross-*turn* fairness (one user saturating the box over many turns) is
[security F10](security.md#3-findings--remediations)'s rate-limiter territory — orthogonal
to bounding one turn, and should not be conflated with this design.

## 5. Recommendation

Phased, smallest-honest-step first:

1. **Phase 1 — token budget.** Add `Gert:Turn:MaxTurnTokens` (4b) as a second trigger for
   the existing wind-down; demote `MaxToolRounds` to a high runaway brake (e.g. 64).
   Wall-clock unchanged. Small, testable, no protocol changes.
2. **Phase 2 — steering.** Replace the 409 with queue-and-inject (4c). This is the change
   that actually makes *long* agentic tasks acceptable to sit through, because the user
   regains the role pi gives them: the budget of last resort.
3. Keep 4d/4e parked until there is a second policy or a fairness incident.

## 6. Open questions

- **What counts against the token budget?** Completion tokens only (model's own output),
  or context footprint (prompt tokens re-billed every round — closer to true GPU cost,
  but penalises long histories the user already paid for)? Leaning: completion tokens
  for the user-facing budget; wall-clock already proxies total occupancy.
- **Default for `MaxTurnTokens`?** Needs measurement against real qwen3.6 todo-flow turns
  (the 2026-06-07 sessions are a starting corpus).
- **UI surface on budget trip** — silent wind-down, or a visible "budget exhausted"
  marker event? Leaning: visible; silent truncation reads as model failure.
- **Steering × thinking models** — injected tail messages interact with reasoning replay
  (`PreserveThinking`); verify against vLLM 0.22 before committing to 4c's placement.
