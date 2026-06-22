# Refactor: adopt Microsoft.Extensions.AI, scrap the custom chat wire layer

Status: PLAN, not started. Author handoff for an autonomous `claude --dangerously-skip-permissions`
session. Baseline is the CURRENT (dirty) working tree on branch `refactor/tool-capability-host`,
mid-flight on the "split the noun" refactor (see `refactor.md`). Do NOT `git reset`, do NOT try to
finish `refactor.md`'s pending phases; treat the working tree as-is as the starting point.

ASCII-only prose and comments (CLAUDE.md). Cite docs by section in code comments. `make check-links`
after any doc edit. Warnings are errors.

---

## 0. Goal and the one idea

Replace Gert's hand-rolled model-wire abstraction with the first-party Microsoft.Extensions.AI
(M.E.AI) stack: `IChatClient`, `IEmbeddingGenerator`, `AIFunction`, and `FunctionInvokingChatClient`.
Delete the wire-translation we maintain; keep only the Gert-specific behavior M.E.AI has no equivalent
for, re-homed into thin `DelegatingChatClient` wrappers and a `FunctionInvokingChatClient` subclass.

The single design idea driving the tool half: **push intelligence into the tool.** Today a tool returns
a rich `ToolResult` (citations, artifacts, todos, stdout) that loop-side code dissects
(`ToolOutcome` -> `ExecutedToolCall` -> the `TurnRunner` tee). After this refactor a tool returns only
its model-facing result and EMITS its own UI side-effects through a host seam it already holds
(`IToolHost`). That deletes the dissection plumbing and makes a new tool self-contained.

### What "scrap Gert.Chat" does and does NOT mean

DELETE (custom wire layer, M.E.AI replaces it): `IChatModelClient`/`ChatModelChunk`/
`ChatCompletionRequest`/`ChatToolSpec`/`ChatModelToolCall`/`ToolCallStart`/`ChatModelMessage`/
`ChatModelImage` (the wire DTOs in `Gert.Model/Chat/`), `OpenAIChatRequestBuilder`, the
`OpenAIChatModelClient`/`OpenAIStreamParser` as the port impl, `OpenAIEmbeddingClient` as the port impl.

KEEP (genuine Gert logic with no M.E.AI analog; re-home, do not delete):
- The multi-provider catalog: `ConfigChatProviderCatalog`, `IChatProviderCatalog`,
  `ChatProviderOptions`, `IDefaultChatProvider`, `IChatClientFactory` (now returns `IChatClient`).
- The `Gert:Chat:Providers` config shape and `ChatProviderParameters`/`EmbeddingsParameters`.
- The named-`HttpClient` + Polly transport wiring and `OpenAIWireLogger` (these live BELOW `IChatClient`
  at the `HttpClient` layer and survive untouched).
- Stream salvage (the `<tool_call>` leak recovery), `reasoning_content` extraction, the live-intent
  tool-call-start signal, vendor extra params, embeddings dimension validation. These move into
  wrappers; they do not disappear.
- The Gert event architecture: `AgentEvent`/`IAgentEventSink`/`Agent`/`ChannelSink`/`AgentRun` and the
  `TurnRunner` tee. M.E.AI has no analog; it stays and adapts M.E.AI output into `AgentEvent`s.
- All domain types (`Conversation`, `Message`, `ToolCall`, `Citation`, `Artifact`, `TodoItem`,
  `MessageStatus`). Untouched.

### Decisions baked into this plan (override if you disagree, but note why)

1. **Keep `IChatModelClientBuilder` as the chat plugin seam**, changing its `Build` to return
   `IChatClient`. Rationale: `Gert.Chat.Tests/PluginArchitectureTests` asserts `IChatModelClientBuilder`
   is the chat capability's plugin interface (registrar `AddGertChatOpenAI`, contracts-vs-impl split).
   Deleting it forces rewriting that test and the keyed-plugin pattern for one capability only. Minimal
   churn keeps the pattern uniform with Database/Rag/Search/Sandbox.
2. **Keep Gert's internal ports' CONSUMERS stable by swapping types, not by wrapping.** The user asked to
   scrap the abstraction, so `IChatClient` flows into `AgentLoop`/`TurnRunner` directly (not hidden under
   the old `IChatModelClient`). The cheap "wrap underneath" path is explicitly rejected per the user.
3. **Provider catalog stays.** "Scrap Gert.Chat" is the wire layer, not multi-provider config. Losing
   the catalog would lose thinking-vs-instruct provider selection (chat-and-tools.md section sampling).
4. **Extra/vendor params go through `ChatOptions.RawRepresentationFactory`** seeding the OpenAI SDK
   `ChatCompletionOptions`, then the SAME JsonPatch calls today's `OpenAIChatRequestBuilder.ApplyExtra`
   makes (SDK 2.6+ JsonPatch; we are on 2.11.0). This is the concrete answer to "how do we add extra
   parameters" now that the vLLM-side constraint is patched.
5. **Three landable movements** (A provider, B tools, C orchestration), each independently buildable and
   test-green, then D docs+cleanup. Do not collapse them; the test gates between them are the safety net.

---

## 1. Pre-flight (do this first, once)

1. Confirm the baseline builds and tests green on the current dirty tree:
   `make build && make test`. If anything is RED before you start, record exactly what in a scratch note
   and do not attribute it to your work. Do not "fix" pre-existing reds unless they block you.
2. Create a working branch off the current one: `git switch -c refactor/meai` (keeps the dirty changes).
3. Read these before touching code: `docs/design/README.md` (routing table), `docs/design/chat-and-tools.md`
   (the tool loop, tool host, detached turns, per-tool bounds, sampling sections),
   `docs/design/tech-stack.md` line ~13 (Model API), `docs/design/decisions.md` (format; you will add
   one record), `refactor.md` (the in-flight split-the-noun design; this plan composes with it, does not
   undo it).
4. M.E.AI version reality check: `Directory.Build.props` pins `Microsoft.Extensions.*` at 10.0.9 and
   `OpenAIVersion` 2.11.0. Find the latest STABLE `Microsoft.Extensions.AI` and `Microsoft.Extensions.AI.Abstractions`
   compatible with net10.0, and the latest `Microsoft.Extensions.AI.OpenAI`. NOTE: the OpenAI adapter
   package has historically shipped as PREVIEW behind the stable abstractions. If only a preview is
   available, that is an accepted risk for this refactor (the user has decided), but RECORD the exact
   version you pinned and whether it was preview, in the decision record (section 5, doc step).

API caveat for the whole plan: exact M.E.AI member names below (`RawRepresentationFactory`,
`FunctionInvokingChatClient`, `FunctionInvocationContext`, `AIFunctionFactory.Create`,
`AsIChatClient`/`AsIEmbeddingGenerator`) are the INTENDED mechanism. Verify each against the version you
install before relying on its exact signature; adapt names as needed but preserve the behavior.

---

## 2. Movement A: provider layer (IChatClient + IEmbeddingGenerator)

Outcome: the model and embedding ports are M.E.AI types; the multi-provider catalog, transport wiring,
wire logger, stream salvage, reasoning extraction, vendor params, and embeddings validation all survive.
The custom `AgentLoop` STILL owns the tool while-loop in this movement (it just talks to `IChatClient`).

### A1. Packages
- `Directory.Build.props`: add `<MicrosoftExtensionsAIVersion>` (abstractions + core) and
  `<MicrosoftExtensionsAIOpenAIVersion>`. Add a comment noting the OpenAI adapter version + preview state.
- `Gert.Chat.csproj`: add `Microsoft.Extensions.AI.Abstractions` (contracts-safe; `IChatClient`,
  `IEmbeddingGenerator`, `ChatMessage`, `AITool` live here).
- `Gert.Chat.OpenAI.csproj`: add `Microsoft.Extensions.AI` (core, for the delegating-client base + DI
  helpers) and `Microsoft.Extensions.AI.OpenAI` (the `.AsIChatClient()`/`.AsIEmbeddingGenerator()`
  adapters). Keep the existing `OpenAI` 2.11.0 reference (the adapter rides the same SDK client).

### A2. Gert.Chat contracts (re-home, do not gut)
- Replace `IChatModelClient` (`src/Gert.Chat/IChatModelClient.cs`) usage with M.E.AI `IChatClient`.
  Delete the file.
- `IChatClientFactory.ForProvider(providerId)` now returns `IChatClient`
  (`src/Gert.Chat/IChatClientFactory.cs`, `ChatClientFactory.cs`). Keep the per-id resolve+cache logic
  and the `catalog.Resolve(providerId)` sentinel handling (`default`/unset/unknown -> default slug).
- `IChatModelClientBuilder.Build(providerId)` now returns `IChatClient`
  (`src/Gert.Chat/IChatModelClientBuilder.cs`). Keep the keyed-by-Type plugin seam (decision 1).
- Replace `IEmbeddingClient` (`src/Gert.Chat/IEmbeddingClient.cs`) with M.E.AI
  `IEmbeddingGenerator<string, Embedding<float>>`. Delete the file. The dimension/count validation moves
  to a wrapper (A4).
- KEEP unchanged: `IChatProviderCatalog`, `ConfigChatProviderCatalog`, `ChatProviderOptions`,
  `IDefaultChatProvider`, `ServiceCollectionExtensions.AddGertChat` (it still registers the catalog +
  factory; only the factory's return type changed).

### A3. Gert.Chat.OpenAI chat impl
- KEEP, untouched: `OpenAIWireLogger` (HttpClient `DelegatingHandler`, logs raw wire bytes below
  `IChatClient` -- M.E.AI's `UseLogging` logs the abstraction, not bytes, so this stays),
  `ChatProviderParameters`, `OpenAIDefaultChatProvider`, the `/v1` endpoint fixup and `CreateSdkClient`
  policy (infinite `NetworkTimeout`, `RetryPolicy(0)` so Polly owns resilience), and the entire named-
  `HttpClient` + `AddStandardResilienceHandler` per-slug wiring in `ServiceCollectionExtensions`.
- DELETE: `OpenAIChatRequestBuilder.cs` (message/tool/vision mapping is what the adapter does) and the
  `IChatModelClient` body of `OpenAIChatModelClient.cs`. Salvage the SDK-client construction helper into
  the builder.
- RE-HOME stream salvage + reasoning into a `DelegatingChatClient` wrapping `chatClient.AsIChatClient()`.
  Call it `SalvagingChatClient` (new, in `Gert.Chat.OpenAI`). It must reproduce, from
  `OpenAIStreamParser.cs`, behavior the adapter does NOT provide:
  - The `<tool_call>` leak hold-back state machine + salvage-parse (Hermes JSON + qwen3-coder XML),
    `SalvagedToolCalls`/`DroppedLeakChars` accounting logged after the stream
    (chat-and-tools.md section tool-call robustness).
  - `reasoning`/`reasoning_content` delta extraction from the raw representation into M.E.AI reasoning
    content (the adapter will NOT populate reasoning from vLLM's non-spec field).
  - Argument normalization: accumulated tool-call args must parse as a JSON object or degrade to `{}`
    (vLLM 0.22 qwen3 unterminated-`{` bug).
  - The live-intent name-first signal is handled in Movement C (it is a loop concern); here just make
    sure streaming `FunctionCallContent` updates flow through cleanly.
- Vendor/extra params: build `ChatOptions.RawRepresentationFactory` to seed an OpenAI SDK
  `ChatCompletionOptions` and apply the same JsonPatch fields `OpenAIChatRequestBuilder.ApplyExtra` set:
  `top_k`, `min_p`, `repetition_penalty`, `chat_template_kwargs.enable_thinking`/`preserve_thinking`,
  and per-message assistant `reasoning_content` (gated by `PreserveThinking`). Spec-typed sampling
  (`Temperature`, `TopP`, `PresencePenalty`, `FrequencyPenalty`, `Seed`, `StopSequences`,
  `MaxOutputTokens`, tool-choice-only-with-tools) maps to `ChatOptions` fields directly.
- `OpenAIChatModelClientBuilder.Build(slug)` -> construct the SDK client over the slug's named
  `HttpClient` + `ChatProviderParameters`, call `.AsIChatClient()`, wrap in `SalvagingChatClient`,
  return `IChatClient`.

### A4. Embeddings
- `OpenAIEmbeddingClient` -> replace with `chatClient.GetEmbeddingClient(model).AsIEmbeddingGenerator()`
  plus a `DelegatingEmbeddingGenerator` wrapper (new) that preserves Gert validation: order-by-index
  reassembly, dimension assertion (reject `!= 1024` for bge-m3), count assertion. Keep
  `EmbeddingsParameters`, `EmbeddingsOptions`, the fail-closed `EmbeddingsTypeValidator`, and the named
  `openai-embeddings` HttpClient.
- `ReadinessCheck` (`src/Gert.Api/Logging/ReadinessCheck.cs`) probes `OpenAIEmbeddingClient.HttpClientName`
  -- repoint it at the preserved constant.

### A5. Consumers (swap types, keep logic)
- `AgentLoop` (`src/Gert.Agent/Loop/AgentLoop.cs`): `StreamRoundAsync` consumes
  `IChatClient.GetStreamingResponseAsync(messages, ChatOptions, ct)` yielding `ChatResponseUpdate`
  instead of `IChatModelClient.StreamAsync` -> `ChatModelChunk`. Map updates -> existing `AgentEvent`s
  (`TextDelta`, `ReasoningDelta`, collect tool calls). `NewCompletion` builds `ChatOptions` + a
  `List<ChatMessage>` instead of `ChatCompletionRequest`. `Toolset.AdvertisedSpecs` becomes
  `IList<AITool>` on `ChatOptions.Tools` (see Movement B for tool shape; in A you may keep a temporary
  `AITool` projection from `ChatToolSpec`). The tool while-loop, budgets, entitlement, wind-down stay.
- `AgentLoopRequest.Model` becomes `IChatClient`. `ChatToolDelegate` holds an `IChatClient`.
- `TurnRunner` `_clients.ForProvider(job.ModelId)` now yields `IChatClient`.
- `ProjectRagResource` + `IngestionService` use `IEmbeddingGenerator` (`GenerateAsync`) instead of
  `IEmbeddingClient.EmbedAsync`.
- `TurnPlanner` + `ModelsController` keep using `IChatProviderCatalog` unchanged.

### A6. Wire DTOs
- Delete `ChatModelChunk`, `ChatCompletionRequest`, `ChatToolSpec`, `ChatModelToolCall`, `ToolCallStart`,
  `ChatModelImage`, and `ChatModelMessage` from `src/Gert.Model/Chat/` once no consumer references them.
  Keep ALL domain types (`Conversation`, `Message`, `ToolCall`, `Citation`, `Artifact`, `TodoItem`,
  `MessageStatus`, `MessageAttachment`). `ChatModelMessage` was the loop's working-message type; it is
  replaced by M.E.AI `ChatMessage` in `AgentLoop`'s working list.

### A7. Fakes + tests
- Replace `Gert.Testing/Fakes/FakeChatModel.cs` (an `IChatModelClient`) with a fake `IChatClient`, and
  `FakeEmbeddings.cs` with a fake `IEmbeddingGenerator`. `FixedChatClientFactory.cs` returns the fake
  `IChatClient`.
- `tests/Gert.Chat.Tests/`: the request-builder and stream-parser tests change shape. Port the salvage,
  reasoning-extraction, and arg-normalization assertions to target `SalvagingChatClient`. Port the
  vendor-extra-param assertions to target the `RawRepresentationFactory` path. Keep the wire-logger test
  (it tests the HttpClient handler, unchanged). Keep the catalog tests unchanged.

### A8. Gate A
`make build && make test`. The two architecture suites and `PluginArchitectureTests` must stay green
(decision 1 keeps the plugin seam). `AgentLoopTests` and `TurnRunnerTests` should pass with the fakes
swapped -- the loop semantics are unchanged in Movement A.

COMMIT: "Movement A: chat/embeddings ports on Microsoft.Extensions.AI".

---

## 3. Movement B: tool model (ITool -> AIFunction, side-effects to a host seam)

Outcome: tools become M.E.AI `AIFunction`s; the rich `ToolResult` shrinks to the model-facing payload;
citations/artifacts/todos/stdout are EMITTED by the tool through a new host seam. `AgentLoop` still owns
orchestration but now executes `AIFunction`s. This is the "intelligence into the tool" movement.

### B1. The host emit seam (the crux)
Add to `IToolHost` (`src/Gert.Tools/Hosting/IToolHost.cs`) a narrow output surface, e.g. `IToolCard Card`,
that a tool calls to report side-effects:
- `void ReportCitations(IReadOnlyList<Citation> citations)`
- `void ReportArtifact(Artifact artifact)`
- `void ReportStdout(string text)`
- `void ReportTodos(IReadOnlyList<TodoItem> todos)`
The IMPL lives in `Gert.Agent` (the chat driver owns persist-then-publish + the `tool_calls` row id +
citation renumber/bind). The tool decides WHAT to emit; `TurnRunner` still owns HOW it is persisted and
ordered. This preserves the architecture-test boundary: `Gert.Tools.Builtin` must NOT reference
`Gert.Service` -- it only sees the `IToolCard` interface, handed at call time (mirrors the existing
`IToolUi` ask_user seam, which already lets a tool emit mid-call keyed by `invocation.ToolCallId`).
Citations are reported WITHOUT a row id; the driver binds them to the row it allocates (as today).

### B2. ToolResult slims
`src/Gert.Tools/ToolResult.cs`: drop `Citations`, `Stdout`, `Todos`, `Artifacts`. Keep `Success`,
`ResultJson` (model-facing), `Error`. Sandbox's "failed but here is the payload" case (non-zero exit is
`Success=false` with a payload) is preserved via `ResultJson` populated on failure.

### B3. ITool -> AIFunction
- Define a Gert base, e.g. `GertTool : AIFunction`, carrying the descriptor metadata M.E.AI does not
  model on its own: `Id`, `Title`, `Icon`, `Group`, `Type` (Standard/Modal), `RequiresHuman`, `Bounds`.
  Put these in `AIFunction.AdditionalProperties` (advertise-time menu + the loop's dispatch axis).
- Schema token budget: M.E.AI default JSON-schema output is more verbose than Gert's `ToolSchema`
  (snake_case, compact, required-iff-non-nullable). qwen-class models break past ~1.8k tokens of tools
  block (chat-and-tools.md section tool specs; MEMORY vLLM note). Either keep `ToolSchema.Generate`
  feeding `AIFunction.JsonSchema`, or configure `AIJsonSchemaCreateOptions` to match snake_case +
  compactness. PROVE the rendered tools-block token count did not grow (add a test).
- Validation: the `ToolCall<TArgs,TResult>` base today runs registered `IValidator<TArgs>` via
  `IValidationProvider.Prove` and converts failures to model-correctable `Success=false` errors. M.E.AI's
  `AIFunctionFactory.Create` does its own binding but will NOT run FluentValidation. Preserve the
  validation hook explicitly in the `GertTool` invoke path (deserialize -> Prove -> CallAsync -> emit
  side-effects -> return model-facing JSON). The validation meta-test (every DTO has a validator) must
  stay green.
- Per-tool conversion (12 tools, all under `src/Gert.Tools.Builtin/Builtin/`):
  - Clean-ish (pure args -> result, minor stdout to relocate): `read_artifact`, `list_artifacts`,
    `clock` (needs `invocation.ClientTimezone` as a context value, not a model arg).
  - Self-emit side-effects: `rag`/`search`/`fetch` (citations), `make_artifact`/`edit_artifact`
    (artifacts + provenance from `invocation.ConversationId`/`MessageId`), `todo` (todos + it implements
    the cross-turn `IToolReminder`/tail-reminder -- preserve that), `sandbox` (stdout + failed-with-
    payload).
  - Stay custom subclasses (modal): `ask_user` (`IToolUi`, hand-parsed args, timeout-exempt, needs
    `ToolCallId`) and `sub_agent` (`IToolDelegate`, nested loop, timeout-exempt, needs
    `ModelId`/`AllowedToolIds`/`Deadline`). These remain hand-written `AIFunction`s.
- Invocation context (`ToolInvocation`: `Pid`, `ConversationId`, `MessageId`, `ToolCallId`, `Deadline`,
  `ClientTimezone`, `ModelId`, `AllowedToolIds`) is NOT model args. Pass it via `AIFunctionArguments`
  context/services or a captured per-call closure when constructing the per-turn `AITool` list.

### B4. Registration
`src/Gert.Tools.Builtin/ServiceCollectionExtensions.cs`: still register each tool and the id-only
`ToolRegistry` (its auth role -- `Contains`/`Normalize`/`AllIds` -- is unchanged). The per-turn projection
to `AITool`/`ChatOptions.Tools` happens in `Gert.Agent` (the `Toolset`), intersected with entitlement.

### B5. Collapse the dissection
Once tools self-emit, `ToolOutcome` (`src/Gert.Agent/ToolOutcome.cs`) and the
`ExecutedToolCall`-as-rich-carrier shrink. `ExecutedToolCall` keeps only what the driver still needs to
write the `tool_calls` row (`CallId`, `Kind`, `Status`, `RequestJson`, `ResponseJson`, `LatencyMs`); the
card/citation/artifact payloads now arrive via `IToolCard`. The `TurnRunner` tee's
`EmitToolCompletedAsync` per-payload switch correspondingly shrinks. This resolves the original
`ExecutedToolCall`-vs-`ToolResultEvent` duplication that started this whole thread.

### B6. Gate B
`make build && make test`. `AgentLoopTests` assertions about artifacts-on-completed-call and citation
provenance move to assert via the `IToolCard` seam (rewrite those cases). Architecture tests green
(the `IToolCard` impl is in `Gert.Agent`, the interface in `Gert.Tools`). Add the tools-block token-count
test (B3).

COMMIT: "Movement B: tools as AIFunctions, side-effects via host card seam".

---

## 4. Movement C: orchestration (FunctionInvokingChatClient)

Outcome: delete `AgentLoop`'s hand-rolled `while(true)` + `ExecuteRoundAsync`; the loop becomes a
`FunctionInvokingChatClient` subclass. This is the riskiest movement -- keep every `AgentLoopTests` case
as the gate. Depends on B (tools must already be `AIFunction`s for the middleware to invoke them).

### C1. The subclass
`GertFunctionInvokingChatClient : FunctionInvokingChatClient` (in `Gert.Agent`), configured with:
- `MaximumIterationsPerRequest` = `TurnOptions.MaxToolRounds` (the round cap).
- Override the per-function invocation (the `InvokeFunctionAsync` / `FunctionInvocationContext` hook --
  verify the exact name) to carry, IN THIS ORDER:
  1. Plan-time entitlement re-check against the snapshot. Unentitled call: feed a synthetic refusal
     result to the model BUT emit NO `ToolStarted`/`ToolCompleted` and write NO row (invisible -- this is
     a security control, auth.md; AgentLoop today does exactly this). Visibility suppression lives in the
     event-emission path keyed off entitlement.
  2. Per-tool call budget (`Toolset.TryConsumeCall`): over budget -> synthetic "budget exhausted" result,
     no invocation (chat-and-tools.md section per-tool bounds).
  3. Per-call timeout under `Effective.CallTimeout`, Modal tools exempt (`Type == Modal`), `<=0` disables;
     a trip is a visible card error, never a torn turn. Distinguish the call-timeout token from the turn
     token (only the latter rethrows).
  4. `BudgetedToolHost` per-call construction (surfaces the tool's effective `TokenBudget` on the host).
- Wind-down: there is no built-in. Override the per-iteration request construction so the final
  (cap) iteration clears `ChatOptions.Tools` (or sets `ChatToolMode.None` to preserve the vLLM prefix
  cache -- now viable since the vLLM-side issue is patched), giving the model one answer-only round; if
  it STILL emits calls, stop and finalize with streamed content (AgentLoop's current guard).

### C2. Events from M.E.AI
`AgentEvent`s now derive from two sources, adapted into the existing `IAgentEventSink`:
- Streaming `ChatResponseUpdate`s -> `TextDelta`, `ReasoningDelta`, and the LIVE-INTENT `ToolStarted`
  (id, kind, null-args) the moment a tool NAME first appears mid-argument-stream. `FunctionInvokingChatClient`
  only sees completed calls, so the early `ToolStarted` must come from a thin streaming interceptor in
  front of the inner client (or in the update->event adapter). Honor the two-emit-same-id dedup contract
  (the second `ToolStarted` carries parsed args at invocation time).
- The invocation override -> `ToolStarted` (with args) and `ToolCompleted`, plus `RoundCompleted` at each
  iteration boundary (`FunctionInvocationContext.Iteration`) for the streaming-row progress flush.
- `TurnFinished`/`AgentResult` metrics (`GenElapsedTicks` excluding tool time, last-round `TokenCount`,
  largest `PromptTokens`, `ToolRounds`) are custom accounting M.E.AI will not compute -- preserve the
  timing fold around the stream-consumption span.

### C3. Narration-rides-back (highest silent-regression risk)
A model that narrates while calling tools must see its own round text as the `content` of the assistant
tool-calls message next round (qwen quirk; chat-and-tools.md section round narration). M.E.AI normally
builds the assistant turn from the streamed response preserving text + `FunctionCallContent` in one
`ChatMessage` -- which would handle this for free -- BUT verify the OpenAI adapter + `SalvagingChatClient`
do not split text away from tool-call content. The dedicated test
`Round_narration_rides_back_in_the_assistant_tool_call_message` is the gate; do not let it regress.

### C4. Sub-agent path
`ChatToolDelegate` reuses the SAME orchestration (now the M.E.AI subclass) with: the delegable read-only
tool set intersected with the parent entitlement, an autonomous host (`ui: null`, `NoOpToolDelegate`,
`NotSupportedObjectResource`), `MaxRounds = 16`, and the discard sink (`NullAgentEventSink`). Preserve the
`CallTimeout = Zero` bounds transform for nested non-modal tools. Keep `Number`/`Events`/`Completion` on
`AgentRun` and the unbounded-channel `Agent` -- M.E.AI changes only what produces the events, not how they
are delivered to the tee.

### C5. Gate C
`make build && make test`. ALL of `AgentLoopTests` (the loop invariants) and `TurnRunnerTests` (tee +
finalizers + metrics) green. Pay special attention to: `Runaway_tool_loop_is_bounded...` (exact upstream
call count: 5 executed + 1 refused + 1 wind-down), `Capped_round_refuses_calls_with_synthetic_results...`,
the per-tool cap/override/disable trio, the timeout/Modal-exemption trio, the live-intent
`A_streamed_tool_name_announces_a_running_card...`, the entitlement-invisibility case, and the autonomous
discard-sink case.

COMMIT: "Movement C: orchestration on FunctionInvokingChatClient; delete the hand-rolled loop".

---

## 5. Movement D: docs, cleanup, final verification

### Docs (same change as the code that changed behavior -- CLAUDE.md docs-first)
- `docs/design/chat-and-tools.md`: rewrite the tool-loop, loop-structure ("split the noun"), per-tool
  bounds, tool-host, and tool-catalog sections to describe `IChatClient` + `FunctionInvokingChatClient` +
  `AIFunction` + the `IToolCard` self-emit seam. Update any `OpenAIStreamParser`/`ChatModelChunk`
  references to `SalvagingChatClient`/`ChatResponseUpdate`.
- `docs/design/tech-stack.md` (~line 13, Model API): the ports are now M.E.AI `IChatClient`/
  `IEmbeddingGenerator`; the OpenAI SDK rides underneath via `.AsIChatClient()`; vendor fields via
  `RawRepresentationFactory` + JsonPatch (not the hand-rolled request builder).
- `docs/design/decisions.md`: add Decision #13 (next number after #12) in the existing format
  (context paragraph; bold Decision; Rejected alternatives). Record: adopt M.E.AI; keep the provider
  catalog; keep `IChatModelClientBuilder` as the plugin seam; re-home salvage/reasoning/vendor-params
  into wrappers; the exact M.E.AI package versions pinned and whether the OpenAI adapter was preview.
- If the loop's per-tool/token-budget behavior or wind-down semantics shift, reflect it in
  `docs/design/turn-budgets.md`.
- Run `make check-links` (heading-anchor edits break links). Keep ASCII-only.

### Cleanup
- Delete dead files: `IChatModelClient.cs`, `IEmbeddingClient.cs`, `OpenAIChatRequestBuilder.cs`, the
  deleted wire DTOs, `ToolOutcome.cs` if fully subsumed, and any now-unused mappers.
- Grep for stragglers: `rg "IChatModelClient|ChatModelChunk|ChatCompletionRequest|IEmbeddingClient|ChatToolSpec|ChatModelMessage"`
  across `src/` and `tests/` -- should be empty.

### Final gate
`make build && make test && make lint && make check-links`. Then a smoke run:
`make serve-mock` (or `make smoke-auth`) to confirm a real turn streams, a tool card renders, citations
attach, and an artifact opens -- the M.E.AI path end-to-end. (There is a known live Gert dev host on
:5217; do NOT boot the smoke harness over it -- it wipes `.dev` data. Use a clean mock boot.)

COMMIT: "Movement D: docs + cleanup for the M.E.AI migration".

---

## 6. Invariants that must survive (do not weaken without reading the cited control)

- The user key comes only from the validated token; `pid` only ever joined under the token-derived
  folder (principles.md).
- "The claim is the ceiling": entitlement snapshot captured at plan time, re-checked per tool call --
  including the INVISIBLE synthetic refusal for an unentitled/hallucinated call (auth.md). This must move
  into the `FunctionInvokingChatClient` override, not be lost.
- Persist-then-publish: allocate seq -> append to `turn_events` -> publish to the bus. The streamer's
  splice depends on this order. The `IToolCard` impl and the event adapter must keep it.
- Fail-closed validation: every request/tool-arg DTO keeps its `IValidator<T>`; the reflection meta-test
  stays green. The tool-arg validation hook must be preserved through the `AIFunction` path (B3).
- Security findings F1-F12 keep their tests; do not weaken a control without reading its finding first.
- Architecture tests: `Gert.Agent` must not reference the host or any impl leaf; `Gert.Service` must not
  reference `Gert.Agent`; `Gert.Tools` (contracts) must not reference `Gert.Tools.Builtin` or
  `Gert.Service`; `Gert.Tools.Builtin` must not reference `Gert.Service`; `PluginArchitectureTests`
  contracts-vs-impl split. The `IToolCard` interface in `Gert.Tools` with impl in `Gert.Agent` respects
  all of these.
- Captive-dependency guard: any `IUserContext` consumer stays Scoped; `IAgentLoop`/`IAgent`/registries
  stay Singleton.

## 7. Risk register (top items, watch these)

1. Narration-rides-back silently regressing if the adapter splits text from tool-call content (C3).
   Gate: the dedicated test.
2. Live-intent tool-call-start card -- M.E.AI's middleware only sees completed calls; needs a streaming
   interceptor (C2). Gate: `A_streamed_tool_name_announces_a_running_card...`.
3. `<tool_call>` leak salvage -- entirely Gert-specific; must be reproduced in `SalvagingChatClient` (A3).
   Gate: ported stream-parser tests.
4. Tools-block token bloat from M.E.AI's verbose schema output breaking qwen tool calls (B3). Gate: a new
   token-count test; keep `ToolSchema` or tune `AIJsonSchemaCreateOptions`.
5. `Microsoft.Extensions.AI.OpenAI` preview/version drift (pre-flight 4). Accepted risk; record the pin.
6. Wind-down semantics have no built-in; needs the override + the "still-calling" guard (C1). Gate:
   `Runaway_tool_loop_is_bounded...` exact call count.
7. Vendor extra params: confirm `RawRepresentationFactory` + JsonPatch actually reach the wire for vLLM
   `chat_template_kwargs`/`reasoning_content` (A3). Gate: ported request-builder tests + a smoke check of
   the logged wire body via `OpenAIWireLogger` at Debug.

## 8. Sequencing summary

A (provider) -> gate+commit -> B (tools) -> gate+commit -> C (orchestration) -> gate+commit ->
D (docs+cleanup) -> final gate+commit. Each movement leaves the tree buildable and test-green. If a
movement cannot be made green, STOP and report rather than weakening a cited invariant or an
architecture/security test.
