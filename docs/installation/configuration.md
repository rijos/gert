# Configuring Gert

The operator's reference: every server-level knob, where it binds from, and a working
example for each. This is the **installation** view - the *design* of the configuration
cascade (server -> user -> project -> conversation) and the project model live in
[design/configuration.md](../design/configuration.md).

The `Gert:*` tree follows one rule everywhere: **functionality -> choose an implementation
(`Type`) -> configure it (`Parameters`)**. Cross-implementation knobs sit beside `Type`; the
connection / impl-private config (what changes when `Type` changes) lives under `Parameters`.
HTTP resilience (`RequestTimeoutSeconds`/`RetryCount`) is **per item that makes calls** - each
chat provider's `Parameters` and `Gert:Embeddings:Parameters` carry their own; there is no
shared HTTP section.

---

## 1. How configuration binds

Standard ASP.NET Core layering, last one wins:

```
appsettings.json  ->  appsettings.{Environment}.json  ->  environment variables  ->  command line
```

- **appsettings.json** (`src/Gert.Api/appsettings.json`) - non-secret defaults. This is
  where a deployment's static config lives.
- **Environment variables** - replace `:` with `__`:
  `Gert__Embeddings__Parameters__BaseUrl=http://vllm:8000`. Map entries by key:
  `Gert__Chat__Providers__qwen36-thinking__Parameters__Model=qwen36`.
- **Command line** - `dotnet run --project src/Gert.Api -- --Gert:Embeddings:Parameters:BaseUrl=http://vllm:8000`.
  Highest precedence; beats launch-profile environment variables too.

**Secrets never go in appsettings.json** (security F8). A bearer key, if your upstream
needs one, arrives via an environment variable or `dotnet user-secrets`. Chat keys are
**per provider** (`Gert:Chat:Providers:<slug>:Parameters:ApiKey`); the embeddings key is
`Gert:Embeddings:Parameters:ApiKey`:

```bash
dotnet user-secrets --project src/Gert.Api set "Gert:Chat:Providers:qwen36-thinking:Parameters:ApiKey" "sk-..."
dotnet user-secrets --project src/Gert.Api set "Gert:Embeddings:Parameters:ApiKey" "sk-..."
# or
export Gert__Chat__Providers__qwen36-thinking__Parameters__ApiKey=sk-...
export Gert__Embeddings__Parameters__ApiKey=sk-...
```

---

## 2. The complete `Gert` config tree

One annotated document of every section, with example (non-secret) values. Secrets are shown
only as env / user-secrets placeholders, never literals (F8). Per-type tables for each options
block follow in sections 3-7.

```jsonc
{
  "Gert": {
    "Database": {
      "Type": "Sqlite"                          // engine for the per-user/per-project databases;
      //   only "Sqlite" ships (default). See section 8.
      // "Parameters": { "DataRoot": "/db" }    // optional: own root for user.db + chat.db (else Storage:DataRoot)
    },
    "Rag": {
      "Type": "Sqlite"                          // vector/RAG index engine, decoupled from Database;
      //   only "Sqlite" (sqlite-vec + FTS5) ships. See section 8.
      // "Parameters": { "DataRoot": "/rag", "VecExtensionPath": "/opt/vec0.so" }   // both optional
    },
    "Chat": {
      // Provider catalog for the picker - a map keyed by provider slug (the GET /api/models
      // id). Empty -> one default "OpenAI" provider is synthesized from
      // Gert:Embeddings:Parameters:BaseUrl (single-vLLM zero-config boot). See section 4.
      "DefaultProvider": "qwen36-thinking",  // the picker's default; a slug, unset -> first entry
      "Providers": {
        "qwen36-thinking": {
          "Name": "Qwen 3.6 - thinking",
          "Type": "openai",                 // selects the chat-client impl (only "openai" today)
          "Capabilities": [ "tools", "vision" ],
          "Context": 131072,
          "Parameters": {
            "BaseUrl": "http://vllm-host:8000",   // server base, NO trailing /v1
            "Model": "qwen36",                     // upstream model id (sent as `model`)
            // ApiKey is a SECRET (F8) - env / user-secrets only, never here.
            "RequestTimeoutSeconds": 120,          // per-item pre-stream timeout (this provider)
            "RetryCount": 2,                       // per-item pre-stream retries (this provider)
            "Temperature": 0.6,
            "TopP": 0.95,
            "Extra": { "top_k": "20", "chat_template_kwargs.enable_thinking": "true" }
          }
        }
      }
    },
    "Embeddings": {
      "Type": "OpenAI",                       // only "OpenAI" ships; unknown fails fast at startup
      "Parameters": {
        "BaseUrl": "http://vllm-host:8000",   // embeddings upstream, NO trailing /v1
        // ApiKey is a SECRET (F8) - env / user-secrets only.
        "Model": "bge-m3",
        "Dimensions": 1024,
        "RequestTimeoutSeconds": 120,
        "RetryCount": 2
      }
    },
    "Tools": {
      "Search": {
        "Type": "SearXNG",                    // only "SearXNG" ships
        "FetchPages": false,                  // SSRF-exposed page fetch; off by default
        "MaxFetch": 3,
        "MaxFetchBytes": 2097152,
        "FetchTimeoutSeconds": 10,
        "MaxRedirects": 3,
        "SearchTimeoutSeconds": 15,
        "Parameters": { "BaseUrl": "http://localhost:8080" }
      },
      "Sandbox": {
        "Type": "Monty",                      // "Monty" (default) or "GVisor"; case-insensitive
        "WallClockSeconds": 10,               // cross-backend per-run caps sit beside Type
        "MemoryMiB": 256,
        "MaxOutputBytes": 65536,
        // Parameters is the per-backend bag. Monty: { BaseUrl, RequestTimeoutSeconds }.
        // GVisor: { RunscPath, Image, CpuSeconds, PidLimit, TmpSizeMiB, EgressEnabled }.
        "Parameters": { "BaseUrl": "http://localhost:8077" }
      }
    },
    "Extractor": {
      "Type": "Subprocess",                   // only "Subprocess" ships
      "Parameters": {
        "HelperPath": "gert-extract",
        "AddressSpaceMiB": 512,
        "CpuSeconds": 20,
        "ProcessLimit": 16,
        "WallClockSeconds": 30,
        "RunAsUid": 65534,
        "MaxDecompressedBytes": 67108864,
        "MaxZipEntries": 2048,
        "MaxOutputBytes": 16777216
      }
    }
  },
  "Auth": { "Authority": "https://id.example.com", "Audience": "gert-api" },
  "Storage": { "DataRoot": "/data", "ExpectedIssuer": "https://id.example.com" }
}
```

> **The `/v1` gotcha:** every base URL here - a provider's `Parameters:BaseUrl` and
> `Gert:Embeddings:Parameters:BaseUrl` - is the *server* base (`http://host:8000`), not the
> OpenAI API base (`http://host:8000/v1`). The adapter appends `/v1` itself - and tolerates a
> pasted `/v1` suffix (it is normalized, never doubled up).

---

## 3. `Gert:Embeddings` - the embeddings upstream

The embeddings functionality. `Type` selects the implementation (`OpenAI` only today - an
OpenAI-compatible `/v1/embeddings` upstream, vLLM serving bge-m3 in the reference deployment);
an unknown `Type` fails fast at startup. The connection + resilience live under `Parameters`
(`EmbeddingsParameters`, `src/Gert.Chat/OpenAI/EmbeddingsParameters.cs`).

`Gert:Embeddings:Parameters`:

| Key | Default | Required? | Secret? | Notes |
|-----|---------|-----------|---------|-------|
| `BaseUrl` | `http://localhost:8000` | no | no | Embeddings server base URL, **without** `/v1`. |
| `ApiKey` | *(unset)* | no | **yes (F8)** | The embeddings bearer key, sent as `Authorization: Bearer`. Env / user-secrets only. (Each chat provider carries its own key under `Gert:Chat:Providers:<slug>:Parameters:ApiKey`.) |
| `Model` | `bge-m3` | no | no | Sent as `model` on `/v1/embeddings`. Knowledge upload (RAG) fails if the upstream doesn't serve it. |
| `Dimensions` | `1024` | no | no | Must match the embedding model (bge-m3 = 1024). Effectively immutable once data exists - it bakes into every `rag.db` ([design section 4](../design/configuration.md)). |
| `RequestTimeoutSeconds` | `120` | no | no | Max wait, **per attempt**, for the upstream to *accept* a request (time to response headers). Per item: the embeddings path's own resilience. |
| `RetryCount` | `2` | no | no | Retries on transient pre-stream (connect/headers) failures. Embedding POSTs are idempotent, so retries are safe. `0` disables. |

> **vLLM prefix caching.** Gert's chat requests are built to be prefix-cache friendly:
> the system prompt is static per project, history is replayed verbatim, tool specs
> serialize deterministically, and no per-request unique fields are sent. To benefit,
> make sure the vLLM server has automatic prefix caching enabled - it is **on by
> default in the V1 engine**; verify with `--enable-prefix-caching` (and watch the
> `vllm:prefix_cache_hit_rate` metrics). Two caveats: prefix reuse is shared across
> all users of the server (fine for a single-tenant box; pass a per-tenant
> `cache_salt` upstream if you ever need isolation), and editing a project's pinned
> instructions invalidates every conversation prefix in that project.

---

## 4. `Gert:Chat:Providers` - the chat provider catalog

A **map keyed by provider slug** - each entry is one named chat preset behind Gert's
chat abstraction, and the slug is the `id` that `GET /api/models` publishes to the
picker (in configured/document order). Binds to `ChatProviderOptions`
(`src/Gert.Chat/Providers/ChatProviderOptions.cs`). When the section is absent or
empty, the catalog falls back to a **single default `openai` provider** built from
`Gert:Embeddings:Parameters:BaseUrl` (upstream model `default`), so the picker always has one
real option; an operator who configures `Gert:Chat:Providers` takes over completely.

**The picker's default** is `Gert:Chat:DefaultProvider` - a chat-level key (a sibling of
`Providers`) naming the default provider's slug. Exactly one provider is the default *by
construction*; **unset** falls back to the first entry in document order, and a value matching
no configured slug is a **startup error** that names the valid slugs.

The split is **catalog/capabilities on the entry, connection + sampling + per-item resilience
under `Parameters`:**

| Key | Required | Notes |
|-----|----------|-------|
| `Name` | no | Display name in the picker. Defaults to the slug. |
| `Type` | no | Selects the chat-client implementation. `"openai"` (default) is the only one implemented - an OpenAI-compatible / vLLM endpoint; the schema is open for others (`"anthropic"`, ...). |
| `Capabilities` | no | Capability tokens, shown as badges. `"tools"` is **load-bearing**: it gates tool calling. **Unset (null) means permissive** - the provider is assumed tool-capable; an explicit list *without* `"tools"` (e.g. `["text only"]`) disables tools for that provider. Other tokens (`"vision"`, ...) are display-only today. |
| `Context` | no | Context window in tokens - the "128K ctx" badge. vLLM reports it as `max_model_len` on `GET /v1/models`. Unset hides the badge. |
| `Fast` | no | Display-only "- fast" marker. |
| `Parameters` | yes | The `Type`-specific connection + sampling + resilience bag - see below. (The picker's `endpoint` hint is taken from `Parameters.BaseUrl`.) |

### 4a. `Parameters` - connection + sampling + resilience (the `openai` type)

Binds to `ChatProviderParameters` (`src/Gert.Chat/Providers/ChatProviderParameters.cs`).
The OpenAI REST-spec sampling fields are typed and **all optional - null omits the field**, so
the upstream's own default applies; everything *outside* the spec rides `Extra`.

| Key | Default | Required? | Secret? | Notes |
|-----|---------|-----------|---------|-------|
| `BaseUrl` | `http://localhost:8000` | no | no | Server base URL, **without** `/v1`. |
| `Model` | `default` | no | no | The upstream model id, sent as `model` on `/v1/chat/completions`. Must exist on the server (`GET <BaseUrl>/v1/models`). |
| `ApiKey` | *(unset)* | no | **yes (F8)** | Env / user-secrets only, **never appsettings.json**. Sent as `Authorization: Bearer`. Empty for a keyless vLLM. |
| `RequestTimeoutSeconds` | `120` | no | no | Per-item pre-stream timeout for **this provider** (time to response headers) - **not** the stream duration; the chat stream is bounded by the turn budget ([section 9](#9-gertturn---the-detached-turn-pipeline)). Each provider gets its own named HTTP client. |
| `RetryCount` | `2` | no | no | Per-item pre-stream retries for **this provider**. Safe for chat - a retried attempt means no tokens were streamed. `0` disables. |
| `Temperature` `TopP` `PresencePenalty` `FrequencyPenalty` `Seed` `Stop` | *(unset)* | no | no | Typed OpenAI-spec sampling. Each unset field is **omitted** from the request, so the upstream default stands. |
| `Extra` | `{}` | no | no | A **string->string map** for everything *outside* the OpenAI REST spec, keyed by JSON path under the request root (dotted, `$.` is prepended at apply time), value parsed to its JSON type. This is where the **vLLM extensions** (`top_k`, `min_p`, `repetition_penalty`) and the **template kwargs** (`chat_template_kwargs.enable_thinking`, `chat_template_kwargs.preserve_thinking`) live. |

### 4b. Thinking vs instruct is a *provider* choice

There is no per-request or per-conversation thinking toggle - **you pick a thinking
provider or an instruct provider.** The same physical model appears under several slugs
with different sampling. The canonical Qwen 3.6 pair:

```jsonc
"Chat": {
  "DefaultProvider": "qwen36-thinking",
  "Providers": {
    "qwen36-thinking": {
      "Name": "Qwen 3.6 - thinking",
      "Type": "openai",
      "Capabilities": [ "tools", "vision" ],
      "Context": 131072,
      "Parameters": {
        "BaseUrl": "http://vllm-host:8000",
        "Model": "qwen36",
        "Temperature": 0.6,
        "TopP": 0.95,
        "Extra": { "top_k": "20", "chat_template_kwargs.enable_thinking": "true" }
      }
    },
    "qwen36-instruct": {
      "Name": "Qwen 3.6 - instruct",
      "Type": "openai",
      "Capabilities": [ "tools", "vision" ],
      "Context": 131072,
      "Parameters": {
        "BaseUrl": "http://vllm-host:8000",
        "Model": "qwen36",                                  // same upstream model, different preset
        "Temperature": 0.7,
        "TopP": 0.8,
        "PresencePenalty": 1.5,
        "Extra": { "top_k": "20", "chat_template_kwargs.enable_thinking": "false" }
      }
    }
  }
}
```

`chat_template_kwargs.preserve_thinking` (interleaved thinking - replaying prior
`reasoning_content` upstream) is a third `Extra` flag, set on a thinking provider that
wants its earlier reasoning fed back. The **response-side** reasoning bubble streams and
persists regardless of provider ([chat-and-tools](../design/chat-and-tools.md#chat-orchestration-the-tool-loop)).

Filling in `Context` and `Capabilities` is on you, the operator - Gert binds this
statically at startup and never probes the upstream:

```bash
# context window:
curl -s http://vllm-host:8000/v1/models | jq '.data[] | {id, max_model_len}'
```

`/v1/models` does not advertise tools/vision, so set `Capabilities` from the model card
or your knowledge of the checkpoint (e.g. qwen36 serves both `tools` and `vision`).

Users pick from this catalog; they can never add endpoints or model ids of their own
([design section 4](../design/configuration.md#4-llm-providers--models)).

---

## 5. `Gert:Tools:Search` - SearXNG web search

The web-search tool backend. `Type` selects the implementation (`SearXNG` only today). The
fetch step (downloading result pages) is the SSRF-exposed part and is **off by default**. The
`web_fetch` tool shares this section's fetch caps (`MaxFetchBytes` / `FetchTimeoutSeconds` /
`MaxRedirects`) - same guarded fetcher, no parallel knob set; it needs no SearXNG instance and
ignores `Parameters:BaseUrl` / `FetchPages`. Binds to `SearXngOptions`
(`src/Gert.Tools/Search/SearXngOptions.cs`).

| Key | Default | Required? | Secret? | Notes |
|-----|---------|-----------|---------|-------|
| `Type` | `SearXNG` | no | no | The search implementation. |
| `FetchPages` | `false` | no | no | Fetch + summarize result pages (SSRF-guarded). |
| `MaxFetch` | `3` | no | no | Pages fetched per search when enabled. |
| `MaxFetchBytes` | `2097152` | no | no | Body-size cap per fetched page. Also bounds `web_fetch`. |
| `FetchTimeoutSeconds` | `10` | no | no | Wall-clock cap per page fetch. Also bounds `web_fetch`. |
| `MaxRedirects` | `3` | no | no | Each hop re-vetted by the SSRF guard. |
| `SearchTimeoutSeconds` | `15` | no | no | Total budget for the search API call, retries included; the HTTP client timeout sits 1 s above as a backstop. |
| `Parameters:BaseUrl` | `http://localhost:8080` | no | no | The SearXNG instance base URL (`SearXngParameters`). |

---

## 6. `Gert:Tools:Sandbox` - the `run_python` sandbox

The `run_python` sandbox backend. `Type` picks one of two implementations behind the one
`IPythonSandbox` port; an unknown value fails fast at startup. The cross-backend per-run caps
sit beside `Type`; the per-backend bag lives under `Parameters`. The defaults *are* the
security posture: egress off, no `/data` mount, hard caps. Raise them knowingly. Binds to
`PythonSandboxOptions` (`src/Gert.Tools/Sandbox/PythonSandboxOptions.cs`).

| Key | Default | Required? | Secret? | Notes |
|-----|---------|-----------|---------|-------|
| `Type` | `Monty` | no | no | `Monty` (Pydantic's Rust Python interpreter via the sidecar - no container infra) or `GVisor` (runsc container). Case-insensitive; unknown fails fast at startup. |
| `WallClockSeconds` | `10` | no | no | Kill timeout per run (both backends). |
| `MemoryMiB` | `256` | no | no | Memory limit (both backends). |
| `MaxOutputBytes` | `65536` | no | no | Captured stdout/stderr cap (both backends). |

### 6a. `Parameters` when `Type=Monty` - the monty sidecar

Binds to `MontyParameters` (`src/Gert.Tools/Sandbox/MontyParameters.cs`). Run the sidecar
from [tools/monty](../../tools/monty/README.md); it is reached server-side only.

| Key | Default | Required? | Secret? | Notes |
|-----|---------|-----------|---------|-------|
| `BaseUrl` | `http://localhost:8077` | no | no | Where the monty sidecar listens. |
| `RequestTimeoutSeconds` | `30` | no | no | HTTP backstop above the run's wall clock, for a hung sidecar. Must be strictly greater than `Gert:Tools:Sandbox:WallClockSeconds`; enforced at startup when the monty backend is selected. |

### 6b. `Parameters` when `Type=GVisor` - the runsc container

Binds to `GVisorParameters` (`src/Gert.Tools/Sandbox/GVisorParameters.cs`). gVisor-only; monty
has no processes, filesystem, or network to limit.

| Key | Default | Required? | Secret? | Notes |
|-----|---------|-----------|---------|-------|
| `RunscPath` | `runsc` | no | no | Path to the gVisor binary. |
| `Image` | `gert-sandbox-python` | no | no | OCI bundle with a Python runtime. |
| `CpuSeconds` | `5` | no | no | CPU-time limit. |
| `PidLimit` | `64` | no | no | Max processes/threads. |
| `TmpSizeMiB` | `32` | no | no | Writable `/tmp`; rootfs stays read-only. |
| `EgressEnabled` | `false` | no | no | Outbound network - the exfiltration brake. Leave off unless you must. |

---

## 7. `Gert:Extractor` - isolated document extraction

The document text-extractor functionality. `Type` selects the implementation (`Subprocess`
only today - the pdf/docx extractor runs as an unprivileged, rlimit-capped helper process).
Binds to `ExtractorOptions` (`src/Gert.Ingestion/ExtractorOptions.cs`); the caps live under
`Parameters` (`ExtractorParameters`).

`Gert:Extractor:Parameters`:

| Key | Default | Required? | Secret? | Notes |
|-----|---------|-----------|---------|-------|
| `HelperPath` | `gert-extract` | no | no | The helper executable. |
| `AddressSpaceMiB` | `512` | no | no | RLIMIT_AS cap. |
| `CpuSeconds` | `20` | no | no | RLIMIT_CPU cap. |
| `ProcessLimit` | `16` | no | no | RLIMIT_NPROC cap. |
| `WallClockSeconds` | `30` | no | no | Kill timeout backstopping RLIMIT_CPU. |
| `RunAsUid` | `65534` | no | no | Unprivileged uid the helper drops to. |
| `MaxDecompressedBytes` | `67108864` | no | no | DOCX zip-bomb cap (total decompressed). |
| `MaxZipEntries` | `2048` | no | no | DOCX zip-bomb cap (entry count). |
| `MaxOutputBytes` | `16777216` | no | no | Cap on emitted extracted text. |

---

## 8. `Auth` + `Storage` + `Gert:Database` + `Gert:Rag` - identity, the data root, and the engines

| Key | Notes |
|-----|-------|
| `Auth:Authority` | The OIDC issuer (Pocket ID). JWTs are validated against its JWKS, RS256 only. |
| `Auth:Audience` | Expected `aud` claim, e.g. `gert-api`. |
| `Storage:DataRoot` | Filesystem root holding the `users/` tree - the **shared default** for the object store and both SQLite engines. Everything Gert stores lives here unless an engine overrides its own root below; back up this directory (and any override roots) and you've backed up Gert. |
| `Storage:ExpectedIssuer` | Fail-closed `iss` assertion checked **before** any user folder is created (F12). Normally equal to `Auth:Authority`. |
| `Gert:Database:Type` | Which engine the per-user/per-project databases (`user.db`/`chat.db`) use. Only `Sqlite` ships (the default - per-user SQLite files); case-insensitive, and a value with no registered engine plugin fails fast at first use. A future server engine (`Postgres`) is selected here; its connection string is a **secret** (F8) - env / user-secrets, never appsettings. |
| `Gert:Database:Parameters:DataRoot` | *(SQLite only, optional)* Own filesystem root for `user.db` + `chat.db` (holding their `users/{key}/...` tree). Unset -> falls back to `Storage:DataRoot`. Set it to place the structured databases on their own volume. |
| `Gert:Rag:Type` | Which engine the per-project RAG/vector index uses - a **separate** capability from `Gert:Database` (a vector store need not be SQL). Only `Sqlite` ships (the default - per-project `rag.db` with sqlite-vec + FTS5). A dedicated vector store (e.g. `Qdrant`) is a sibling plugin selected here. |
| `Gert:Rag:Parameters:DataRoot` | *(SQLite only, optional)* Own filesystem root for `rag.db`. Unset -> falls back to `Storage:DataRoot`. Set it to place the vector index on its own (e.g. larger) volume, independent of the structured databases. |
| `Gert:Rag:Parameters:VecExtensionPath` | *(SQLite only, optional)* Path to the native **sqlite-vec** extension (`vec0.so`/`vec0.dll`). Unset -> the copy beside the running assembly (vendored). |

Both `Storage` values are **required** - the host refuses to start without them (unless *every*
SQLite engine sets its own `Parameters:DataRoot`, the object store still needs `Storage:DataRoot`).
`Gert:Database:Type` and `Gert:Rag:Type` are optional and default to `Sqlite`.

> **Database, RAG, and storage are independent stores.** `Gert:Database:Type` picks the engine
> for the structured data (`user.db`/`chat.db`), `Gert:Rag:Type` the vector/RAG index
> (`rag.db`), and the object store (uploads, memory bodies) is selected by which `AddGertStorage*`
> the build ships. By default all three sit under `Storage:DataRoot`; a SQLite engine can take its
> own root via `Gert:Database:Parameters:DataRoot` / `Gert:Rag:Parameters:DataRoot` (e.g. the
> vector index on a bigger disk). Deleting a user/project drops all three - each engine removing
> its own files - orchestrated by the service layer.

Dev-only escape hatch: `Gert:Dev:JwksPath` points at a local JWKS file so the test
harness can mint tokens offline. It is rejected in the Production environment; never
set it on a real deployment.

---

## 9. `Gert:Turn` - the detached turn pipeline

Binds to `TurnOptions` (`src/Gert.Service/Chat/TurnOptions.cs`). A **round** is one
upstream completion request that comes back with tool calls - executing them and
re-prompting starts the next round, so every round costs a full vLLM completion.

The guards are layered (the rationale and survey live in
[design/turn-budgets.md](../design/turn-budgets.md)): bound every part, brake the loop,
make every trip visible on its tool card.

| Key | Default | Notes |
|-----|---------|-------|
| `MaxTurnDuration` | `00:05:00` | Hard wall-clock cap on one turn (model rounds + tools) - the real budget. Doubles as the orphan horizon: a `streaming` row older than this reads as `error`. |
| `MaxConcurrentTurns` | `4` | Parallel turn lanes. Turns shard by (user, project, conversation): one conversation never runs concurrently with itself; different conversations may overlap. `1` restores the global serial worker. Must be >= 1 (validated at startup). |
| `MaxToolRounds` | `64` | **Runaway brake, not a work budget** - sized an order of magnitude above legitimate turns. Past it the runner refuses further calls with budget-exhausted errors (visible on the cards), winds down in one final round, and logs a warning. |
| `MaxSearchCallsPerTurn` | `5` | Per-turn cap on `web_search` calls - searches dominate tool-loop runaway (each costs a SearXNG round-trip and floods the prompt with results). Past it, further searches fail with a budget-exhausted error the model can read (visible on the card); the turn continues. `0` disables. |
| `MaxTokensPerRound` | `16384` | Per-round completion bound: the `max_tokens` sent on every upstream request. Reasoning tokens count against it on a thinking provider - keep it generous. `0` disables. |
| `ToolCallTimeout` | `00:01:00` | Generic wall-clock backstop on one tool execution, behind each tool's own tighter limits (sandbox wall clock, search timeouts). A trip fails that call with a visible card error; the turn continues. `0` disables. Interactive tools (`ask_user`) are exempt - see `AskUserTimeout`. |
| `AskUserTimeout` | `00:05:00` | How long one `ask_user` question waits for the user before the tool returns its graceful "user did not respond" result. The effective wait is min(this, remaining turn budget - 15 s grace), so it can never outlive `MaxTurnDuration`. |
| `DeltaFlushInterval` | `00:00:00.150` | Delta coalescing window - buffered model chunks emit as one event per window. `0` disables coalescing. |
| `DeltaFlushMaxChars` | `512` | Size backstop for the coalescing window. |

`MaxTokensPerRound` is the single per-round `max_tokens` for every turn - sampling is no
longer a user/conversation cascade (it rides the selected provider -
[section 4](#4-gertchatproviders---the-chat-provider-catalog)), so there is no per-model cogwheel to
override it.

---

## 10. `Artifacts` - served-artifact tickets

Binds to `ArtifactTicketOptions` (`src/Gert.Api/Security/ArtifactTicketOptions.cs`):
the separate-origin HTML-artifact preview and the HMAC-signed capability URLs it rides
(security F3). Top-level section, like `Auth`/`Storage`.

| Key | Default | Notes |
|-----|---------|-------|
| `Origin` | *(empty)* | The separate origin (`scheme://host[:port]`) that serves rendered HTML artifacts - a sandbox subdomain in prod, a second port in dev/CI. Empty means same origin: the ticket URL is relative and isolation rests on the iframe sandbox alone. |
| `Secret` | *(unset)* | **Secret** - env / user-secrets only. HMAC signing key for ticket URLs. An explicit value must be at least **32 UTF-8 bytes** (e.g. `openssl rand -base64 32`) - the host refuses to start on a shorter one. Unset = a random per-process key: fine for a single instance, but tickets stop surviving restarts and multiple instances behind a LB won't accept each other's tickets. |
| `Lifetime` | `00:05:00` | Ticket validity window. Long enough to load the iframe, short enough that a leaked URL is near-useless. |

---

## 11. `Gert:RateLimiting` - the per-user API limiter

Binds to `RateLimiting.PolicyOptions` (`src/Gert.Api/Security/RateLimiting.cs`),
security F10. A **fixed window per user**: each authenticated caller gets its own
partition keyed by the token `(iss, sub)` pair - the same identity anchor as the user
folder key, so two IdPs minting the same `sub` never share a bucket (anonymous traffic
falls back to the remote IP). This means
one client - or one stolen token - can't saturate the box, and one user's bursts never
throttle another's. Applied to `/api/*` only; `/healthz` is exempt. A rejected request
is a branded `429`. The defaults are a DoS brake, not a usage quota - leave the section
absent and nothing changes.

| Key | Default | Notes |
|-----|---------|-------|
| `PermitLimit` | `600` | Max requests per partition (per user / per anonymous IP) within one window. |
| `Window` | `00:01:00` | The fixed window length. |

---

## 12. Request size limits

Not knobs - compile-time constants, listed so the numbers are findable:

- **Document uploads are capped at 50 MiB** (`UploadConstraints.MaxSizeBytes`), enforced
  fail-closed by `DocumentUploadValidator` and re-checked on the streamed bytes, so an
  over-limit upload gets the branded 400.
- **Kestrel's request-body limit is set to that cap + 1 MiB** of multipart-framing
  headroom (`src/Gert.Api/Program.cs`), so a full-size file reaches the validator
  instead of dying as a bare Kestrel 413.

> **Reverse proxy:** a proxy in front needs a matching body-size setting - e.g. nginx
> `client_max_body_size 51m;` - or it will reject big uploads before Gert sees them.

---

## 13. Dev & test modes

Not for production - listed here so a deployment never enables them by accident.

| Switch | What it does |
|--------|--------------|
| `Gert:Dev:JwksPath` | Trust a local dev JWKS (offline token mint). Refused in Production. |
| `Gert:Web:TestHarness` | Serves the component-test harness pages from the SPA origin. Off unless explicitly `true`. |
| `make run` | Plain host against whatever appsettings says. |
| `make serve-mock` | Everything mocked (auth, vLLM, SearXNG) + a dev proxy that signs you in - no real upstreams needed. |

The `FakeE2E` launch profile (`src/Gert.Api/Properties/launchSettings.json`) is the
glue the harness uses: real adapters, mock URLs, dev JWKS. It is a Development-only
profile by construction.

---

## 14. `Logging` - verbosity

The standard .NET `Logging:LogLevel` config drives **Serilog** (the host logger): set the
floor with `Logging:LogLevel:Default`, and quiet individual categories with overrides.

```jsonc
"Logging": {
  "LogLevel": {
    "Default": "Information",          // Trace|Debug|Information|Warning|Error|Critical
    "Microsoft.AspNetCore": "Warning"  // keep framework noise down, even at Default=Debug
  }
}
```

Bind it like any other knob ([section 1](#1-how-configuration-binds)) - appsettings, the
`Logging__LogLevel__Default=Debug` env var, or a `--Logging:LogLevel:Default=Debug`
command-line override. Booting at `Debug` makes chat behaviour (the turn pipeline + the
HTTP traffic to the model upstream) visible, while the `Microsoft.AspNetCore` override
keeps Kestrel internals quiet. Levels map onto Serilog's
(`Trace`->`Verbose`, `Critical`->`Fatal`); the NDJSON `level` field is lower-cased
(`"level":"debug"`).

### Tracing the model wire (tuning)

At `Debug`, `OpenAIWireLogger` traces the **exact POST** Gert sends to the OpenAI-compatible
upstream - method, URI, headers (the api-key bearer **redacted**), and the full body
(messages, tools, sampling params, `chat_template_kwargs`, plus the SDK-injected
`model`/`stream`/`stream_options`), then the response status + headers. This is the seam for
tuning sampling and the tools block. Each trace is one NDJSON line tagged `OpenAI request:` /
`OpenAI response:` in `msg`, with the body also broken out as a `Body` field - so pull the
request bodies out cleanly with:

```bash
Logging__LogLevel__Default=Debug make run 2>&1 \
  | jq -r 'select(.msg | startswith("OpenAI request")) | .Body' | jq .
```

> WARNING: The body carries message **content**, so this is a **local tuning mode only** - never
> run the host at `Debug` in production ([operations section logging format](../design/operations.md#logging-format-shared)).
> The response body is the SSE stream and is left untraced so streaming isn't buffered.
