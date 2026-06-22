# Gert.Agent refactor — collapse `Turn*` into two mechanisms

> Working plan. Pre-release, no users — breaking changes and history rewrites are
> acceptable. This is the *plan*; the design docs (`docs/design/chat-and-tools.md`,
> the read-side of `docs/design/rest-api.md`) get rewritten from it before code lands.

## 1. Why

The `Turn*` stack (37 files, ~3.7k lines) isn't convoluted because it's badly
written — it's convoluted because **one noun, "Turn", carries two unrelated
lifecycles**:

- a **compute lifecycle** — a model+tools run that starts, streams, and ends; and
- a **durability lifecycle** — a persisted, resumable, replayable record.

Stapling them together is why cancellation, questions, planning, the job, the
durable log, and the bus all wear the `Turn` prefix and bleed into one another. The
felt mess — five overlapping observation callbacks, a `DeltaSink` with a split
personality, two structurally-identical registries, a "rebuild on reconnect" path
that's separate from the live path — all traces back to that one conflation.

**Fix: split the noun.** Everything below follows from two mechanisms with zero
overlap.

## 2. Target model — two mechanisms

### A. The agent — compute, in your name, in the background

Process-local. Owns identity/privileges, its background task (← the agent number),
and an event stream out. Its **only** output is `AgentEvent`. It knows nothing about
logs, buses, conversations, HTTP, or resumption.

```csharp
public interface IAgent
{
    // "Do this in my name."  job = identity + privileges + history + tools + budgets
    IAgentRun Start(AgentJob job);
}

public interface IAgentRun
{
    int Number { get; }                            // the agent number; this run's background identity
    IAsyncEnumerable<AgentEvent> Events { get; }   // "while it's busy, I get stuff back"
    Task Completion { get; }                       // ran-to-end / faulted / cancelled
}
```

Internally the loop emits through **one** sink method (cheap producer, threads
through `StreamRoundAsync`/`ExecuteRoundAsync` exactly where `DeltaSink` does today);
a `Channel` bridges to the `IAsyncEnumerable` the caller reads. Sink inside, stream
out — the producer-cheap / consumer-nice synthesis.

```csharp
internal sealed class Agent(IAgentLoop loop, IIdentityBinder identity) : IAgent
{
    private int _counter;

    public IAgentRun Start(AgentJob job)
    {
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var run = new AgentRun(Interlocked.Increment(ref _counter), channel.Reader);
        run.Task = Task.Run(async () =>                       // the background task the number names
        {
            var sink = new ChannelSink(channel.Writer);
            try   { identity.Bind(job); await loop.RunAsync(job.ToRequest(sink), run.Token); }
            finally { channel.Writer.TryComplete(); }
        });
        return run;
    }
}

internal sealed class ChannelSink(ChannelWriter<AgentEvent> w) : IAgentEventSink
{
    public ValueTask EmitAsync(AgentEvent ev, CancellationToken ct) => w.WriteAsync(ev, ct);
}
```

### B. The conversation event log — truth + cross-instance sync

An append-only event log per conversation in the DB. **The single source of truth.**
The agent's events are pumped into it by the caller (the thin tee that's all that's
left of `TurnRunner`):

```csharp
var run = agent.Start(job);                       // do this in my name, in the background
await Emit(MessageStart(job));                     // transport framing stays with the caller
await foreach (var ev in run.Events)               // while it's busy, I get stuff back
    await log.AppendAsync(conversationId, ev);     // the ONE persistence mechanism; doorbell fires inside
// loop exception → FinalizeError here; deadline/cancel → FinalizeCancelled
```

Everything observable is a **read of this one log**:

```
live (attached)   = read from cursor, then follow the in-process doorbell
reconnect/detach  = read from cursor, then poll the DB
history for a run = fold the log into coalesced messages
```

## 3. `AgentEvent` taxonomy → `Gert.Model`

The cross-cutting vocabulary both the loop and its consumers speak. POCOs, no deps —
lives in `Gert.Model`. `ExecutedToolCall` moves with it (already Model-shaped: only
references `Gert.Model{,.Chat,.Events}` — `Citation`/`Artifact`/`ToolCallStatus`;
its lone `using Gert.Agent` is a doc `<see cref>` the refactor deletes).

```csharp
// Gert.Model/Agent/AgentEvent.cs
namespace Gert.Model.Agent;

public abstract record AgentEvent;
public sealed record TextDelta(string Text)                   : AgentEvent;
public sealed record ReasoningDelta(string Text)              : AgentEvent;
public sealed record ToolStarted(string CallId, string Name)  : AgentEvent;  // the live "Running" card
public sealed record ToolCompleted(ExecutedToolCall Call)     : AgentEvent;  // carries citations + artifacts
public sealed record RoundCompleted(int Round, int Tokens)    : AgentEvent;
public sealed record TurnFinished(AgentResult Result)         : AgentEvent;  // AgentResult = today's AgentLoopResult
```

Consequence: `AgentLoop` stops referencing `ChatEvent` (a transport type). The
`AgentEvent → ChatEvent` mapping concentrates in the caller's tee, next to the bus
and repo — one `switch`, replacing the five callbacks (`Emit`, `OnText`,
`OnReasoning`, `OnProgress`, `OnToolExecuted`).

## 4. Split accumulate from coalesce — the reuse keystone

`DeltaSink` does two unrelated jobs; that split personality is the core smell. Split
them, and **the accumulate half becomes the reconnect-rebuild function** — not new
code, the same fold.

```csharp
// Gert.Model/Agent/DeltaAccumulator.cs — pure fold: AgentEvent → renderable state.
// No transport, no timing, no I/O. Used at TWO call sites.
public sealed class DeltaAccumulator
{
    private readonly StringBuilder _content   = new();
    private readonly StringBuilder _reasoning = new();
    public void Apply(AgentEvent ev)
    {
        switch (ev)
        {
            case TextDelta t:      _content.Append(t.Text);   break;
            case ReasoningDelta r: _reasoning.Append(r.Text); break;
            // discrete events (tool started/completed, citations) pass through as-is
        }
    }
    public string Content   => _content.ToString();
    public string Reasoning => _reasoning.ToString();
}
```

| | lives in | live path | reconnect path |
|---|---|---|---|
| **accumulate** (`DeltaAccumulator`) | `Gert.Model` | builds `AgentResult.Content` as the run goes | **reused** — folds the log slice to rebuild the in-flight message |
| **coalesce** (`DeltaCoalescer`) | caller / read side | batches deltas → append to log + ring doorbell (cut write/publish amplification) | **not used** — log is already granular |

This settles the history fork: **event-source it.** The log is truth; `messages`
becomes a droppable cache of `acc.Content` at message boundaries, rebuildable by
re-folding. Reconnect stops being a separate mechanism — it's "replay the log
through the same accumulator."

## 5. Delivery — one data source, two modes

Multi-instance reality: the agent loop is a background thread **pinned to one Gert
process**. The doorbell ("new seq available") lives in that process's memory. The
**only** thing crossing instances is the DB. So:

```
SOURCE OF TRUTH (cross-instance):  the conversation event log in the DB.

FORWARD DELIVERY:
  attached     = co-located with the running thread → in-process doorbell pushes live over the TCP stream
  detached /   = dropped, or reconnected to ANOTHER instance → poll the DB log from cursor;
  reattached     terminal event present → done, stop polling
```

**Polling the DB log is the floor — always correct, cross-instance. The in-process
push stream is a latency optimization available only while attached.** Push must
never be load-bearing for correctness.

## 6. Back-channel (cancel / `ask_user` answer) — always via the DB

**No in-memory live-runs registry.** HTTP on any instance writes a control row;
the running instance observes it on its sync beat. One synchronization point — the
DB — for both the forward stream and the back-channel. This deletes the registry
*and* the tombstones (a cancel written before the loop starts is just a control row
the loop's first read observes — the durable row **is** the tombstone) *and* the
dual-source cancellation token.

The loop's effective cancellation = `host-shutdown-token OR wall-clock-deadline OR
control-row-observed`, all checked at each sync beat.

`ask_user`: emit `QuestionAsked` into the log; the loop polls for the matching answer
row until it appears or the deadline fires — same mechanism as cancel.

**Semantic change to call out in the doc:** cancellation is now eventually-consistent,
bounded by the sync-beat cadence — not the instant in-memory CTS fire.

### The four locking rules (an implementer will hurt themselves here)

The loop is a steady writer (event appends); cancel/answer are rare tiny writes to
the same `chat.db`. The failure mode is real but almost always self-inflicted by
transaction scope, not by SQLite.

1. **Never hold a write transaction across a round, a model stream, or a tool call.**
   The deadlock you fear: `BEGIN` at round start / `COMMIT` at round end means the
   cancel write can't acquire the lock *and* the loop can't observe the cancel until
   it commits. Every event append is its own tiny autocommit write; the connection
   sits idle between appends.
2. **WAL + `busy_timeout` (a few seconds).** In WAL, readers never block the writer
   and vice-versa; the only contention is writer-vs-writer (append vs. control
   write), both sub-millisecond and single-statement, so the loser waits and
   succeeds rather than getting `SQLITE_BUSY`.
3. **Control reads must be fresh point reads.** A long-lived *read* transaction in
   WAL pins a snapshot and will **never** see a cancel row written after it began.
   Read the control table with a fresh statement each beat — never inside a held
   read transaction.
4. **Fold the control-read into the flush cadence you already have.** The loop
   already touches the DB on every coalescer flush to append events; read the control
   table in that same beat. `append events + read {cancel?, answer?}` = one sync
   beat. Bounds cancel/answer latency to the (UX-tuned) flush interval, adds no extra
   cadence, aligns the contention window to writes you're doing anyway.

Already-DB-mediated and consistent with this: the cross-instance "one active run"
gate is the `ux_messages_streaming` partial-unique index `TurnPlanner` inserts under
— that index *is* the single-writer lock. Routing the back-channel through the DB is
the same coordination substrate, not a new pattern.

## 7. What dies / moves / stays

| Today | Fate |
|---|---|
| `TurnRunner` (491) | **dissolves** — the persist tee (the `foreach`) is all that remains; identity binding + background task + cancellation move into `Agent` |
| `DeltaSink` (157) | **splits** — `DeltaAccumulator` (Model, reused) + `DeltaCoalescer` (caller, live-only) |
| `AgentLoopRequest` 5 callbacks | **one** `IAgentEventSink` |
| `ITurnCancellation` + `ITurnQuestions` + `TurnCancellation` + `TurnQuestions` + tombstones | **deleted** — DB control rows + sync-beat polling |
| `TurnJob` | `AgentJob` (identity + privileges + history + tools); not a "turn" |
| `TurnRunner` reconnect/rebuild + bus + `turn_events` | **one** append-only log + a doorbell; reconnect = poll + fold |
| `AgentLoop` → `ChatEvent` dependency | gone; emits `Gert.Model.Agent.AgentEvent` only |
| `MessageStatusRules` 409 | "one active writer per conversation" (the partial-unique index) |
| orphan write-back | "last log event non-terminal & older than `MaxTurnDuration` → append synthetic error" |
| `TurnPlanner` (501) | thins to the request handler: validate → append user message → `agent.Start` |
| `ChannelTurnQueue` / `TurnWorker` | `agent.Start` owns the background task; a global concurrency gate replaces the shards (TBD — see open questions) |
| `DetachedUserContext` | becomes the agent's `IIdentityBinder` seam |
| two-phase plan/run, entitlement snapshot, per-user SQLite scoping | **stay** — the concurrency/security spine, untouched in intent |

## 8. Implementation phases (proposed; iterate)

1. **`Gert.Model` vocabulary.** Add `AgentEvent` hierarchy + `AgentResult`; move
   `ExecutedToolCall`. No behaviour change.
2. **`DeltaAccumulator`** (Model) + **`DeltaCoalescer`** (read side). Unit-test the
   fold against a recorded event sequence == final message content.
3. **`IAgentEventSink` + collapse the 5 callbacks.** Loop emits `AgentEvent` through
   one sink; `TurnRunner` temporarily implements the sink with the old callback
   bodies (behaviour-preserving seam).
4. **`IAgent` / `IAgentRun` + `ChannelSink`.** Loop runs behind the agent; `Agent`
   owns the background task + identity. `TurnRunner` becomes the tee `foreach`.
5. **Event log as truth + doorbell-as-optimization.** Unify `turn_events`/bus reads
   behind "read-from-cursor (+ follow | poll)"; reconnect re-folds via
   `DeltaAccumulator`. `messages` demoted to cache.
6. **DB back-channel.** Control table; loop reads it on the flush beat; delete the
   registries + tombstones + dual CTS. Apply the four locking rules. Wire `ask_user`
   answers through it.
7. **Queue/worker simplification** (see open questions) + delete dead `Turn*` types.
8. **Docs.** Rewrite `chat-and-tools.md` + read-side of `rest-api.md` from this plan;
   re-point code-comment doc citations; `make check-links`.

Each phase should keep `make build` (warnings = errors) + `make test` green and the
architecture tests (inward-only refs; `Gert.Agent` ⊄ host/impl; `Gert.Service` ⊄
`Gert.Agent`) intact.

## 9. Open questions to iterate

- **Queue vs. gate.** With one-active-run enforced by the partial-unique index, is a
  per-conversation FIFO lane ever needed, or is a second concurrent run simply 409'd?
  If so, `ChannelTurnQueue` + sharded `TurnWorker` collapse to `agent.Start` + a
  global concurrency `SemaphoreSlim(MaxConcurrentTurns)`. Confirm nothing relies on
  in-conversation queueing.
- **Sync-beat cadence.** Reuse the coalescer flush interval verbatim, or a separate
  (faster) control-poll cadence for snappier cancel? Trade latency vs. write churn.
- **`messages` cache: keep or delete?** If deleted, every run folds the full log for
  history — fine for small local conversations, but set a compaction/snapshot
  threshold now or later?
- **`ToolStarted` granularity.** Does the live "Running" card need args echoed, or is
  `(callId, name)` enough for the UI? Affects the event shape.
- **Doorbell transport.** Keep the existing in-process bus as the doorbell, or
  replace with a lighter "max seq" signal that tailers wait on?
- **Identity seam naming.** `IIdentityBinder` vs. keeping `DetachedUserContext` as-is
  behind the agent.

## 10. Invariants that must survive

- User key only from the validated token; `pid` only ever joined under the
  token-derived folder.
- Persist-before-publish: append to log → bump seq → ring doorbell. Push is never
  load-bearing for correctness; poll is the floor.
- "The claim is the ceiling": entitlement snapshot captured at plan time, re-checked
  per tool call inside the loop.
- Fail-closed validation; every request DTO keeps its `IValidator<T>`.
- Security findings F1–F12 keep their tests; don't weaken a control without reading
  its finding.
