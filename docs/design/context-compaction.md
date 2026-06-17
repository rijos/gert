# Context compaction - keeping conversations inside the window

**Status: open design - under discussion.** Nothing here is implemented. Today a
conversation's upstream history grows without bound; the only "policy" is the upstream
model erroring when the prompt no longer fits, which surfaces as a failed turn. The
composer's context ring (`messages.context_tokens`, migration 003) *measures* the problem
but nothing *acts* on it. This note maps the option space so we can pick the mechanisms
before the implementation is scheduled into a build plan.

---

## 1. The problem

The planner rebuilds upstream history as **role + content only** (tool calls/results never
re-enter the prompt - [chat-and-tools](chat-and-tools.md#chat-orchestration-the-tool-loop)),
so growth is genuine user/assistant text - plus, on a thinking provider whose
`chat_template_kwargs.preserve_thinking` is on
([installation section providers](../installation/configuration.md#4-gertchatproviders---the-chat-provider-catalog)),
prior turns' reasoning replayed as `reasoning_content`. (Model-authored *files* are already
out of the picture: the canvas tool suite carries them as tool arguments, which drop from
history, and `read_artifact` is the model's recall path -
[chat-and-tools section artifacts](chat-and-tools.md#artifacts-the-canvas-tool-suite).)
Long-running conversations therefore:

1. **Hard-fail eventually.** Prompt > model context -> upstream 400 -> the turn errors.
   The user gets a dead conversation with no path forward except starting over.
2. **Degrade before failing.** Models reason worse over very long contexts, and every
   round re-bills the full prompt - slow turns, hot GPU, for tail content that rarely
   matters.
3. **Punish thinking mode hardest.** Reasoning replay multiplies history size for the
   turns where headroom matters most.

## 2. Constraints (what any mechanism must respect)

| Constraint | Consequence |
|---|---|
| **Prefix-cache friendliness** ([installation section 3](../installation/configuration.md#3-gertembeddings---the-embeddings-upstream)) | History bytes must stay stable turn-over-turn. Anything that rewrites the *head* of the prompt invalidates the whole cache - acceptable rarely (once per compaction), fatal if it happens every turn. This is the argument **against** sliding-window truncation. |
| **The DB is the source of truth; transports replay it** | Compaction must never mutate or delete persisted messages. It changes what the *planner sends upstream*, not what the user sees. Any durable compaction state needs its own column/row with normal seq/event semantics if it's user-visible. |
| **Detached turns** ([chat-and-tools](chat-and-tools.md#detached-turns)) | Work can run off-turn: the background-worker pattern already exists, so summarization need not add latency to the turn that triggers it. |
| **Per-provider windows** | the selected provider's `Context` (`Gert:Chat:Providers:<slug>:Context`) is the budget denominator; a conversation switched to a smaller-window provider must re-evaluate immediately. |
| **Measurement exists** | The previous turn's `context_tokens` (prompt + completion of the final round) is ground truth from vLLM's usage tail - no tokenizer dependency needed for the trigger. |
| **Fail-closed, visible trips** ([turn-budgets](turn-budgets.md) precedent) | Whatever bounds we add must trip *visibly* (an event on the conversation), never silently degrade. |

## 3. Candidate mechanisms

### 3a. Bound + visible trip - the floor, do first
Before planning a turn, estimate the prompt (last `context_tokens` + new content at
~4 chars/token) against the model's `Context` with a headroom factor. Over the line ->
fail **fast and clearly** (a `context_exhausted` event naming the limit and the ways out:
compact, new conversation, bigger model) instead of letting vLLM 400 mid-stream. ~Small,
no behaviour change for healthy conversations, converts the worst failure mode into an
actionable one.

### 3b. Cheap elision - recover tokens without an LLM call
Lossy-but-mechanical trims to *older* turns, applied in order until the prompt fits:

1. **Drop replayed reasoning** beyond the last N assistant turns (when the selected
   provider replays it - `chat_template_kwargs.preserve_thinking` on). Zero information
   loss for final answers; reasoning is only interleaved-useful near the tail.
2. **Stub large inline code blocks in old turns.** Whole files already stay out of
   history (the canvas tool suite - tool args drop from the prompt, `read_artifact`
   recalls), so this only covers big fenced snippets the model pasted *in prose*;
   replace the fence body with a one-line stub in the upstream rendering of old
   messages. The bubble is untouched.
3. **Drop old image attachments** (vision parts) from upstream history past N turns.

Elision changes prompt bytes for the affected turns once (one cache invalidation when it
first kicks in), then those turns are stable again.

### 3c. Auto-compaction - summarize-and-anchor
When the post-turn measurement crosses a threshold (e.g. 70-80 % of `Context`):

- A **background job** (the detached-worker pattern; ingestion/turn queues already model
  this) summarizes the oldest turns up to a cut point into a compact block.
- Stored on the **conversation row**: `compaction_summary` + `compacted_upto_seq`
  (+ `compacted_at`). The planner then renders: system prompt -> summary block -> messages
  with `seq > compacted_upto_seq`. Re-compaction folds the old summary into the new one.
- Persisted rows are untouched - reload still shows the full thread; only the upstream
  prompt shrinks. A `compacted` event (with the token counts) renders a small divider in
  the thread: "older messages summarized for the model".
- **Cache cost:** one full invalidation per compaction, then a long stable run - amortized
  exactly like the `TodoReminder` tail rule intends.
- **Who summarizes:** the catalog's `Fast` model if one is configured, else the
  conversation's model with thinking off and a tight `max_tokens`.

### 3d. Sliding-window truncation - rejected
Dropping oldest turns wholesale each turn keeps the prompt bounded but (a) rewrites the
prompt head **every** turn - worst-case prefix-cache behaviour, (b) silently forgets, with
no record of what was lost. Only acceptable as the emergency fallback inside 3a when
even compaction can't fit (e.g. a tiny-context model).

### 3e. Promote-to-memory - the durable end of the spectrum
Compaction summaries that contain *facts worth keeping* overlap with project memory
([configuration section 2.3](configuration.md#23-memory)). A "promote to memory" affordance on
the compaction divider would let the user (or later, `auto` memory mode) move durable
facts out of conversation scope entirely. Out of scope for v1 of compaction; the summary
column design must just not preclude it.

## 4. Recommendation

Phased, same shape as [turn-budgets section 5](turn-budgets.md#5-recommendation):

1. **Phase 1 - 3a (bound + visible trip).** Smallest honest step; removes the cliff.
2. **Phase 2 - 3b (elision), reasoning-replay trim first.** No model cost, large wins for
   thinking-mode and canvas-heavy conversations.
3. **Phase 3 - 3c (auto-compaction).** The real fix for "this conversation should live for
   weeks". Ships with the visible divider event and a per-conversation toggle
   (`off - auto`, default `auto`) - a new conversation/settings field, *not* the deleted
   sampling cascade.
4. 3d only as the in-3a emergency fallback; 3e parked until memory `auto` is revisited.

## 5. Open questions

- **Trigger threshold** - fixed % of `Context` (one knob, e.g. `Gert:Turn:CompactAt = 0.75`)
  or absolute-tokens-remaining? Leaning %: it tracks model swaps for free.
- **Cut point** - compact everything but the last N turns, or last-K-tokens? Leaning
  last-K-tokens (keeps short recent turns intact regardless of count).
- **Summary visibility** - divider-only, or expandable to read the summary text? Editable?
  (Editable summary = the user steering what the model remembers - attractive, more UI.)
- **Summarizer choice** - `Fast` catalog model vs the conversation's model: quality vs an
  extra deployment expectation. Default if no `Fast` model exists?
- **Interaction with reasoning replay** - compaction and a thinking provider's reasoning
  replay (`chat_template_kwargs.preserve_thinking`) both touch upstream history rendering;
  the planner needs one ordering rule (proposal: elision first, then compaction cut, replay
  only after the cut).
- **Estimation slack** - the 4-chars/token heuristic for the *new* message: good enough,
  or do we want the upstream `/tokenize` endpoint when available?
